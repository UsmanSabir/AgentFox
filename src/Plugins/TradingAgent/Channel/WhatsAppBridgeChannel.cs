using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Logging;
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
    private readonly HttpClient _http = new();

    public WhatsAppBridgeChannel(string? callbackUrl, string? groupFilter, ILogger logger)
    {
        Type = "whatsapp-bridge";
        Name = "whatsapp-bridge";
        ChannelId = "whatsapp-bridge";
        _callbackUrl = callbackUrl;
        _groupFilter = groupFilter;
        _logger = logger;
    }

    public override Task<bool> ConnectAsync()
    {
        IsConnected = true;
        _logger.LogInformation(
            "[WhatsAppBridge] Ready. Inbound: POST /webhook/whatsapp-bridge" +
            (string.IsNullOrEmpty(_groupFilter) ? "" : $" | GroupFilter: {_groupFilter}") +
            (string.IsNullOrEmpty(_callbackUrl) ? " | Outbound: disabled" : $" | Outbound: {_callbackUrl}"));
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

        // Silently accept messages from non-matching groups — not an error
        if (!string.IsNullOrEmpty(_groupFilter) &&
            !string.Equals(payload.Group, _groupFilter, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(WebhookResult.Ok());
        }

        var ts = long.TryParse(payload.Timestamp, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : DateTime.UtcNow;

        var message = new ChannelMessage
        {
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
        public string? From      { get; set; }
        public string? Group     { get; set; }
        public string? Body      { get; set; }
        public string? Timestamp { get; set; }
    }
}
