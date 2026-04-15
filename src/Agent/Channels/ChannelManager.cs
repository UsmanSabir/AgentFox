using AgentFox.Agents;
using AgentFox.Hitl;
using AgentFox.Models;
using AgentFox.Plugins.Channels;
using AgentFox.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentFox.Channels;

/// <summary>
/// Channel manager for handling multiple channel integrations.
/// Supports both direct agent execution and gateway-based lane processing.
/// </summary>
public class ChannelManager
{
    private readonly Dictionary<string, Channel> _channels = new();
    private readonly Func<FoxAgent?> _agentFactory;
    private ChannelMessageGateway? _gateway;
    private readonly SessionManager? _sessionManager;
    private readonly ICommandQueue? _commandQueue;
    private readonly ILogger? _logger;
    private HitlManager? _hitlManager;

    public IReadOnlyDictionary<string, Channel> Channels => _channels;
    public ChannelMessageGateway? Gateway => _gateway;

    public Channel? GetChannelByName(string name) =>
        _channels.Values.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            c.Type.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ChannelManager(
        Func<FoxAgent?> agentFactory,
        SessionManager? sessionManager = null,
        ICommandQueue? commandQueue = null,
        ILogger? logger = null)
    {
        _agentFactory = agentFactory;
        _sessionManager = sessionManager;
        _commandQueue = commandQueue;
        _logger = logger;
    }

    public ChannelManager(
        FoxAgent agent,
        SessionManager? sessionManager = null,
        ICommandQueue? commandQueue = null,
        ILogger? logger = null)
        : this(() => agent, sessionManager, commandQueue, logger)
    {
    }

    public void SetGateway(ChannelMessageGateway gateway)
    {
        _gateway = gateway;
        _logger?.LogInformation("ChannelMessageGateway set for channel manager");
    }

    /// <summary>
    /// Wires in the HITL manager so incoming channel messages can resolve
    /// pending approval gates (/approve, /reject) and free-form input gates.
    /// </summary>
    public void SetHitlManager(HitlManager hitlManager) =>
        _hitlManager = hitlManager;

    public void AddChannel(Channel channel)
    {
        _channels[channel.ChannelId] = channel;
        channel.OnMessageReceived += async (_, msg) => await HandleMessage(channel, msg);
    }

    public async Task<bool> AddAndConnectAsync(Channel channel)
    {
        var connected = await channel.ConnectAsync();
        if (!connected)
        {
            _logger?.LogWarning("AddAndConnectAsync: could not connect channel '{Name}'", channel.Name);
            return false;
        }

        AddChannel(channel);
        _logger?.LogInformation("Channel '{Name}' added and connected at runtime", channel.Name);
        return true;
    }

    public async Task RemoveChannelAsync(string channelId)
    {
        if (_channels.TryGetValue(channelId, out var channel))
        {
            await channel.DisconnectAsync();
            _channels.Remove(channelId);
        }
    }

    public async Task ConnectAllAsync()
    {
        foreach (var channel in _channels.Values)
            await channel.ConnectAsync();
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var channel in _channels.Values)
            await channel.DisconnectAsync();
    }

    private async Task HandleMessage(Channel channel, ChannelMessage message)
    {
        var agent = _agentFactory();
        if (agent == null)
        {
            _logger?.LogWarning("HandleMessage: agent not yet available, dropping message {MessageId}", message.Id);
            return;
        }

        // ── HITL interception — runs before gateway/queue routing ─────────────
        if (_hitlManager != null)
        {
            var channelId = string.IsNullOrEmpty(message.ChannelId)
                ? channel.ChannelId
                : message.ChannelId;
            var content = message.Content?.Trim() ?? string.Empty;

            // Mode 1: /approve <id> [feedback]
            if (content.StartsWith("/approve ", StringComparison.OrdinalIgnoreCase))
            {
                var rest = content["/approve ".Length..].Trim();
                var spaceIdx = rest.IndexOf(' ');
                var approvalId = spaceIdx < 0 ? rest : rest[..spaceIdx];
                var feedback   = spaceIdx < 0 ? null : rest[(spaceIdx + 1)..].Trim();

                if (_hitlManager.Respond(approvalId, approved: true, feedback))
                {
                    await channel.SendReplyAsync(message, $"✅ Approved `{approvalId}`.");
                    return;
                }
            }
            // Mode 1: /reject <id> [reason]
            else if (content.StartsWith("/reject ", StringComparison.OrdinalIgnoreCase))
            {
                var rest = content["/reject ".Length..].Trim();
                var spaceIdx = rest.IndexOf(' ');
                var approvalId = spaceIdx < 0 ? rest : rest[..spaceIdx];
                var reason     = spaceIdx < 0 ? null : rest[(spaceIdx + 1)..].Trim();

                if (_hitlManager.Respond(approvalId, approved: false, reason))
                {
                    await channel.SendReplyAsync(message, $"❌ Rejected `{approvalId}`.");
                    return;
                }
            }
            // Mode 2: free-form reply to a request_human_input call
            else if (_hitlManager.HasPendingFreeForm(channelId))
            {
                if (_hitlManager.RespondFreeForm(channelId, content))
                    return;
            }
        }

        try
        {
            if (_gateway != null)
            {
                var task = await _gateway.ProcessChannelMessageAsync(message, channel, agent.Id);

                _logger?.LogInformation(
                    "Channel message routed through gateway: MessageId={MessageId}, State={State}",
                    message.Id,
                    task.State);
            }
            else
            {
                _logger?.LogInformation("Processing channel message via queue: {MessageId}", message.Id);
                var sessionChannelId = string.IsNullOrEmpty(message.ChannelId)
                    ? channel.ChannelId
                    : message.ChannelId;
                var sessionId = _sessionManager?.GetOrCreateChannelSession(
                    sessionChannelId, channel.Name, agent.Id)
                    ?? Guid.NewGuid().ToString("N");

                AgentResult result;
                if (_commandQueue != null)
                {
                    var tcs = new TaskCompletionSource<AgentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var cmd = AgentCommand.CreateMainCommand(sessionId, agent.Id, message.Content);
                    cmd.ResultSource = tcs;
                    _commandQueue.Enqueue(cmd);
                    result = await tcs.Task;
                }
                else
                {
                    result = await agent.ProcessAsync(message.Content, sessionId);
                }

                await channel.SendReplyAsync(message, result.Output);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error handling channel message: {MessageId}", message.Id);
            try
            {
                await channel.SendReplyAsync(message, $"Error processing request: {ex.Message}");
            }
            catch (Exception sendEx)
            {
                _logger?.LogError(sendEx, "Error sending error message to channel");
            }
        }
    }
}
