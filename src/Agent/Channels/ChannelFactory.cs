using Microsoft.Extensions.Logging;

namespace AgentFox.Channels;

/// <summary>
/// Creates Channel instances from a type name and a flat config dictionary.
/// Used by ManageChannelTool to add channels at runtime without restarting.
/// </summary>
public static class ChannelFactory
{
    /// <summary>
    /// Supported channel type names (lowercase).
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedTypes =
        ["telegram", "slack", "discord", "teams", "whatsapp"];

    /// <summary>
    /// Create a channel from a type name and a flat config dictionary.
    /// Returns (channel, null) on success or (null, errorMessage) on failure.
    /// </summary>
    public static (Channel? Channel, string? Error) Create(
        string type,
        Dictionary<string, string> config,
        ILogger? logger = null)
    {
        return type.ToLowerInvariant() switch
        {
            "telegram" => CreateTelegram(config, logger),
            "slack"    => CreateSlack(config),
            "discord"  => CreateDiscord(config),
            "teams"    => CreateTeams(config),
            "whatsapp" => CreateWhatsApp(config),
            _ => (null, $"Unknown channel type '{type}'. Supported: {string.Join(", ", SupportedTypes)}")
        };
    }

    /// <summary>
    /// Returns the config fields required to add a channel of the given type.
    /// Keys are field names, values are short descriptions. Returns null for unknown types.
    /// </summary>
    public static Dictionary<string, string>? GetRequiredConfig(string type) =>
        type.ToLowerInvariant() switch
        {
            "telegram" => new()
            {
                ["BotToken"] = "Bot token from @BotFather (required)",
                ["PollingTimeoutSeconds"] = "Long-poll timeout in seconds (optional, default: 30)"
            },
            "slack" => new()
            {
                ["BotToken"] = "xoxb-... bot token (required)",
                ["SigningSecret"] = "App signing secret (required)",
                ["AppToken"] = "xapp-... app-level token for Socket Mode (optional)"
            },
            "discord" => new()
            {
                ["BotToken"] = "Bot token from the Discord developer portal (required)",
                ["GuildId"] = "Server/guild ID as a number (required)",
                ["DefaultChannelId"] = "Default channel ID to post messages to (required)"
            },
            "teams" => new()
            {
                ["TenantId"] = "Azure tenant ID (required)",
                ["ClientId"] = "App client ID (required)",
                ["ClientSecret"] = "App client secret (required)",
                ["ServiceUrl"] = "Teams service URL (required)"
            },
            "whatsapp" => new()
            {
                ["PhoneNumberId"] = "Phone number ID from WhatsApp Business API (required)",
                ["AccessToken"] = "Access token (required)",
                ["BusinessAccountId"] = "Business account ID (required)"
            },
            _ => null
        };

    // ── Private creators ─────────────────────────────────────────────────────

    private static (Channel?, string?) CreateTelegram(Dictionary<string, string> config, ILogger? logger)
    {
        if (!config.TryGetValue("BotToken", out var token) || string.IsNullOrWhiteSpace(token))
            return (null, "Telegram requires 'BotToken'");

        var timeout = config.TryGetValue("PollingTimeoutSeconds", out var t) && int.TryParse(t, out var s) ? s : 30;
        long? chatId = null;
        if (config.TryGetValue("ChatId", out var chatIdStr)
            && !string.IsNullOrWhiteSpace(chatIdStr)
            && long.TryParse(chatIdStr, out var parsedChatId))
        {
            chatId = parsedChatId;
        }

        config.TryGetValue("WorkspacePath", out var workspacePath);
        return (new TelegramChannel(token, timeout, logger, chatId, workspacePath), null);
    }

    private static (Channel?, string?) CreateSlack(Dictionary<string, string> config)
    {
        if (!config.TryGetValue("BotToken", out var botToken) || string.IsNullOrWhiteSpace(botToken))
            return (null, "Slack requires 'BotToken'");
        if (!config.TryGetValue("SigningSecret", out var signingSecret) || string.IsNullOrWhiteSpace(signingSecret))
            return (null, "Slack requires 'SigningSecret'");

        config.TryGetValue("AppToken", out var appToken);
        return (new SlackChannel(botToken, signingSecret, appToken), null);
    }

    private static (Channel?, string?) CreateDiscord(Dictionary<string, string> config)
    {
        if (!config.TryGetValue("BotToken", out var botToken) || string.IsNullOrWhiteSpace(botToken))
            return (null, "Discord requires 'BotToken'");
        if (!config.TryGetValue("GuildId", out var guildStr) || !ulong.TryParse(guildStr, out var guildId))
            return (null, "Discord requires 'GuildId' as a numeric ID");
        if (!config.TryGetValue("DefaultChannelId", out var chanStr) || !ulong.TryParse(chanStr, out var channelId))
            return (null, "Discord requires 'DefaultChannelId' as a numeric ID");

        return (new DiscordChannel(botToken, guildId, channelId), null);
    }

    private static (Channel?, string?) CreateTeams(Dictionary<string, string> config)
    {
        if (!config.TryGetValue("TenantId", out var tenantId) || string.IsNullOrWhiteSpace(tenantId))
            return (null, "Teams requires 'TenantId'");
        if (!config.TryGetValue("ClientId", out var clientId) || string.IsNullOrWhiteSpace(clientId))
            return (null, "Teams requires 'ClientId'");
        if (!config.TryGetValue("ClientSecret", out var clientSecret) || string.IsNullOrWhiteSpace(clientSecret))
            return (null, "Teams requires 'ClientSecret'");
        if (!config.TryGetValue("ServiceUrl", out var serviceUrl) || string.IsNullOrWhiteSpace(serviceUrl))
            return (null, "Teams requires 'ServiceUrl'");

        return (new TeamsChannel(tenantId, clientId, clientSecret, serviceUrl), null);
    }

    private static (Channel?, string?) CreateWhatsApp(Dictionary<string, string> config)
    {
        if (!config.TryGetValue("PhoneNumberId", out var phoneId) || string.IsNullOrWhiteSpace(phoneId))
            return (null, "WhatsApp requires 'PhoneNumberId'");
        if (!config.TryGetValue("AccessToken", out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
            return (null, "WhatsApp requires 'AccessToken'");
        if (!config.TryGetValue("BusinessAccountId", out var businessId) || string.IsNullOrWhiteSpace(businessId))
            return (null, "WhatsApp requires 'BusinessAccountId'");

        return (new WhatsAppChannel(phoneId, accessToken, businessId), null);
    }
}
