using AgentFox.Models;
using AgentFox.Agents;
using AgentFox.Sessions;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;

namespace AgentFox.Channels;

/// <summary>
/// Base class for all channel integrations
/// </summary>
public abstract class Channel
{
    public string Name { get; protected set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public bool IsConnected { get; protected set; }
    
    /// <summary>
    /// Connect to the channel
    /// </summary>
    public abstract Task<bool> ConnectAsync();
    
    /// <summary>
    /// Disconnect from the channel
    /// </summary>
    public abstract Task DisconnectAsync();
    
    /// <summary>
    /// Send a message to the channel
    /// </summary>
    public abstract Task<ChannelMessage> SendMessageAsync(string content);
    
    /// <summary>
    /// Receive messages from the channel
    /// </summary>
    public abstract Task<List<ChannelMessage>> ReceiveMessagesAsync();
    
    /// <summary>
    /// Handle incoming message
    /// </summary>
    public event EventHandler<ChannelMessage>? OnMessageReceived;
    
    /// <summary>
    /// Raise message received event (for testing and internal use)
    /// </summary>
    public void RaiseMessageReceived(ChannelMessage message)
    {
        OnMessageReceived?.Invoke(this, message);
    }

    /// <summary>
    /// Send a reply in the context of an original message.
    /// Override this in multi-chat channels (e.g., Telegram) to route replies
    /// back to the correct chat. The default implementation delegates to SendMessageAsync.
    /// </summary>
    public virtual async Task SendReplyAsync(ChannelMessage originalMessage, string content)
    {
        await SendMessageAsync(content);
    }

    /// <summary>
    /// Proactively send a message to a specific target within this channel.
    /// For single-recipient channels (WhatsApp, Teams) targetId is ignored.
    /// For multi-chat channels (Telegram) targetId is the chat/user ID.
    /// For workspace channels (Slack, Discord) targetId is the channel/room name or ID.
    /// Default implementation delegates to SendMessageAsync (ignores targetId).
    /// </summary>
    public virtual async Task SendToTargetAsync(string targetId, string content)
    {
        await SendMessageAsync(content);
    }
}

/// <summary>
/// Channel message structure
/// </summary>
public class ChannelMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChannelId { get; set; } = string.Empty;
    public string SenderId { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public MessageType Type { get; set; } = MessageType.Text;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum MessageType
{
    Text,
    Image,
    File,
    Audio,
    Video,
    Location,
    Command
}

/// <summary>
/// Channel manager for handling multiple channel integrations
/// Supports both direct agent execution and gateway-based lane processing
/// </summary>
public class ChannelManager
{
    private readonly Dictionary<string, Channel> _channels = new();
    private readonly FoxAgent _agent;
    private ChannelMessageGateway? _gateway;
    private readonly SessionManager? _sessionManager;
    private readonly ICommandQueue? _commandQueue;
    private readonly ILogger? _logger;

    public IReadOnlyDictionary<string, Channel> Channels => _channels;
    public ChannelMessageGateway? Gateway => _gateway;

    /// <summary>
    /// Look up a registered channel by its human-readable name (case-insensitive).
    /// E.g., "telegram", "slack", "discord".
    /// Returns null if no matching channel is registered.
    /// </summary>
    public Channel? GetChannelByName(string name) =>
        _channels.Values.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ChannelManager(
        FoxAgent agent,
        SessionManager? sessionManager = null,
        ICommandQueue? commandQueue = null,
        ILogger? logger = null)
    {
        _agent = agent;
        _sessionManager = sessionManager;
        _commandQueue = commandQueue;
        _logger = logger;
    }
    
    /// <summary>
    /// Set the channel message gateway for lane-based processing
    /// When set, channel messages will be routed through the gateway instead of direct execution
    /// </summary>
    public void SetGateway(ChannelMessageGateway gateway)
    {
        _gateway = gateway;
        _logger?.LogInformation("ChannelMessageGateway set for channel manager");
    }
    
    /// <summary>
    /// Register a channel and subscribe to its incoming messages.
    /// The caller is responsible for connecting the channel first.
    /// </summary>
    public void AddChannel(Channel channel)
    {
        _channels[channel.ChannelId] = channel;
        channel.OnMessageReceived += async (s, msg) => await HandleMessage(channel, msg);
    }

    /// <summary>
    /// Connect a channel and register it live at runtime — no restart needed.
    /// Returns false if the connection attempt fails (channel is not added in that case).
    /// </summary>
    public async Task<bool> AddAndConnectAsync(Channel channel)
    {
        var connected = await channel.ConnectAsync();
        if (!connected)
        {
            _logger?.LogWarning("AddAndConnectAsync: could not connect channel '{Name}'", channel.Name);
            return false;
        }
        AddChannel(channel);
        _logger?.LogInformation("Channel '{Name}' added and connected at runtime", channel.Name);
        return true;
    }
    
    /// <summary>
    /// Remove a channel
    /// </summary>
    public async Task RemoveChannelAsync(string channelId)
    {
        if (_channels.TryGetValue(channelId, out var channel))
        {
            await channel.DisconnectAsync();
            _channels.Remove(channelId);
        }
    }
    
    /// <summary>
    /// Connect all channels
    /// </summary>
    public async Task ConnectAllAsync()
    {
        foreach (var channel in _channels.Values)
        {
            await channel.ConnectAsync();
        }
    }
    
    /// <summary>
    /// Disconnect all channels
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        foreach (var channel in _channels.Values)
        {
            await channel.DisconnectAsync();
        }
    }
    
    /// <summary>
    /// Handle incoming message from a channel
    /// Routes through gateway if available, otherwise direct execution (legacy mode)
    /// </summary>
    private async Task HandleMessage(Channel channel, ChannelMessage message)
    {
        try
        {
            if (_gateway != null)
            {
                // Gateway-based processing (lane system)
                var task = await _gateway.ProcessChannelMessageAsync(
                    message,
                    channel,
                    _agent.Id);
                
                _logger?.LogInformation(
                    "Channel message routed through gateway: MessageId={MessageId}, State={State}",
                    message.Id, task.State);
            }
            else
            {
                // No gateway — route through the command queue (Main lane) when available,
                // so channel turns share the same serial execution lane as interactive ones.
                _logger?.LogInformation("Processing channel message via queue: {MessageId}", message.Id);
                // Use message.ChannelId when set (allows per-chat sessions, e.g., telegram_{chatId}).
                // Fall back to channel.ChannelId for single-recipient channels.
                var sessionChannelId = string.IsNullOrEmpty(message.ChannelId)
                    ? channel.ChannelId
                    : message.ChannelId;
                var sessionId = _sessionManager?.GetOrCreateChannelSession(
                    sessionChannelId, channel.Name, _agent.Id)
                    ?? Guid.NewGuid().ToString("N");

                AgentResult result;
                if (_commandQueue != null)
                {
                    var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var cmd = AgentCommand.CreateMainCommand(sessionId, _agent.Id, message.Content);
                    cmd.ResultSource = tcs;
                    _commandQueue.Enqueue(cmd);
                    result = await tcs.Task;
                }
                else
                {
                    // Fallback: no queue configured (tests / embedded use)
                    result = await _agent.ProcessAsync(message.Content, sessionId);
                }

                await channel.SendReplyAsync(message, result.Output);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling channel message: {MessageId}", message.Id);
            try
            {
                await channel.SendReplyAsync(message, $"❌ Error processing request: {ex.Message}");
            }
            catch (Exception sendEx)
            {
                _logger?.LogError(sendEx, "Error sending error message to channel");
            }
        }
    }
}

/// <summary>
/// WhatsApp Channel Integration
/// </summary>
public class WhatsAppChannel : Channel
{
    private readonly string _phoneNumberId;
    private readonly string _accessToken;
    private readonly string _businessAccountId;
    private string? _qrCode;
    
    public WhatsAppChannel(string phoneNumberId, string accessToken, string businessAccountId)
    {
        Name = "WhatsApp";
        ChannelId = $"whatsapp_{phoneNumberId}";
        _phoneNumberId = phoneNumberId;
        _accessToken = accessToken;
        _businessAccountId = businessAccountId;
    }
    
    /// <summary>
    /// Generate QR code for WhatsApp pairing
    /// </summary>
    public string GeneratePairingQRCode()
    {
        // In production, this would call WhatsApp Business API
        _qrCode = $"https://api.whatsapp.com/generate-qr?phone={_phoneNumberId}";
        return _qrCode;
    }
    
    /// <summary>
    /// Get pairing status
    /// </summary>
    public PairingStatus GetPairingStatus()
    {
        return new PairingStatus
        {
            IsPaired = IsConnected,
            QRCode = _qrCode,
            PhoneNumberId = _phoneNumberId
        };
    }
    
    public override async Task<bool> ConnectAsync()
    {
        try
        {
            // Simulated connection - in production, would validate with WhatsApp API
            await Task.Delay(100);
            IsConnected = true;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }
    
    public override async Task DisconnectAsync()
    {
        IsConnected = false;
        await Task.CompletedTask;
    }
    
    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        // In production, would send via WhatsApp Business API
        await Task.Delay(50);
        return new ChannelMessage
        {
            ChannelId = ChannelId,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>
            {
                ["status"] = "sent"
            }
        };
    }
    
    public override async Task<List<ChannelMessage>> ReceiveMessagesAsync()
    {
        // In production, would poll WhatsApp Webhook API
        await Task.Delay(50);
        return new List<ChannelMessage>();
    }
}

/// <summary>
/// WhatsApp pairing status
/// </summary>
public class PairingStatus
{
    public bool IsPaired { get; set; }
    public string? QRCode { get; set; }
    public string? PhoneNumberId { get; set; }
    public DateTime? PairedAt { get; set; }
}

#region Telemgram

/// <summary>
/// Telegram Channel Integration — long-polling mode.
/// One bot instance handles messages from multiple chats; each chat gets its own session.
/// </summary>
public class TelegramChannel : Channel
{
    //review this https://github.com/TelegramBots/telegram.bot
    private readonly string _botToken;
    private readonly int _pollingTimeoutSeconds;
    private readonly HttpClient _http;
    private readonly ILogger? _logger;

    private long _updateOffset = 0;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    private string ApiBase => $"https://api.telegram.org/bot{_botToken}";

    public TelegramChannel(string botToken, int pollingTimeoutSeconds = 30, ILogger? logger = null)
    {
        Name = "Telegram";
        ChannelId = "telegram";
        _botToken = botToken;
        _pollingTimeoutSeconds = pollingTimeoutSeconds;
        _logger = logger;
        // HTTP timeout must exceed the long-poll window
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(pollingTimeoutSeconds + 15) };
    }

    /// <summary>
    /// Calls getMe to verify the bot token and retrieve bot identity.
    /// </summary>
    public async Task<BotInfo> GetBotInfoAsync()
    {
        var json = await _http.GetStringAsync($"{ApiBase}/getMe");
        var resp = JsonConvert.DeserializeObject<TgApiResponse<TgUser>>(json);
        if (resp?.Ok != true || resp.Result == null)
            throw new InvalidOperationException("Telegram getMe failed — check your bot token.");
        return new BotInfo
        {
            Id = resp.Result.Id.ToString(),
            Name = resp.Result.FirstName,
            Username = resp.Result.Username ?? string.Empty
        };
    }

    public override async Task<bool> ConnectAsync()
    {
        try
        {
            var info = await GetBotInfoAsync();
            _logger?.LogInformation("Telegram connected: @{Username} (id={Id})", info.Username, info.Id);
            IsConnected = true;
            _pollingCts = new CancellationTokenSource();
            _pollingTask = Task.Run(() => PollLoopAsync(_pollingCts.Token));
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Telegram ConnectAsync failed");
            IsConnected = false;
            return false;
        }
    }

    public override async Task DisconnectAsync()
    {
        _pollingCts?.Cancel();
        if (_pollingTask != null)
        {
            try { await _pollingTask; } catch { }
        }
        IsConnected = false;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"{ApiBase}/getUpdates?offset={_updateOffset}&timeout={_pollingTimeoutSeconds}&allowed_updates=[\"message\"]";
                var json = await _http.GetStringAsync(url, ct);
                var resp = JsonConvert.DeserializeObject<TgApiResponse<List<TgUpdate>>>(json);

                if (resp?.Ok == true && resp.Result != null)
                {
                    foreach (var update in resp.Result)
                    {
                        _updateOffset = update.UpdateId + 1;
                        HandleUpdate(update);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Telegram polling error; retrying in 5s");
                try { await Task.Delay(5_000, ct); } catch { }
            }
        }
    }

    private void HandleUpdate(TgUpdate update)
    {
        var msg = update.Message;
        if (msg == null || string.IsNullOrWhiteSpace(msg.Text)) return;

        var chatId = msg.Chat.Id;
        var incoming = new ChannelMessage
        {
            // Per-chat ChannelId so SessionManager creates one session per conversation
            ChannelId = $"telegram_{chatId}",
            SenderId = msg.From?.Id.ToString() ?? chatId.ToString(),
            SenderName = msg.From == null ? "Unknown"
                : $"{msg.From.FirstName} {msg.From.LastName}".Trim(),
            Content = msg.Text,
            Type = MessageType.Text,
            Metadata = new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(),
                ["message_id"] = msg.MessageId.ToString()
            }
        };
        RaiseMessageReceived(incoming);
    }

    /// <summary>
    /// Routes the reply back to the chat from which the original message came.
    /// Reads chat_id from originalMessage.Metadata set during HandleUpdate.
    /// </summary>
    public override async Task SendReplyAsync(ChannelMessage originalMessage, string content)
    {
        if (!originalMessage.Metadata.TryGetValue("chat_id", out var chatStr)
            || !long.TryParse(chatStr, out var chatId))
        {
            _logger?.LogWarning("SendReplyAsync: no chat_id in message metadata");
            return;
        }
        await SendToChatAsync(chatId, content);
    }

    /// <summary>
    /// Sends to the channel's default chat (no context). Most callers should use SendReplyAsync.
    /// </summary>
    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        _logger?.LogWarning("SendMessageAsync called without chat context on TelegramChannel");
        return new ChannelMessage { ChannelId = ChannelId, Content = content, Timestamp = DateTime.UtcNow };
    }

    public override Task<List<ChannelMessage>> ReceiveMessagesAsync() =>
        Task.FromResult(new List<ChannelMessage>()); // polling handled by background task

    /// <summary>
    /// Proactively send to a specific Telegram chat by its numeric chat ID.
    /// targetId must be a parseable long (e.g., "123456789" or "-100123456789" for groups).
    /// </summary>
    public override async Task SendToTargetAsync(string targetId, string content)
    {
        if (!long.TryParse(targetId, out var chatId))
        {
            _logger?.LogWarning("TelegramChannel.SendToTargetAsync: invalid chat_id '{TargetId}'", targetId);
            return;
        }
        await SendToChatAsync(chatId, content);
    }

    private async Task SendToChatAsync(long chatId, string text)
    {
        // Telegram max message size is 4096 chars
        const int maxLen = 4096;
        for (int i = 0; i < text.Length; i += maxLen)
        {
            var chunk = text.Substring(i, Math.Min(maxLen, text.Length - i));
            await PostMessageAsync(chatId, chunk, parseMode: "Markdown");
        }
    }

    private async Task PostMessageAsync(long chatId, string text, string? parseMode = null)
    {
        var payload = parseMode != null
            ? (object)new { chat_id = chatId, text, parse_mode = parseMode }
            : new { chat_id = chatId, text };

        var body = new StringContent(
            JsonConvert.SerializeObject(payload),
            System.Text.Encoding.UTF8,
            "application/json");

        var resp = await _http.PostAsync($"{ApiBase}/sendMessage", body);
        if (!resp.IsSuccessStatusCode)
        {
            var raw = await resp.Content.ReadAsStringAsync();
            _logger?.LogError("Telegram sendMessage failed ({Status}): {Body}", resp.StatusCode, raw);

            // Telegram rejects malformed Markdown — retry as plain text
            if (parseMode != null && raw.Contains("can't parse entities"))
                await PostMessageAsync(chatId, text, parseMode: null);
        }
    }
}

/// <summary>Telegram bot identity returned by getMe.</summary>
public class BotInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}

// ── Internal Telegram API response models ─────────────────────────────────────

internal class TgApiResponse<T>
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("result")] public T? Result { get; set; }
}

internal class TgUpdate
{
    [JsonProperty("update_id")] public long UpdateId { get; set; }
    [JsonProperty("message")] public TgMessage? Message { get; set; }
}

internal class TgMessage
{
    [JsonProperty("message_id")] public long MessageId { get; set; }
    [JsonProperty("from")] public TgUser? From { get; set; }
    [JsonProperty("chat")] public TgChat Chat { get; set; } = new();
    [JsonProperty("text")] public string? Text { get; set; }
}

internal class TgChat
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("type")] public string Type { get; set; } = string.Empty;
}

internal class TgUser
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("first_name")] public string FirstName { get; set; } = string.Empty;
    [JsonProperty("last_name")] public string? LastName { get; set; }
    [JsonProperty("username")] public string? Username { get; set; }
}

#endregion

/// <summary>
/// Microsoft Teams Channel Integration
/// </summary>
public class TeamsChannel : Channel
{
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _serviceUrl;
    private string? _accessToken;
    
    public TeamsChannel(string tenantId, string clientId, string clientSecret, string serviceUrl)
    {
        Name = "Microsoft Teams";
        ChannelId = $"teams_{tenantId}";
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _serviceUrl = serviceUrl;
    }
    
    /// <summary>
    /// Register bot with Microsoft Teams
    /// </summary>
    public async Task RegisterBotAsync(string botId, string botSecret)
    {
        // In production, would register with Azure AD and Teams
        await Task.Delay(100);
    }
    
    /// <summary>
    /// Send proactive message to user
    /// </summary>
    public async Task SendProactiveMessageAsync(string conversationId, string content)
    {
        // In production, would use Microsoft Bot Framework
        await Task.Delay(50);
    }
    
    /// <summary>
    /// Create meeting
    /// </summary>
    public async Task<string> CreateMeetingAsync(string subject, DateTime startTime, DateTime endTime)
    {
        // In production, would use Microsoft Graph API
        await Task.Delay(100);
        return Guid.NewGuid().ToString();
    }
    
    public override async Task<bool> ConnectAsync()
    {
        try
        {
            // Simulated OAuth flow
            await Task.Delay(100);
            _accessToken = "simulated_token";
            IsConnected = true;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }
    
    public override async Task DisconnectAsync()
    {
        _accessToken = null;
        IsConnected = false;
        await Task.CompletedTask;
    }
    
    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        // In production, would send via Microsoft Bot Framework
        await Task.Delay(50);
        return new ChannelMessage
        {
            ChannelId = ChannelId,
            Content = content,
            Timestamp = DateTime.UtcNow
        };
    }
    
    public override async Task<List<ChannelMessage>> ReceiveMessagesAsync()
    {
        // In production, would poll Microsoft Bot Framework
        await Task.Delay(50);
        return new List<ChannelMessage>();
    }
}

/// <summary>
/// Slack Channel Integration (bonus)
/// </summary>
public class SlackChannel : Channel
{
    private readonly string _botToken;
    private readonly string _signingSecret;
    private readonly string _appToken;
    
    public SlackChannel(string botToken, string signingSecret, string? appToken = null)
    {
        Name = "Slack";
        ChannelId = $"slack_{botToken[..Math.Min(8, botToken.Length)]}";
        _botToken = botToken;
        _signingSecret = signingSecret;
        _appToken = appToken;
    }
    
    /// <summary>
    /// Set up Slack webhook
    /// </summary>
    public async Task SetupWebhookAsync(string url)
    {
        // In production, would configure Slack app
        await Task.Delay(50);
    }
    
    /// <summary>
    /// Post to channel
    /// </summary>
    public async Task PostToChannelAsync(string channel, string content, MessageAttachment? attachment = null)
    {
        // In production, would use Slack Web API
        await Task.Delay(50);
    }
    
    public override async Task<bool> ConnectAsync()
    {
        try
        {
            await Task.Delay(100);
            IsConnected = true;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }
    
    public override async Task DisconnectAsync()
    {
        IsConnected = false;
        await Task.CompletedTask;
    }
    
    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        await Task.Delay(50);
        return new ChannelMessage
        {
            ChannelId = ChannelId,
            Content = content,
            Timestamp = DateTime.UtcNow
        };
    }
    
    public override async Task<List<ChannelMessage>> ReceiveMessagesAsync()
    {
        await Task.Delay(50);
        return new List<ChannelMessage>();
    }
}

/// <summary>
/// Slack message attachment
/// </summary>
public class MessageAttachment
{
    public string? Title { get; set; }
    public string? Text { get; set; }
    public string? Color { get; set; }
    public List<Field>? Fields { get; set; }
}

public class Field
{
    public string Title { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsShort { get; set; } = true;
}
