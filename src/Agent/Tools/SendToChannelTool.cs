using AgentFox.Channels;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

/// <summary>
/// Tool that lets the agent proactively push a message to any registered channel.
///
/// Parameters and Description are computed from the live ChannelManager on every access,
/// so the agent always sees up-to-date channel information — even after channels are
/// added or removed at runtime via ManageChannelTool.
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

    // Computed live so the agent always sees the current channel list.
    public override string Description
    {
        get
        {
            var channels = CurrentChannelNames();
            var list = channels.Count > 0 ? string.Join(", ", channels) : "none — use manage_channel to add one";
            return
                "Send a message to a specific registered channel. " +
                $"Available channels: {list}. " +
                "For Telegram, target_id is the numeric chat ID (e.g. '123456789' for DMs, '-100...' for groups). " +
                "For Slack/Discord, target_id is the channel name or ID. " +
                "Use manage_channel to add new channels at runtime.";
        }
    }

    // Computed live so EnumValues always matches the current channel set.
    public override Dictionary<string, ToolParameter> Parameters
    {
        get
        {
            var channels = CurrentChannelNames();
            return new()
            {
                ["channel_name"] = new()
                {
                    Type = "string",
                    Description = channels.Count > 0
                        ? $"Target channel. Available: {string.Join(", ", channels)}"
                        : "Target channel. No channels configured yet — use manage_channel to add one.",
                    Required = true,
                    EnumValues = channels.Count > 0 ? channels : null
                },
                ["target_id"] = new()
                {
                    Type = "string",
                    Description =
                        "Destination within the channel. " +
                        "For Telegram: numeric chat ID (e.g., '123456789'). " +
                        "For Slack/Discord: channel name or ID. " +
                        "For single-recipient channels (WhatsApp, Teams): omit this field.",
                    Required = false
                },
                ["message"] = new()
                {
                    Type = "string",
                    Description = "The message content to send. Markdown is supported on Telegram and Discord.",
                    Required = true
                }
            };
        }
    }

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
            var registered = string.Join(", ", CurrentChannelNames());
            return ToolResult.Fail(
                $"Channel '{channelName}' is not registered. " +
                $"Registered: {(registered.Length > 0 ? registered : "none")}");
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
                "send_to_channel: delivered to {Destination} ({Length} chars)",
                destination, message.Length);

            return ToolResult.Ok($"Message sent to {destination} successfully.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "send_to_channel: failed to send to {Channel}:{Target}", channelName, targetId);
            return ToolResult.Fail($"Failed to send to '{channelName}': {ex.Message}");
        }
    }

    private List<string> CurrentChannelNames() =>
        _channelManager.Channels.Values
            .Select(c => c.Name.ToLowerInvariant())
            .ToList();
}
