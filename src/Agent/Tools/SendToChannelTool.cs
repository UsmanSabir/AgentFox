using AgentFox.Channels;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

/// <summary>
/// Tool that lets the agent proactively push a message to any registered channel.
///
/// This is the "outbound notification" primitive — distinct from the reactive
/// RequesterChannel pattern (which replies back to whoever sent the original message).
///
/// Use this when the agent decides, mid-execution, to notify a specific channel:
///   e.g., "check email → summarise → send_to_channel(telegram, chat_id, summary)"
///
/// Works from any execution context: terminal session, background sub-agent,
/// scheduled task, or another channel's handler.
///
/// Registration (Program.cs, before agent build):
///   toolRegistry.Register(new SendToChannelTool(channelManager, logger));
/// </summary>
public class SendToChannelTool : BaseTool
{
    private readonly ChannelManager _channelManager;
    private readonly ILogger? _logger;

    public SendToChannelTool(ChannelManager channelManager, ILogger? logger = null)
    {
        _channelManager = channelManager;
        _logger = logger;
    }

    public override string Name => "send_to_channel";

    public override string Description =>
        "Send a message to a specific registered channel. " +
        "Use this to proactively notify a channel with results, summaries, or alerts — " +
        "regardless of where the current task was initiated (terminal, another channel, etc.). " +
        "Supported channels: telegram, slack, discord, whatsapp, teams (must be registered at startup). " +
        "For Telegram, target_id is the numeric chat ID (e.g., '123456789' for DMs, '-100...' for groups). " +
        "For Slack/Discord, target_id is the channel name or ID.";

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["channel_name"] = new()
        {
            Type = "string",
            Description = "Name of the target channel: 'telegram', 'slack', 'discord', 'whatsapp', or 'teams'",
            Required = true,
            EnumValues = ["telegram", "slack", "discord", "whatsapp", "teams"]
        },
        ["target_id"] = new()
        {
            Type = "string",
            Description = "Destination within the channel. For Telegram: numeric chat ID (e.g., '123456789'). " +
                          "For Slack/Discord: channel name or ID. For single-recipient channels (WhatsApp, Teams): can be omitted.",
            Required = false
        },
        ["message"] = new()
        {
            Type = "string",
            Description = "The message content to send. Markdown is supported on Telegram and Discord.",
            Required = true
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var channelName = arguments.GetValueOrDefault("channel_name")?.ToString();
        var targetId    = arguments.GetValueOrDefault("target_id")?.ToString() ?? string.Empty;
        var message     = arguments.GetValueOrDefault("message")?.ToString();

        if (string.IsNullOrWhiteSpace(channelName))
            return ToolResult.Fail("channel_name is required");
        if (string.IsNullOrWhiteSpace(message))
            return ToolResult.Fail("message is required");

        var channel = _channelManager.GetChannelByName(channelName);
        if (channel == null)
        {
            var registered = string.Join(", ",
                _channelManager.Channels.Values.Select(c => c.Name.ToLowerInvariant()));
            return ToolResult.Fail(
                $"Channel '{channelName}' is not registered. " +
                $"Registered channels: {(registered.Length > 0 ? registered : "none")}");
        }

        if (!channel.IsConnected)
            return ToolResult.Fail($"Channel '{channelName}' is registered but not connected.");

        try
        {
            await channel.SendToTargetAsync(targetId, message);

            var destination = string.IsNullOrWhiteSpace(targetId)
                ? channelName
                : $"{channelName}:{targetId}";

            _logger?.LogInformation(
                "send_to_channel: message delivered to {Destination} ({Length} chars)",
                destination, message.Length);

            return ToolResult.Ok($"Message sent to {destination} successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "send_to_channel: failed to send to {Channel}:{Target}",
                channelName, targetId);
            return ToolResult.Fail($"Failed to send to '{channelName}': {ex.Message}");
        }
    }
}
