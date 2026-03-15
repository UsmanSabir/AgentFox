using AgentFox.Models;
using AgentFox.Agents;
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
    private readonly ILogger? _logger;
    
    public IReadOnlyDictionary<string, Channel> Channels => _channels;
    public ChannelMessageGateway? Gateway => _gateway;
    
    public ChannelManager(FoxAgent agent, ILogger? logger = null)
    {
        _agent = agent;
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
    /// Add a channel
    /// </summary>
    public void AddChannel(Channel channel)
    {
        _channels[channel.ChannelId] = channel;
        channel.OnMessageReceived += async (s, msg) => await HandleMessage(channel, msg);
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
                // Legacy direct execution mode
                _logger?.LogInformation("Processing channel message in legacy mode: {MessageId}", message.Id);
                var result = await _agent.ExecuteAsync(message.Content);
                await channel.SendMessageAsync(result.Output);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling channel message: {MessageId}", message.Id);
            try
            {
                await channel.SendMessageAsync($"❌ Error processing request: {ex.Message}");
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

/// <summary>
/// Telegram Channel Integration
/// </summary>
public class TelegramChannel : Channel
{
    private readonly string _botToken;
    private readonly long _chatId;
    private string? _webhookUrl;
    
    public TelegramChannel(string botToken, long? chatId = null)
    {
        Name = "Telegram";
        ChannelId = $"telegram_{botToken[..Math.Min(8, botToken.Length)]}";
        _botToken = botToken;
        _chatId = chatId ?? 0;
    }
    
    /// <summary>
    /// Set webhook for Telegram bot
    /// </summary>
    public async Task SetWebhookAsync(string url)
    {
        _webhookUrl = url;
        // In production, would call Telegram Bot API setWebhook
        await Task.Delay(50);
    }
    
    /// <summary>
    /// Get bot info
    /// </summary>
    public async Task<BotInfo> GetBotInfoAsync()
    {
        // In production, would call Telegram Bot API getMe
        await Task.Delay(50);
        return new BotInfo
        {
            Id = "12345",
            Name = "AgentFox Bot",
            Username = "agentfox_bot"
        };
    }
    
    public override async Task<bool> ConnectAsync()
    {
        try
        {
            // Simulated connection
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
        // In production, would send via Telegram Bot API
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
        // In production, would poll Telegram Bot API
        await Task.Delay(50);
        return new List<ChannelMessage>();
    }
}

/// <summary>
/// Telegram bot info
/// </summary>
public class BotInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}

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
