using Microsoft.Extensions.Logging;

namespace AgentFox.Plugins.Channels;

public sealed class TelegramChannelProvider : IChannelProvider
{
    public string ChannelType => "telegram";
    public string DisplayName => "Telegram";

    public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() =>
        new Dictionary<string, ChannelConfigField>
        {
            ["BotToken"] = new() { Description = "Bot token from @BotFather", Required = true },
            ["PollingTimeoutSeconds"] = new() { Description = "Long-poll timeout in seconds", Required = false },
            ["ChatId"] = new() { Description = "Default chat id for proactive sends", Required = false }
        };

    public (Channel? Channel, string? Error) Create(Dictionary<string, string> config, ChannelCreationContext context)
    {
        if (!config.TryGetValue("BotToken", out var token) || string.IsNullOrWhiteSpace(token))
            return (null, "Telegram requires 'BotToken'");

        var timeout = config.TryGetValue("PollingTimeoutSeconds", out var rawTimeout)
            && int.TryParse(rawTimeout, out var parsedTimeout)
                ? parsedTimeout
                : 30;

        long? chatId = null;
        if (config.TryGetValue("ChatId", out var chatIdStr)
            && !string.IsNullOrWhiteSpace(chatIdStr)
            && long.TryParse(chatIdStr, out var parsedChatId))
        {
            chatId = parsedChatId;
        }

        return (new TelegramChannel(
            token,
            timeout,
            context.LoggerFactory.CreateLogger<TelegramChannel>(),
            chatId,
            context.WorkspacePath), null);
    }
}

public sealed class SlackChannelProvider : IChannelProvider
{
    public string ChannelType => "slack";
    public string DisplayName => "Slack";

    public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() =>
        new Dictionary<string, ChannelConfigField>
        {
            ["BotToken"] = new() { Description = "xoxb-... bot token", Required = true },
            ["SigningSecret"] = new() { Description = "App signing secret", Required = true },
            ["AppToken"] = new() { Description = "xapp-... app-level token for Socket Mode", Required = false }
        };

    public (Channel? Channel, string? Error) Create(Dictionary<string, string> config, ChannelCreationContext context)
    {
        if (!config.TryGetValue("BotToken", out var botToken) || string.IsNullOrWhiteSpace(botToken))
            return (null, "Slack requires 'BotToken'");
        if (!config.TryGetValue("SigningSecret", out var signingSecret) || string.IsNullOrWhiteSpace(signingSecret))
            return (null, "Slack requires 'SigningSecret'");

        config.TryGetValue("AppToken", out var appToken);
        return (new SlackChannel(botToken, signingSecret, appToken), null);
    }
}

public sealed class DiscordChannelProvider : IChannelProvider
{
    public string ChannelType => "discord";
    public string DisplayName => "Discord";

    public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() =>
        new Dictionary<string, ChannelConfigField>
        {
            ["BotToken"] = new() { Description = "Bot token from the Discord developer portal", Required = true },
            ["GuildId"] = new() { Description = "Server or guild id as a number", Required = true },
            ["DefaultChannelId"] = new() { Description = "Default channel id to post messages to", Required = true }
        };

    public (Channel? Channel, string? Error) Create(Dictionary<string, string> config, ChannelCreationContext context)
    {
        if (!config.TryGetValue("BotToken", out var botToken) || string.IsNullOrWhiteSpace(botToken))
            return (null, "Discord requires 'BotToken'");
        if (!config.TryGetValue("GuildId", out var guildStr) || !ulong.TryParse(guildStr, out var guildId))
            return (null, "Discord requires 'GuildId' as a numeric ID");
        if (!config.TryGetValue("DefaultChannelId", out var chanStr) || !ulong.TryParse(chanStr, out var channelId))
            return (null, "Discord requires 'DefaultChannelId' as a numeric ID");

        return (new DiscordChannel(botToken, guildId, channelId), null);
    }
}

public sealed class TeamsChannelProvider : IChannelProvider
{
    public string ChannelType => "teams";
    public string DisplayName => "Microsoft Teams";

    public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() =>
        new Dictionary<string, ChannelConfigField>
        {
            ["TenantId"] = new() { Description = "Azure tenant ID", Required = true },
            ["ClientId"] = new() { Description = "App client ID", Required = true },
            ["ClientSecret"] = new() { Description = "App client secret", Required = true },
            ["ServiceUrl"] = new() { Description = "Teams service URL", Required = true }
        };

    public (Channel? Channel, string? Error) Create(Dictionary<string, string> config, ChannelCreationContext context)
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
}

public sealed class WhatsAppChannelProvider : IChannelProvider
{
    public string ChannelType => "whatsapp";
    public string DisplayName => "WhatsApp";

    public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() =>
        new Dictionary<string, ChannelConfigField>
        {
            ["PhoneNumberId"] = new() { Description = "Phone number ID from WhatsApp Business API", Required = true },
            ["AccessToken"] = new() { Description = "Access token", Required = true },
            ["BusinessAccountId"] = new() { Description = "Business account ID", Required = true }
        };

    public (Channel? Channel, string? Error) Create(Dictionary<string, string> config, ChannelCreationContext context)
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
