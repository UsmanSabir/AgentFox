using AgentFox.Plugins.Channels;
using Microsoft.Extensions.Logging;

namespace TradingAgent.Channel;

/// <summary>
/// Channel provider discovered at startup from the plugins folder.
/// Registered automatically — no code changes needed in the main app.
///
/// appsettings.json Channels entry:
/// {
///   "Type":        "whatsapp-bridge",
///   "Enabled":     true,
///   "CallbackUrl": "http://your-bridge:3000/send",  // optional, for HITL replies
///   "GroupFilter": "PSX Signals"                    // optional, filters by group name
/// }
/// </summary>
public sealed class WhatsAppBridgeChannelProvider : IChannelProvider
{
    public string ChannelType => "whatsapp-bridge";
    public string DisplayName => "WhatsApp Bridge (3rd-party)";

    public IReadOnlyDictionary<string, ChannelConfigField> GetConfigSchema() =>
        new Dictionary<string, ChannelConfigField>
        {
            ["CallbackUrl"] = new()
            {
                Description = "Bridge endpoint for outbound messages (HITL approval prompts). " +
                              "POST with JSON body { \"text\": \"...\" }.",
                Required = false
            },
            ["GroupFilter"] = new()
            {
                Description = "Only process messages from this WhatsApp group name. " +
                              "Leave empty to accept all groups.",
                Required = false
            },
            ["RequireSignature"] = new()
            {
                Description = "Require X-AgentFox-Timestamp and X-AgentFox-Signature HMAC headers (default true).",
                Required = false
            },
            ["WebhookSecretEnvironmentVariable"] = new()
            {
                Description = "Environment variable containing the webhook HMAC secret.",
                Required = false
            },
            ["MaxClockSkewSeconds"] = new()
            {
                Description = "Maximum accepted timestamp age in seconds (default 120).",
                Required = false
            },
            ["AllowedSenders"] = new()
            {
                Description = "Optional comma-separated sender allowlist.",
                Required = false
            }
        };

    public (AgentFox.Plugins.Channels.Channel? Channel, string? Error) Create(
        Dictionary<string, string> config,
        ChannelCreationContext context)
    {
        config.TryGetValue("CallbackUrl", out var callbackUrl);
        config.TryGetValue("GroupFilter", out var groupFilter);
        config.TryGetValue("RequireSignature", out var requireSignatureRaw);
        config.TryGetValue("WebhookSecretEnvironmentVariable", out var secretEnvironmentVariable);
        config.TryGetValue("MaxClockSkewSeconds", out var maxClockSkewRaw);
        config.TryGetValue("AllowedSenders", out var allowedSendersRaw);

        var requireSignature = !bool.TryParse(requireSignatureRaw, out var parsedRequired) || parsedRequired;
        var maxClockSkew = int.TryParse(maxClockSkewRaw, out var parsedSkew)
            ? Math.Clamp(parsedSkew, 10, 3600)
            : 120;
        var secretVariable = string.IsNullOrWhiteSpace(secretEnvironmentVariable)
            ? "AGENTFOX_TRADING_WEBHOOK_SECRET"
            : secretEnvironmentVariable.Trim();
        var webhookSecret = Environment.GetEnvironmentVariable(secretVariable);
        var allowedSenders = (allowedSendersRaw ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var logger = context.LoggerFactory.CreateLogger(nameof(WhatsAppBridgeChannel));
        return (new WhatsAppBridgeChannel(
            callbackUrl, groupFilter, logger, requireSignature, webhookSecret,
            maxClockSkew, allowedSenders), null);
    }
}
