using System.IO;
using AgentFox.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AgentFox.Plugins.Channels;

public class WhatsAppChannel : Channel
{
    private readonly string _phoneNumberId;
    private readonly string _accessToken;
    private readonly string _businessAccountId;
    private string? _qrCode;

    public WhatsAppChannel(string phoneNumberId, string accessToken, string businessAccountId)
    {
        Type = "whatsapp";
        Name = "WhatsApp";
        ChannelId = $"whatsapp_{phoneNumberId}";
        _phoneNumberId = phoneNumberId;
        _accessToken = accessToken;
        _businessAccountId = businessAccountId;
    }

    public string GeneratePairingQRCode()
    {
        _qrCode = $"https://api.whatsapp.com/generate-qr?phone={_phoneNumberId}";
        return _qrCode;
    }

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
            Timestamp = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>
            {
                ["status"] = "sent"
            }
        };
    }

    public override async Task<List<ChannelMessage>> ReceiveMessagesAsync()
    {
        await Task.Delay(50);
        return new List<ChannelMessage>();
    }
}

public class PairingStatus
{
    public bool IsPaired { get; set; }
    public string? QRCode { get; set; }
    public string? PhoneNumberId { get; set; }
    public DateTime? PairedAt { get; set; }
}

public class TelegramChannel : Channel
{
    private readonly string _botToken;
    private readonly int _pollingTimeoutSeconds;
    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly string _chatIdStoragePath;
    private long? _defaultChatId;
    private long _updateOffset;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;

    private string ApiBase => $"https://api.telegram.org/bot{_botToken}";

    public TelegramChannel(
        string botToken,
        int pollingTimeoutSeconds = 30,
        ILogger? logger = null,
        long? chatId = null,
        string? workspacePath = null)
    {
        Type = "telegram";
        Name = "Telegram";
        ChannelId = "telegram";
        _botToken = botToken;
        _pollingTimeoutSeconds = pollingTimeoutSeconds;
        _logger = logger;
        _defaultChatId = chatId;
        _chatIdStoragePath = !string.IsNullOrWhiteSpace(workspacePath)
            ? Path.Combine(workspacePath, "telegram_chat_id.txt")
            : Path.Combine(AppContext.BaseDirectory, "telegram_chat_id.txt");
        LoadPersistedChatId();
        _http = HttpResilienceFactory.CreateForPolling(TimeSpan.FromSeconds(pollingTimeoutSeconds + 15));
    }

    public async Task<BotInfo> GetBotInfoAsync()
    {
        var json = await _http.GetStringAsync($"{ApiBase}/getMe");
        var resp = JsonConvert.DeserializeObject<TgApiResponse<TgUser>>(json);
        if (resp?.Ok != true || resp.Result == null)
            throw new InvalidOperationException("Telegram getMe failed - check your bot token.");

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
            if (!_defaultChatId.HasValue)
            {
                var resolvedChatId = await ResolveDefaultChatIdAsync();
                if (resolvedChatId.HasValue)
                    _logger?.LogInformation("Telegram default chat id resolved: {ChatId}", resolvedChatId.Value);
            }

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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Telegram polling error; retrying in 5s");
                try { await Task.Delay(5000, ct); } catch { }
            }
        }
    }

    private void HandleUpdate(TgUpdate update)
    {
        var msg = update.Message;
        if (msg == null || string.IsNullOrWhiteSpace(msg.Text))
            return;

        var chatId = msg.Chat.Id;
        if (chatId != 0 && !_defaultChatId.HasValue)
        {
            _defaultChatId = chatId;
            PersistDefaultChatId(chatId);
            _logger?.LogInformation("Telegram default chat id persisted from incoming update: {ChatId}", chatId);
        }

        var incoming = new ChannelMessage
        {
            ChannelId = $"telegram_{chatId}",
            SenderId = msg.From?.Id.ToString() ?? chatId.ToString(),
            SenderName = msg.From == null ? "Unknown" : $"{msg.From.FirstName} {msg.From.LastName}".Trim(),
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

    public override async Task<ChannelMessage> SendMessageAsync(string content)
    {
        _logger?.LogWarning("SendMessageAsync called without chat context on TelegramChannel");
        await Task.CompletedTask;
        return new ChannelMessage
        {
            ChannelId = ChannelId,
            Content = content,
            Timestamp = DateTime.UtcNow
        };
    }

    public override Task<List<ChannelMessage>> ReceiveMessagesAsync() =>
        Task.FromResult(new List<ChannelMessage>());

    public override Task SendToTargetAsync(string targetId, string content)
    {
        return SendToTargetInternalAsync(string.Empty, content);
    }

    public async Task SendToTargetInternalAsync(string targetId, string content)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            if (!await EnsureDefaultChatIdAsync())
            {
                _logger?.LogWarning("TelegramChannel.SendToTargetAsync: no chat ID configured or discoverable from getUpdates.");
                return;
            }

            await SendToChatAsync(_defaultChatId!.Value, content);
            return;
        }

        if (!long.TryParse(targetId, out var chatId))
        {
            _logger?.LogWarning("TelegramChannel.SendToTargetAsync: invalid chat_id '{TargetId}'", targetId);
            return;
        }

        await SendToChatAsync(chatId, content);
    }

    private async Task<bool> EnsureDefaultChatIdAsync()
    {
        if (_defaultChatId.HasValue)
            return true;

        return (await ResolveDefaultChatIdAsync()).HasValue;
    }

    private async Task<long?> ResolveDefaultChatIdAsync()
    {
        try
        {
            var url = $"{ApiBase}/getUpdates?limit=1";
            var json = await _http.GetStringAsync(url);
            var resp = JsonConvert.DeserializeObject<TgApiResponse<List<TgUpdate>>>(json);
            if (resp?.Ok == true && resp.Result != null && resp.Result.Count > 0)
            {
                var update = resp.Result[0];
                if (update.UpdateId >= _updateOffset)
                    _updateOffset = update.UpdateId + 1;

                var msg = update.Message;
                var chatId = msg != null && msg.Chat.Id != 0
                    ? msg.Chat.Id
                    : msg?.From?.Id ?? 0;

                if (chatId != 0)
                {
                    _defaultChatId = chatId;
                    PersistDefaultChatId(chatId);
                    return chatId;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Telegram default chat id lookup via getUpdates failed");
        }

        return null;
    }

    private void LoadPersistedChatId()
    {
        if (_defaultChatId.HasValue || string.IsNullOrWhiteSpace(_chatIdStoragePath))
            return;

        try
        {
            if (File.Exists(_chatIdStoragePath))
            {
                var text = File.ReadAllText(_chatIdStoragePath).Trim();
                if (long.TryParse(text, out var chatId) && chatId != 0)
                {
                    _defaultChatId = chatId;
                    _logger?.LogInformation("Telegram default chat id loaded from {Path}", _chatIdStoragePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read persisted Telegram chat id from {Path}", _chatIdStoragePath);
        }
    }

    private void PersistDefaultChatId(long chatId)
    {
        if (chatId == 0 || string.IsNullOrWhiteSpace(_chatIdStoragePath))
            return;

        try
        {
            var directory = Path.GetDirectoryName(_chatIdStoragePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_chatIdStoragePath, chatId.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to persist Telegram chat id to {Path}", _chatIdStoragePath);
        }
    }

    private async Task SendToChatAsync(long chatId, string text)
    {
        const int maxLen = 4096;
        for (var i = 0; i < text.Length; i += maxLen)
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

            if (parseMode != null && raw.Contains("can't parse entities"))
                await PostMessageAsync(chatId, text, parseMode: null);
        }
    }
}

public class BotInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}

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

public class TeamsChannel : Channel
{
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _serviceUrl;
    private string? _accessToken;

    public TeamsChannel(string tenantId, string clientId, string clientSecret, string serviceUrl)
    {
        Type = "teams";
        Name = "Microsoft Teams";
        ChannelId = $"teams_{tenantId}";
        _tenantId = tenantId;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _serviceUrl = serviceUrl;
    }

    public async Task RegisterBotAsync(string botId, string botSecret)
    {
        await Task.Delay(100);
    }

    public async Task SendProactiveMessageAsync(string conversationId, string content)
    {
        await Task.Delay(50);
    }

    public async Task<string> CreateMeetingAsync(string subject, DateTime startTime, DateTime endTime)
    {
        await Task.Delay(100);
        return Guid.NewGuid().ToString();
    }

    public override async Task<bool> ConnectAsync()
    {
        try
        {
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

public class SlackChannel : Channel
{
    private readonly string _botToken;
    private readonly string _signingSecret;
    private readonly string? _appToken;

    public SlackChannel(string botToken, string signingSecret, string? appToken = null)
    {
        Type = "slack";
        Name = "Slack";
        ChannelId = $"slack_{botToken[..Math.Min(8, botToken.Length)]}";
        _botToken = botToken;
        _signingSecret = signingSecret;
        _appToken = appToken;
    }

    public async Task SetupWebhookAsync(string url)
    {
        await Task.Delay(50);
    }

    public async Task PostToChannelAsync(string channel, string content, MessageAttachment? attachment = null)
    {
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
