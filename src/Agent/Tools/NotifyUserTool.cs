using AgentFox.Channels;
using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

public class NotifyUserTool : BaseTool
{
    private readonly ChannelManager _channelManager;
    private readonly ILogger? _logger;

    public NotifyUserTool(ChannelManager channelManager, ILogger? logger = null)
    {
        _channelManager = channelManager;
        _logger = logger;
    }

    public override string Name => "notify_user";

    public override string Description
    {
        get
        {
            var connected = _channelManager.Channels.Values
                .Where(c => c.IsConnected)
                .Select(c => c.Type)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var channelList = connected.Count > 0
                ? string.Join(", ", connected)
                : "none - use manage_channel to add one";

            return
                "Send a notification or message to the user via all connected channels at once. " +
                $"Active channels: {channelList}. " +
                "Use this for alerts, cron job results, status updates, summaries, or any message intended for the user.";
        }
    }

    public override Dictionary<string, ToolParameter> Parameters { get; } = new()
    {
        ["message"] = new()
        {
            Type = "string",
            Description = "The message to deliver to the user. Markdown is supported on Telegram and Discord.",
            Required = true
        }
    };

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var message = arguments.GetValueOrDefault("message")?.ToString();
        if (string.IsNullOrWhiteSpace(message))
            return ToolResult.Fail("message is required");

        var channels = _channelManager.Channels.Values
            .Where(c => c.IsConnected)
            .ToList();

        if (channels.Count == 0)
            return ToolResult.Fail("No channels are connected. Use manage_channel to add a channel.");

        var sent = new List<string>();
        var failed = new List<string>();

        foreach (var channel in channels)
        {
            try
            {
                await channel.SendToTargetAsync(string.Empty, message);
                sent.Add(channel.Type);
                _logger?.LogInformation(
                    "notify_user: delivered to {Channel} ({Length} chars)",
                    channel.Type,
                    message.Length);
            }
            catch (Exception ex)
            {
                failed.Add(channel.Type);
                _logger?.LogError(ex, "notify_user: failed to deliver to {Channel}", channel.Type);
            }
        }

        if (failed.Count == 0)
            return ToolResult.Ok($"Notification delivered via: {string.Join(", ", sent)}.");

        if (sent.Count == 0)
            return ToolResult.Fail($"Failed to deliver via all channels: {string.Join(", ", failed)}.");

        return ToolResult.Ok(
            $"Partially delivered. Sent: {string.Join(", ", sent)}. " +
            $"Failed: {string.Join(", ", failed)}.");
    }
}
