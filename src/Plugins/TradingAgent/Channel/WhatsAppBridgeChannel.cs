using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingAgent.Channel;

/// <summary>
/// Webhook-only channel for PSX trading signals arriving from a 3rd-party
/// WhatsApp bridge (e.g. WPPConnect, Baileys).
///
/// The bridge service connects to WhatsApp Web and POSTs each group message to:
///   POST /webhook/whatsapp-bridge
///
/// Expected payload:
/// {
///   "from":      "923001234567",
///   "group":     "PSX Signals",         // optional — used for GroupFilter
///   "body":      "BUY OGDC @ 165 ...",
///   "timestamp": "1736900000"            // unix epoch (optional)
/// }
///
/// Outbound messages (HITL approval prompts) are forwarded to CallbackUrl if set.
/// </summary>
public sealed class WhatsAppBridgeChannel : AgentFox.Plugins.Channels.Channel
{
    private readonly string? _callbackUrl;
    private readonly string? _groupFilter;
    private readonly ILogger _logger;
    private readonly bool _requireSignature;
    private readonly byte[]? _webhookSecret;
    private readonly int _maxClockSkewSeconds;
    private readonly IReadOnlySet<string> _allowedSenders;
    private readonly ConcurrentDictionary<string, DateTime> _seenMessageIds = new();
    private readonly HttpClient _http = new();

    public WhatsAppBridgeChannel(
        string? callbackUrl,
        string? groupFilter,
        ILogger logger,
        bool requireSignature = true,
        string? webhookSecret = null,
        int maxClockSkewSeconds = 120,
        IReadOnlySet<string>? allowedSenders = null)
    {
        Type = "whatsapp-bridge";
        Name = "whatsapp-bridge";
        ChannelId = "whatsapp-bridge";
        _callbackUrl = callbackUrl;
        _groupFilter = groupFilter;
        _logger = logger;
        _requireSignature = requireSignature;
        _webhookSecret = string.IsNullOrWhiteSpace(webhookSecret) ? null : Encoding.UTF8.GetBytes(webhookSecret);
        _maxClockSkewSeconds = Math.Clamp(maxClockSkewSeconds, 10, 3600);
        _allowedSenders = allowedSenders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public override Task<bool> ConnectAsync()
    {
        IsConnected = true;
        _logger.LogInformation(
            "[WhatsAppBridge] Ready. Inbound: POST /webhook/whatsapp-bridge" +
            (string.IsNullOrEmpty(_groupFilter) ? "" : $" | GroupFilter: {_groupFilter}") +
            (string.IsNullOrEmpty(_callbackUrl) ? " | Outbound: disabled" : $" | Outbound: {_callbackUrl}") +
            (_requireSignature ? " | Signature: required" : " | Signature: disabled"));

        if (_requireSignature && _webhookSecret is null)
            _logger.LogError(
                "[WhatsAppBridge] Signature verification is required but the configured secret environment variable is empty. All inbound webhooks will be rejected.");
        return Task.FromResult(true);
    }

    public override Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        var msg = new ChannelMessage { ChannelId = ChannelId, Content = content };

        if (string.IsNullOrEmpty(_callbackUrl))
        {
            _logger.LogDebug("[WhatsAppBridge] No CallbackUrl — outbound message dropped.");
            return msg;
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { text = content });
            using var response = await _http.PostAsync(
                _callbackUrl,
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("[WhatsAppBridge] Callback returned {Status}.", response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WhatsAppBridge] Failed to deliver outbound message.");
        }

        return msg;
    }

    public override Task<List<ChannelMessage>> ReceiveMessagesAsync()
        => Task.FromResult(new List<ChannelMessage>());

    public override Task<WebhookResult> ProcessWebhookAsync(
        string body,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default)
    {
        var authenticationError = ValidateAuthentication(body, headers);
        if (authenticationError is not null)
        {
            _logger.LogWarning("[WhatsAppBridge] Rejected webhook: {Reason}", authenticationError);
            return Task.FromResult(WebhookResult.Failed(authenticationError));
        }

        BridgePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BridgePayload>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[WhatsAppBridge] Invalid JSON payload.");
            return Task.FromResult(WebhookResult.Failed("Invalid JSON payload."));
        }

        if (string.IsNullOrWhiteSpace(payload?.Body))
            return Task.FromResult(WebhookResult.Failed("Missing or empty 'body' field."));

        if (string.IsNullOrWhiteSpace(payload.Id))
            return Task.FromResult(WebhookResult.Failed("Missing stable source message 'id'."));

        if (_allowedSenders.Count > 0 &&
            (string.IsNullOrWhiteSpace(payload.From) || !_allowedSenders.Contains(payload.From)))
            return Task.FromResult(WebhookResult.Failed("Sender is not authorized."));

        // Silently accept messages from non-matching groups — not an error
        if (!string.IsNullOrEmpty(_groupFilter) &&
            !string.Equals(payload.Group, _groupFilter, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(WebhookResult.Ok());
        }

        var ts = long.TryParse(payload.Timestamp, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : DateTime.UtcNow;

        EvictSeenMessageIds();
        if (!_seenMessageIds.TryAdd(payload.Id, DateTime.UtcNow))
            return Task.FromResult(WebhookResult.Failed("Replayed source message id."));

        var message = new ChannelMessage
        {
            Id        = payload.Id,
            ChannelId = ChannelId,
            SenderId  = payload.From ?? "unknown",
            Content   = payload.Body,
            Timestamp = ts,
            Metadata  = new Dictionary<string, string>
            {
                ["group"]  = payload.Group ?? "",
                ["source"] = "whatsapp-bridge"
            }
        };

        RaiseMessageReceived(message);
        return Task.FromResult(WebhookResult.Ok());
    }

    private sealed class BridgePayload
    {
        public string? Id        { get; set; }
        public string? From      { get; set; }
        public string? Group     { get; set; }
        public string? Body      { get; set; }
        public string? Timestamp { get; set; }
    }

    private string? ValidateAuthentication(
        string body,
        IReadOnlyDictionary<string, string> headers)
    {
        if (!_requireSignature) return null;
        if (_webhookSecret is null) return "Webhook signature secret is not configured.";
        if (!headers.TryGetValue("X-AgentFox-Timestamp", out var timestamp)
            || !long.TryParse(timestamp, out var unix))
            return "Missing or invalid X-AgentFox-Timestamp header.";

        var age = Math.Abs((DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix)).TotalSeconds);
        if (age > _maxClockSkewSeconds)
            return "Webhook timestamp is outside the permitted clock skew.";

        if (!headers.TryGetValue("X-AgentFox-Signature", out var supplied))
            return "Missing X-AgentFox-Signature header.";

        supplied = supplied.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? supplied[7..]
            : supplied;
        byte[] suppliedBytes;
        try { suppliedBytes = Convert.FromHexString(supplied); }
        catch (FormatException) { return "Invalid webhook signature format."; }

        var payload = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        var expected = HMACSHA256.HashData(_webhookSecret, payload);
        return suppliedBytes.Length == expected.Length
               && CryptographicOperations.FixedTimeEquals(suppliedBytes, expected)
            ? null
            : "Invalid webhook signature.";
    }

    private void EvictSeenMessageIds()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var item in _seenMessageIds)
            if (item.Value < cutoff)
                _seenMessageIds.TryRemove(item.Key, out _);
    }
}
