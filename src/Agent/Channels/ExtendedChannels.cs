using System.Diagnostics;
using AgentFox.Models;
using Discord;
using Discord.WebSocket;
using Discord.Webhook;

namespace AgentFox.Channels;

/// <summary>
/// Discord Channel Integration using Discord.Net
/// </summary>
public class DiscordChannel : Channel
{
    private readonly string _botToken;
    private readonly ulong _guildId;
    private readonly ulong _channelId;
    private DiscordSocketClient? _client;
    private SocketTextChannel? _textChannel;
    private DiscordWebhookClient? _webhookClient;
    private readonly List<ChannelMessage> _receivedMessages = new();
    private bool _stopReconnecting = false;
    private bool _reconnecting = false;
    
    public DiscordChannel(string botToken, ulong guildId, ulong channelId)
    {
        Name = "Discord";
        ChannelId = $"discord_{guildId}_{channelId}";
        _botToken = botToken;
        _guildId = guildId;
        _channelId = channelId;
    }
    
    /// <summary>
    /// Set up Discord webhook for message sending
    /// </summary>
    public async Task SetWebhookAsync(string webhookUrl)
    {
        try
        {
            _webhookClient = new DiscordWebhookClient(webhookUrl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to set webhook URL: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Send an embed message to Discord
    /// </summary>
    public async Task SendEmbedAsync(EmbedBuilder embed)
    {
        try
        {
            if (!IsConnected || _textChannel == null)
                throw new InvalidOperationException("Discord channel is not connected");

            await _textChannel.SendMessageAsync(embed: embed.Build());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to send embed: {ex.Message}", ex);
        }
    }
    
    /// <summary>
    /// Add reaction to a message
    /// </summary>
    public async Task AddReactionAsync(ulong messageId, IEmote emoji)
    {
        try
        {
            if (!IsConnected || _textChannel == null)
                throw new InvalidOperationException("Discord channel is not connected");

            var message = await _textChannel.GetMessageAsync(messageId);
            if (message is IUserMessage userMessage)
            {
                await userMessage.AddReactionAsync(emoji);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to add reaction: {ex.Message}", ex);
        }
    }
    
    public override async Task<bool> ConnectAsync()
    {
        _stopReconnecting = false;
        return await ConnectInternalAsync();
    }

    private async Task<bool> ConnectInternalAsync()
    {
        try
        {
            // Dispose any previous client cleanly
            if (_client != null)
            {
                _client.MessageReceived -= HandleMessageReceivedAsync;
                _client.Disconnected   -= OnDisconnectedAsync;
                _client.Ready          -= OnClientReady;
                try { await _client.StopAsync(); } catch { /* ignore */ }
                _client.Dispose();
                _client = null;
            }

            // Request MessageContent privileged intent so message.Content is populated.
            // NOTE: You must also enable "Message Content Intent" in the Discord Developer
            //       Portal under your bot's settings (Bot → Privileged Gateway Intents).
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            };

            _client = new DiscordSocketClient(config);
            
            // Hook up logging, disconnect handler, and ready handler.
            // IsConnected is set to true inside OnClientReady, AFTER the guild
            // and text channel have been successfully resolved from the cache.
            _client.Log          += LogAsync;
            _client.Disconnected += OnDisconnectedAsync;
            _client.Ready        += OnClientReady;

            // Login and start
            await _client.LoginAsync(TokenType.Bot, _botToken);
            await _client.StartAsync();
            
            // Wait for the socket to reach "Connected" state.
            // Note: IsConnected (our flag) is set later inside OnClientReady.
            int maxWaitTime = 30000; // 30 seconds
            int elapsed = 0;
            while (!_client.ConnectionState.Equals(ConnectionState.Connected) && elapsed < maxWaitTime)
            {
                await Task.Delay(100);
                elapsed += 100;
            }
            
            if (!_client.ConnectionState.Equals(ConnectionState.Connected))
            {
                IsConnected = false;
                return false;
            }

            // Guild/channel resolution and IsConnected=true happen in OnClientReady.
            // Return true to indicate the socket handshake succeeded; the caller should
            // treat the channel as fully ready once IsConnected becomes true.
            _reconnecting = false;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    private Task OnClientReady()
    {
        // The Ready event guarantees the guild cache is fully populated — safe to resolve now.
        var guild = _client?.GetGuild(_guildId);
        if (guild == null)
        {
            IsConnected = false;
            Debug.WriteLine($"[Discord] OnClientReady: guild {_guildId} not found in cache.");
            return Task.CompletedTask; // don't subscribe MessageReceived in a broken state
        }

        _textChannel = guild.GetTextChannel(_channelId);
        if (_textChannel == null)
        {
            // This skips the cache and asks Discord's servers directly
            //_textChannel = await _client?.Rest?.GetChannelAsync(_channelId)! as ITextChannel;

            IsConnected = false;
            Debug.WriteLine($"[Discord] OnClientReady: text channel {_channelId} not found in guild {_guildId}.");
            return Task.CompletedTask; // don't subscribe MessageReceived in a broken state
        }

        // Guard against double-subscribe: Ready can fire more than once within a
        // single Discord.Net session (e.g. after a gateway resume).
        if (_client != null)
        {
            _client.MessageReceived -= HandleMessageReceivedAsync;
            _client.MessageReceived += HandleMessageReceivedAsync;
        }

        IsConnected = true;
        Debug.WriteLine($"[Discord] Ready — guild '{guild.Name}', channel '#{_textChannel.Name}'.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Triggered by Discord.Net whenever the socket drops. Attempts reconnection
    /// with exponential backoff (5 s → 10 s → 20 s → 40 s → 80 s → 120 s max).
    /// </summary>
    private Task OnDisconnectedAsync(Exception? ex)
    {
        if (_stopReconnecting || _reconnecting)
            return Task.CompletedTask;

        _reconnecting = true;
        IsConnected = false;

        _ = Task.Run(async () =>
        {
            int delayMs = 5000; // start at 5 s
            const int maxDelayMs = 120_000; // cap at 2 min

            while (!_stopReconnecting)
            {
                Debug.WriteLine($"[Discord] Disconnected. Reconnecting in {delayMs / 1000} s…");
                await Task.Delay(delayMs);

                if (_stopReconnecting)
                    break;

                var success = await ConnectInternalAsync();
                if (success)
                {
                    Debug.WriteLine("[Discord] Reconnected successfully.");
                    break;
                }

                // Exponential backoff
                delayMs = Math.Min(delayMs * 2, maxDelayMs);
            }

            _reconnecting = false;
        });

        return Task.CompletedTask;
    }
    
    public override async Task DisconnectAsync()
    {
        // Signal the reconnect loop to stop before we tear down the client
        _stopReconnecting = true;

        try
        {
            if (_client != null)
            {
                _client.MessageReceived -= HandleMessageReceivedAsync;
                _client.Disconnected   -= OnDisconnectedAsync;
                _client.Ready -= OnClientReady;

                await _client.LogoutAsync();
                await _client.StopAsync();
                _client.Dispose();
                _client = null;
            }
            
            if (_webhookClient != null)
            {
                _webhookClient.Dispose();
                _webhookClient = null;
            }
            
            IsConnected = false;
        }
        catch
        {
            // Ensure disconnection happens
            IsConnected = false;
        }
    }
    
    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        try
        {
            if (!IsConnected || _textChannel == null)
                throw new InvalidOperationException("Discord channel is not connected");

            if(string.IsNullOrWhiteSpace(content))
                content = "[Empty Response]";

            // Split long messages (Discord has a 2000 character limit)
            const int maxLength = 2000;
            var messages = SplitMessage(content, maxLength);
            
            IMessage? lastMessage = null;
            foreach (var msg in messages)
            {
                lastMessage = await _textChannel.SendMessageAsync(msg);
            }
            
            if (lastMessage == null)
                throw new InvalidOperationException("Failed to send message");

            return new ChannelMessage
            {
                Id = lastMessage.Id.ToString(),
                ChannelId = ChannelId,
                SenderId = _client!.CurrentUser.Id.ToString(),
                SenderName = _client.CurrentUser.Username,
                Content = content,
                Timestamp = DateTime.UtcNow,
                Type = MessageType.Text
            };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to send message: {ex.Message}", ex);
        }
    }
    
    public override async Task<List<ChannelMessage>> ReceiveMessagesAsync()
    {
        try
        {
            if (!IsConnected || _textChannel == null)
                return new List<ChannelMessage>();

            var messages = new List<ChannelMessage>();
            
            // Fetch recent messages from the channel
            var discordMessages = await _textChannel.GetMessagesAsync(limit: 10).FlattenAsync();
            
            foreach (var msg in discordMessages.OrderBy(m => m.Timestamp))
            {
                if (msg.Author.IsBot && msg.Author.Id == _client!.CurrentUser.Id)
                    continue; // Skip own messages
                
                messages.Add(new ChannelMessage
                {
                    Id = msg.Id.ToString(),
                    ChannelId = ChannelId,
                    SenderId = msg.Author.Id.ToString(),
                    SenderName = msg.Author.Username,
                    Content = msg.Content,
                    Timestamp = msg.Timestamp.UtcDateTime,
                    Type = MessageType.Text,
                    Metadata = new Dictionary<string, string>
                    {
                        { "messageId", msg.Id.ToString() },
                        { "authorId", msg.Author.Id.ToString() }
                    }
                });
            }
            
            return messages;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to receive messages: {ex.Message}", ex);
        }
    }
    
    private async Task HandleMessageReceivedAsync(SocketMessage message)
    {
        try
        {
            // Ignore system messages and messages from the bot itself.
            // Cast to SocketUserMessage to access user-sent content.
            if (message.Author.IsBot || message is not SocketUserMessage userMessage)
                return;
            
            // Only process messages from our channel
            if (userMessage.Channel.Id != _channelId)
                return;

            // message.Content is populated only when the MessageContent privileged
            // gateway intent is enabled (set in ConnectInternalAsync via DiscordSocketConfig
            // and in the Discord Developer Portal under Bot → Privileged Gateway Intents).
            var content = userMessage.Content;
            
            var channelMessage = new ChannelMessage
            {
                Id = userMessage.Id.ToString(),
                ChannelId = ChannelId,
                SenderId = userMessage.Author.Id.ToString(),
                SenderName = userMessage.Author.Username,
                Content = content,
                Timestamp = userMessage.Timestamp.UtcDateTime,
                Type = MessageType.Text,
                Metadata = new Dictionary<string, string>
                {
                    { "messageId", userMessage.Id.ToString() },
                    { "authorId",  userMessage.Author.Id.ToString() }
                }
            };
            
            _receivedMessages.Add(channelMessage);
            RaiseMessageReceived(channelMessage);
        }
        catch
        {
            // Log but don't crash on message handling errors
        }
    }
    
    private static Task LogAsync(LogMessage message)
    {
        // You can implement logging here
        // Console.WriteLine($"[{message.Severity}] {message.Source}: {message.Message}");
        Debug.WriteLine($"[{message.Severity}] {message.Source}: {message.Message}");
        return Task.CompletedTask;
    }
    
    private static List<string> SplitMessage(string message, int maxLength)
    {
        var messages = new List<string>();
        if (message.Length <= maxLength)
        {
            messages.Add(message);
        }
        else
        {
            int index = 0;
            while (index < message.Length)
            {
                int length = Math.Min(maxLength, message.Length - index);
                messages.Add(message.Substring(index, length));
                index += length;
            }
        }
        return messages;
    }
}


/// <summary>
/// Helper extension methods for Discord embeds
/// </summary>
public static class DiscordEmbedExtensions
{
    /// <summary>
    /// Create an embed from field list
    /// </summary>
    public static EmbedBuilder CreateEmbed(string title, string? description = null, uint? color = null)
    {
        var embed = new EmbedBuilder()
            .WithTitle(title);
        
        if (!string.IsNullOrEmpty(description))
            embed.WithDescription(description);
        
        if (color.HasValue)
            embed.WithColor(new Color(color.Value));
        
        return embed;
    }
}

/// <summary>
/// SMS Channel (Twilio, etc.)
/// </summary>
public class SMSChannel : Channel
{
    private readonly string _accountSid;
    private readonly string _authToken;
    private readonly string _fromNumber;
    
    public SMSChannel(string accountSid, string authToken, string fromNumber)
    {
        Name = "SMS";
        ChannelId = $"sms_{fromNumber}";
        _accountSid = accountSid;
        _authToken = authToken;
        _fromNumber = fromNumber;
    }
    
    /// <summary>
    /// Send SMS to phone number
    /// </summary>
    public async Task<string> SendSMSAsync(string to, string message)
    {
        // In production, would use Twilio API
        await Task.Delay(100);
        return $"SMS sent to {to}: {message}";
    }
    
    /// <summary>
    /// Verify phone number
    /// </summary>
    public async Task<bool> VerifyPhoneAsync(string phoneNumber)
    {
        await Task.Delay(100);
        return true;
    }
    
    public override async Task<bool> ConnectAsync()
    {
        await Task.Delay(100);
        IsConnected = true;
        return true;
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
/// Email Channel (SMTP)
/// </summary>
public class EmailChannel : Channel
{
    private readonly string _smtpHost;
    private readonly int _smtpPort;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromEmail;
    
    public EmailChannel(string smtpHost, int smtpPort, string username, string password, string fromEmail)
    {
        Name = "Email";
        ChannelId = $"email_{fromEmail}";
        _smtpHost = smtpHost;
        _smtpPort = smtpPort;
        _username = username;
        _password = password;
        _fromEmail = fromEmail;
    }
    
    /// <summary>
    /// Send email
    /// </summary>
    public async Task SendEmailAsync(string to, string subject, string body, bool isHtml = false)
    {
        // In production, would use System.Net.Mail
        await Task.Delay(100);
    }
    
    /// <summary>
    /// Send email with attachment
    /// </summary>
    public async Task SendEmailWithAttachmentAsync(string to, string subject, string body, string attachmentPath)
    {
        await Task.Delay(100);
    }
    
    public override async Task<bool> ConnectAsync()
    {
        try
        {
            // Would verify SMTP connection
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
/// WebSocket Channel for real-time communication
/// </summary>
public class WebSocketChannel : Channel
{
    private readonly string _wsUrl;
    private List<(string, string)> _subscribedEvents = new();
    
    public WebSocketChannel(string wsUrl)
    {
        Name = "WebSocket";
        ChannelId = $"ws_{wsUrl.GetHashCode()}";
        _wsUrl = wsUrl;
    }
    
    /// <summary>
    /// Subscribe to event
    /// </summary>
    public void Subscribe(string eventName, string filter = "")
    {
        _subscribedEvents.Add((eventName, filter));
    }
    
    /// <summary>
    /// Unsubscribe from event
    /// </summary>
    public void Unsubscribe(string eventName)
    {
        _subscribedEvents.RemoveAll(e => e.Item1 == eventName);
    }
    
    public override async Task<bool> ConnectAsync()
    {
        // Would establish WebSocket connection
        await Task.Delay(100);
        IsConnected = true;
        return true;
    }
    
    public override async Task DisconnectAsync()
    {
        // Would close WebSocket connection
        IsConnected = false;
        await Task.CompletedTask;
    }
    
    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        // Would send via WebSocket
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
        // Would receive from WebSocket
        await Task.Delay(50);
        return new List<ChannelMessage>();
    }
}

/// <summary>
/// RSS/Atom Feed Channel
/// </summary>
public class RSSChannel : Channel
{
    private readonly string _feedUrl;
    private DateTime _lastCheck = DateTime.MinValue;
    
    public RSSChannel(string feedUrl)
    {
        Name = "RSS";
        ChannelId = $"rss_{feedUrl.GetHashCode()}";
        _feedUrl = feedUrl;
    }
    
    /// <summary>
    /// Check for new items
    /// </summary>
    public async Task<List<RSSItem>> CheckForNewItemsAsync()
    {
        await Task.Delay(100);
        _lastCheck = DateTime.UtcNow;
        return new List<RSSItem>();
    }
    
    /// <summary>
    /// Get all items
    /// </summary>
    public async Task<List<RSSItem>> GetAllItemsAsync()
    {
        await Task.Delay(100);
        return new List<RSSItem>();
    }
    
    public override async Task<bool> ConnectAsync()
    {
        await Task.Delay(100);
        IsConnected = true;
        return true;
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
        var items = await CheckForNewItemsAsync();
        return items.Select(i => new ChannelMessage
        {
            ChannelId = ChannelId,
            Content = $"{i.Title}: {i.Description}",
            Timestamp = i.PublishedDate,
            Metadata = new Dictionary<string, string> { ["link"] = i.Link }
        }).ToList();
    }
}

public class RSSItem
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
    public string? Author { get; set; }
    public string? Category { get; set; }
}

/// <summary>
/// Webhook Channel for generic HTTP callbacks
/// </summary>
public class WebhookChannel : Channel
{
    private readonly string _webhookUrl;
    private readonly Dictionary<string, Func<ChannelMessage, Task<string>>> _handlers = new();
    
    public WebhookChannel(string webhookUrl)
    {
        Name = "Webhook";
        ChannelId = $"webhook_{webhookUrl.GetHashCode()}";
        _webhookUrl = webhookUrl;
    }
    
    /// <summary>
    /// Register webhook handler
    /// </summary>
    public void On(string eventType, Func<ChannelMessage, Task<string>> handler)
    {
        _handlers[eventType] = handler;
    }
    
    /// <summary>
    /// Trigger webhook
    /// </summary>
    public async Task<string> TriggerAsync(string eventType, ChannelMessage message)
    {
        if (_handlers.TryGetValue(eventType, out var handler))
        {
            return await handler(message);
        }
        return "No handler registered";
    }
    
    public override async Task<bool> ConnectAsync()
    {
        await Task.Delay(100);
        IsConnected = true;
        return true;
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
