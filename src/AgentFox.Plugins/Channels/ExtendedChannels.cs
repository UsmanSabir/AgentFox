using System.Collections.Concurrent;
using System.Diagnostics;
using Discord;
using Discord.Webhook;
using Discord.WebSocket;

namespace AgentFox.Plugins.Channels;

public class DiscordChannel : Channel
{
    private readonly string _botToken;
    private readonly ulong _guildId;
    private readonly ulong _channelId;
    private DiscordSocketClient? _client;
    private SocketTextChannel? _textChannel;
    private DiscordWebhookClient? _webhookClient;
    private readonly List<ChannelMessage> _receivedMessages = new();
    private bool _stopReconnecting;
    private bool _reconnecting;

    // Durable in-memory outbound buffer: messages that can't be delivered right now (channel
    // down / reconnecting) are queued here and flushed, in order, once the gateway is Ready
    // again — so a reply is never silently lost across a transient disconnect.
    private readonly ConcurrentQueue<PendingOutbound> _pendingOutbound = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private const int MaxPendingOutbound = 200;

    private sealed record PendingOutbound(string Content, ulong? ReplyToMessageId);

    public DiscordChannel(string botToken, ulong guildId, ulong channelId)
    {
        Type = "discord";
        Name = "Discord";
        ChannelId = $"discord_{guildId}_{channelId}";
        _botToken = botToken;
        _guildId = guildId;
        _channelId = channelId;
    }

    public async Task SetWebhookAsync(string webhookUrl)
    {
        try
        {
            _webhookClient = new DiscordWebhookClient(webhookUrl);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to set webhook URL: {ex.Message}", ex);
        }
    }

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

    public async Task AddReactionAsync(ulong messageId, IEmote emoji)
    {
        try
        {
            if (!IsConnected || _textChannel == null)
                throw new InvalidOperationException("Discord channel is not connected");

            var message = await _textChannel.GetMessageAsync(messageId);
            if (message is IUserMessage userMessage)
                await userMessage.AddReactionAsync(emoji);
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
            if (_client != null)
            {
                _client.MessageReceived -= HandleMessageReceivedAsync;
                _client.Disconnected -= OnDisconnectedAsync;
                _client.Ready -= OnClientReady;
                try { await _client.StopAsync(); } catch { }
                _client.Dispose();
                _client = null;
            }

            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent
            };

            _client = new DiscordSocketClient(config);
            _client.Log += LogAsync;
            _client.Disconnected += OnDisconnectedAsync;
            _client.Ready += OnClientReady;

            await _client.LoginAsync(TokenType.Bot, _botToken);
            await _client.StartAsync();

            const int maxWaitTime = 30000;
            var elapsed = 0;
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
        var guild = _client?.GetGuild(_guildId);
        if (guild == null)
        {
            IsConnected = false;
            Debug.WriteLine($"[Discord] OnClientReady: guild {_guildId} not found in cache.");
            return Task.CompletedTask;
        }

        _textChannel = guild.GetTextChannel(_channelId);
        if (_textChannel == null)
        {
            IsConnected = false;
            Debug.WriteLine($"[Discord] OnClientReady: text channel {_channelId} not found in guild {_guildId}.");
            return Task.CompletedTask;
        }

        if (_client != null)
        {
            _client.MessageReceived -= HandleMessageReceivedAsync;
            _client.MessageReceived += HandleMessageReceivedAsync;
        }

        IsConnected = true;
        Debug.WriteLine($"[Discord] Ready - guild '{guild.Name}', channel '#{_textChannel.Name}'.");

        // Drain anything that was buffered while the channel was down.
        if (!_pendingOutbound.IsEmpty)
            _ = FlushPendingOutboundAsync();

        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(Exception? ex)
    {
        if (_stopReconnecting || _reconnecting)
            return Task.CompletedTask;

        _reconnecting = true;
        IsConnected = false;

        _ = Task.Run(async () =>
        {
            var delayMs = 5000;
            const int maxDelayMs = 120000;

            while (!_stopReconnecting)
            {
                Debug.WriteLine($"[Discord] Disconnected. Reconnecting in {delayMs / 1000} s...");
                await Task.Delay(delayMs);

                if (_stopReconnecting)
                    break;

                var success = await ConnectInternalAsync();
                if (success)
                {
                    Debug.WriteLine("[Discord] Reconnected successfully.");
                    break;
                }

                delayMs = Math.Min(delayMs * 2, maxDelayMs);
            }

            _reconnecting = false;
        });

        return Task.CompletedTask;
    }

    public override async Task DisconnectAsync()
    {
        _stopReconnecting = true;

        try
        {
            if (_client != null)
            {
                _client.MessageReceived -= HandleMessageReceivedAsync;
                _client.Disconnected -= OnDisconnectedAsync;
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
            IsConnected = false;
        }
    }

    // A long-running agent turn can outlast a transient Discord gateway drop: by the time
    // the reply is delivered the socket may be mid-reconnect (OnDisconnectedAsync backs off
    // up to 120s). Rather than throwing instantly and losing the message, wait briefly for
    // the connection (and a fresh _textChannel) to be restored.
    private static readonly TimeSpan SendConnectionWait = TimeSpan.FromSeconds(90);

    private async Task<bool> WaitForConnectionAsync(TimeSpan timeout)
    {
        if (IsConnected && _textChannel != null)
            return true;

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !_stopReconnecting)
        {
            await Task.Delay(500);
            if (IsConnected && _textChannel != null)
                return true;
        }

        return IsConnected && _textChannel != null;
    }

    public override async Task SendReplyAsync(ChannelMessage originalMessage, string content)
    {
        ulong? replyTo = null;
        if (originalMessage.Metadata != null
            && originalMessage.Metadata.TryGetValue("messageId", out var msgIdStr)
            && ulong.TryParse(msgIdStr, out var msgId))
        {
            replyTo = msgId;
        }

        if (!IsConnected || _textChannel == null)
            await WaitForConnectionAsync(SendConnectionWait);

        if (!IsConnected || _textChannel == null)
        {
            BufferOutbound(content, replyTo);
            return;
        }

        try
        {
            await SendDirectAsync(content, replyTo);
        }
        catch (Exception ex)
        {
            // Socket may have dropped mid-send — keep the message and retry on reconnect.
            Debug.WriteLine($"[Discord] Reply send failed, buffering: {ex.Message}");
            BufferOutbound(content, replyTo);
        }
    }

    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        if (!IsConnected || _textChannel == null)
            await WaitForConnectionAsync(SendConnectionWait);

        if (!IsConnected || _textChannel == null)
        {
            BufferOutbound(content, replyToMessageId: null);
            return QueuedMessage(content);
        }

        try
        {
            var lastMessage = await SendDirectAsync(content, replyToMessageId: null);
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
            Debug.WriteLine($"[Discord] Send failed, buffering: {ex.Message}");
            BufferOutbound(content, replyToMessageId: null);
            return QueuedMessage(content);
        }
    }

    // Core send: splits long content into Discord's 2000-char chunks and attaches the reply
    // reference (if any) to the first chunk. Used by both the live path and the buffer flush.
    private async Task<IMessage?> SendDirectAsync(string content, ulong? replyToMessageId)
    {
        if (_textChannel == null)
            throw new InvalidOperationException("Discord channel is not connected");

        if (string.IsNullOrWhiteSpace(content))
            content = "[Empty Response]";

        const int maxLength = 2000;
        var messages = SplitMessage(content, maxLength);

        var reference = replyToMessageId.HasValue
            ? new MessageReference(messageId: replyToMessageId.Value, channelId: _channelId)
            : null;

        IMessage? lastMessage = null;
        for (var i = 0; i < messages.Count; i++)
        {
            var refForChunk = i == 0 ? reference : null;
            try
            {
                lastMessage = await _textChannel.SendMessageAsync(text: messages[i], messageReference: refForChunk);
            }
            catch when (refForChunk != null)
            {
                // The referenced message may have been deleted — resend this chunk without it.
                lastMessage = await _textChannel.SendMessageAsync(messages[i]);
            }
        }

        return lastMessage;
    }

    private void BufferOutbound(string content, ulong? replyToMessageId)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        // Bound the buffer so a prolonged outage can't grow it without limit.
        while (_pendingOutbound.Count >= MaxPendingOutbound && _pendingOutbound.TryDequeue(out _))
        {
        }

        _pendingOutbound.Enqueue(new PendingOutbound(content, replyToMessageId));
        Debug.WriteLine($"[Discord] Buffered outbound message (queue depth {_pendingOutbound.Count}).");
    }

    private async Task FlushPendingOutboundAsync()
    {
        // Only one flush at a time; a second trigger just returns and lets the first drain.
        if (!await _flushLock.WaitAsync(0))
            return;

        try
        {
            // Peek-then-dequeue: an item is removed only after it is confirmed sent, so a
            // failure mid-flush leaves it (and the rest) queued for the next reconnect.
            while (IsConnected && _textChannel != null && _pendingOutbound.TryPeek(out var pending))
            {
                try
                {
                    await SendDirectAsync(pending.Content, pending.ReplyToMessageId);
                    _pendingOutbound.TryDequeue(out _);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Discord] Flush failed, will retry on next reconnect: {ex.Message}");
                    break;
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private ChannelMessage QueuedMessage(string content) => new()
    {
        Id = string.Empty,
        ChannelId = ChannelId,
        Content = content,
        Timestamp = DateTime.UtcNow,
        Type = MessageType.Text
    };

    public override async Task<List<ChannelMessage>> ReceiveMessagesAsync()
    {
        try
        {
            if (!IsConnected || _textChannel == null)
                return new List<ChannelMessage>();

            var messages = new List<ChannelMessage>();
            var discordMessages = await _textChannel.GetMessagesAsync(limit: 10).FlattenAsync();

            foreach (var msg in discordMessages.OrderBy(m => m.Timestamp))
            {
                if (msg.Author.IsBot && msg.Author.Id == _client!.CurrentUser.Id)
                    continue;

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
                        ["messageId"] = msg.Id.ToString(),
                        ["authorId"] = msg.Author.Id.ToString()
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
            if (message.Author.IsBot || message is not SocketUserMessage userMessage)
                return;

            if (userMessage.Channel.Id != _channelId)
                return;

            var channelMessage = new ChannelMessage
            {
                Id = userMessage.Id.ToString(),
                ChannelId = ChannelId,
                SenderId = userMessage.Author.Id.ToString(),
                SenderName = userMessage.Author.Username,
                Content = userMessage.Content,
                Timestamp = userMessage.Timestamp.UtcDateTime,
                Type = MessageType.Text,
                Metadata = new Dictionary<string, string>
                {
                    ["messageId"] = userMessage.Id.ToString(),
                    ["authorId"] = userMessage.Author.Id.ToString()
                }
            };

            _receivedMessages.Add(channelMessage);
            RaiseMessageReceived(channelMessage);
        }
        catch
        {
        }
    }

    private static Task LogAsync(LogMessage message)
    {
        Debug.WriteLine($"[{message.Severity}] {message.Source}: {message.Message}");
        return Task.CompletedTask;
    }

    private static List<string> SplitMessage(string message, int maxLength)
    {
        var messages = new List<string>();
        if (message.Length <= maxLength)
        {
            messages.Add(message);
            return messages;
        }

        var index = 0;
        while (index < message.Length)
        {
            var length = Math.Min(maxLength, message.Length - index);
            messages.Add(message.Substring(index, length));
            index += length;
        }

        return messages;
    }
}

public static class DiscordEmbedExtensions
{
    public static EmbedBuilder CreateEmbed(string title, string? description = null, uint? color = null)
    {
        var embed = new EmbedBuilder().WithTitle(title);
        if (!string.IsNullOrEmpty(description))
            embed.WithDescription(description);
        if (color.HasValue)
            embed.WithColor(new Color(color.Value));
        return embed;
    }
}

public class SMSChannel : Channel
{
    private readonly string _accountSid;
    private readonly string _authToken;
    private readonly string _fromNumber;

    public SMSChannel(string accountSid, string authToken, string fromNumber)
    {
        Type = "sms";
        Name = "SMS";
        ChannelId = $"sms_{fromNumber}";
        _accountSid = accountSid;
        _authToken = authToken;
        _fromNumber = fromNumber;
    }

    public async Task<string> SendSMSAsync(string to, string message)
    {
        await Task.Delay(100);
        return $"SMS sent to {to}: {message}";
    }

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

public class EmailChannel : Channel
{
    private readonly string _smtpHost;
    private readonly int _smtpPort;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromEmail;

    public EmailChannel(string smtpHost, int smtpPort, string username, string password, string fromEmail)
    {
        Type = "email";
        Name = "Email";
        ChannelId = $"email_{fromEmail}";
        _smtpHost = smtpHost;
        _smtpPort = smtpPort;
        _username = username;
        _password = password;
        _fromEmail = fromEmail;
    }

    public async Task SendEmailAsync(string to, string subject, string body, bool isHtml = false)
    {
        await Task.Delay(100);
    }

    public async Task SendEmailWithAttachmentAsync(string to, string subject, string body, string attachmentPath)
    {
        await Task.Delay(100);
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

public class WebSocketChannel : Channel
{
    private readonly string _wsUrl;
    private readonly List<(string, string)> _subscribedEvents = new();

    public WebSocketChannel(string wsUrl)
    {
        Type = "websocket";
        Name = "WebSocket";
        ChannelId = $"ws_{wsUrl.GetHashCode()}";
        _wsUrl = wsUrl;
    }

    public void Subscribe(string eventName, string filter = "")
    {
        _subscribedEvents.Add((eventName, filter));
    }

    public void Unsubscribe(string eventName)
    {
        _subscribedEvents.RemoveAll(e => e.Item1 == eventName);
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

public class RSSChannel : Channel
{
    private readonly string _feedUrl;
    private DateTime _lastCheck = DateTime.MinValue;

    public RSSChannel(string feedUrl)
    {
        Type = "rss";
        Name = "RSS";
        ChannelId = $"rss_{feedUrl.GetHashCode()}";
        _feedUrl = feedUrl;
    }

    public async Task<List<RSSItem>> CheckForNewItemsAsync()
    {
        await Task.Delay(100);
        _lastCheck = DateTime.UtcNow;
        return new List<RSSItem>();
    }

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

public class WebhookChannel : Channel
{
    private readonly string _webhookUrl;
    private readonly Dictionary<string, Func<ChannelMessage, Task<string>>> _handlers = new();

    public WebhookChannel(string webhookUrl)
    {
        Type = "webhook";
        Name = "Webhook";
        ChannelId = $"webhook_{webhookUrl.GetHashCode()}";
        _webhookUrl = webhookUrl;
    }

    public void On(string eventType, Func<ChannelMessage, Task<string>> handler)
    {
        _handlers[eventType] = handler;
    }

    public async Task<string> TriggerAsync(string eventType, ChannelMessage message)
    {
        if (_handlers.TryGetValue(eventType, out var handler))
            return await handler(message);

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
