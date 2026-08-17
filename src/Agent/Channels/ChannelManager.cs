using AgentFox.Agents;
using AgentFox.Hitl;
using AgentFox.Models;
using AgentFox.Plugins;
using AgentFox.Plugins.Channels;
using AgentFox.Plugins.Interfaces;
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
    private PluginConfigManager? _pluginConfigManager;
    private readonly IAgentRegistry? _agentRegistry;

    public IReadOnlyDictionary<string, Channel> Channels => _channels;
    public ChannelMessageGateway? Gateway => _gateway;

    /// <summary>
    /// Resolves by channel id first, then display name, then type. Id comes first because it is the
    /// only one guaranteed unique — two Telegram bots share a name and a type, and a caller that
    /// knows the id means that specific channel.
    /// </summary>
    public Channel? GetChannelByName(string name) =>
        _channels.Values.FirstOrDefault(c =>
            c.ChannelId.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? _channels.Values.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            c.Type.Equals(name, StringComparison.OrdinalIgnoreCase));

    public ChannelManager(
        Func<FoxAgent?> agentFactory,
        SessionManager? sessionManager = null,
        ICommandQueue? commandQueue = null,
        IAgentRegistry? agentRegistry = null,
        ILogger? logger = null)
    {
        _agentFactory = agentFactory;
        _sessionManager = sessionManager;
        _commandQueue = commandQueue;
        _agentRegistry = agentRegistry;
        _logger = logger;
    }

    public ChannelManager(
        FoxAgent agent,
        SessionManager? sessionManager = null,
        ICommandQueue? commandQueue = null,
        IAgentRegistry? agentRegistry = null,
        ILogger? logger = null)
        : this(() => agent, sessionManager, commandQueue, agentRegistry, logger)
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

    /// <summary>
    /// Wires in plugin config access so incoming channel messages can drive the trading
    /// kill switch (/killswitch on|off) without going through the LLM.
    /// </summary>
    public void SetPluginConfigManager(PluginConfigManager pluginConfigManager) =>
        _pluginConfigManager = pluginConfigManager;

    public void AddChannel(Channel channel)
    {
        // Providers mint their own ids, and several are not unique per instance: Telegram hardcodes
        // "telegram", so a second bot used to replace the first in this dictionary — registered,
        // connected, polling, and unreachable by every send. Suffixing keeps both addressable, and
        // the warning says which entry needs an explicit Name in config.
        if (string.IsNullOrWhiteSpace(channel.ChannelId))
            channel.ChannelId = channel.Type;

        if (_channels.ContainsKey(channel.ChannelId))
        {
            var baseId = channel.ChannelId;
            var suffix = 2;
            while (_channels.ContainsKey($"{baseId}#{suffix}"))
                suffix++;

            channel.ChannelId = $"{baseId}#{suffix}";
            _logger?.LogWarning(
                "AddChannel: id '{BaseId}' is already registered; this channel is now '{ChannelId}'. "
                + "Give it a \"Name\" in config to pin a stable id for subscriptions.",
                baseId, channel.ChannelId);
        }

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

    /// <summary>
    /// The single place a topic turns into a recipient list. Every fan-out in the process goes
    /// through here, so the delivery policy lives in one readable block instead of being restated
    /// at each call site.
    ///
    /// <para>Three cases, in order:</para>
    /// <list type="number">
    ///   <item><b>No topic</b> — an unaddressed notification, which is what every caller predating
    ///         subscriptions sends. Reaches every connected channel, unchanged.</item>
    ///   <item><b>Matched</b> — the channels whose subscriptions cover the topic.</item>
    ///   <item><b>Matched nothing</b> — normally an empty list, and the caller's zero return is the
    ///         signal. For a <see cref="NotificationTopics.IsMandatory"/> topic it falls back to
    ///         every connected channel: those are questions whose answer the agent is blocked on,
    ///         and a misconfigured filter must not be able to deadlock a turn.</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<Channel> ResolveRecipients(string? topic)
    {
        var connected = _channels.Values.Where(c => c.IsConnected).ToList();

        if (string.IsNullOrWhiteSpace(topic) || connected.Count == 0)
            return connected;

        var matched = connected.Where(c => c.Subscriptions.Matches(topic)).ToList();
        if (matched.Count > 0)
            return matched;

        if (NotificationTopics.IsMandatory(topic))
        {
            _logger?.LogWarning(
                "No channel subscribes to '{Topic}', which cannot go undelivered — falling back to "
                + "all {Count} connected channel(s). Add it to a channel's subscriptions to silence this.",
                topic, connected.Count);
            return connected;
        }

        // Worth a warning rather than a debug line: this is the failure mode of subject routing.
        // Nothing throws, nothing retries, the message is simply gone — and a filter one character
        // off from the topic looks correct in config for as long as nobody reads the logs.
        _logger?.LogWarning(
            "Notification on '{Topic}' matched none of the {Count} connected channel(s) and was dropped. "
            + "Check the subscription filters against the published topic.",
            topic, connected.Count);

        return matched;
    }

    /// <summary>
    /// Sends the same message to every channel subscribed to <paramref name="topic"/> — used for
    /// HITL approval notifications so any configured surface (not just the originating one) can act
    /// on them. Mirrors <c>NotifyUserTool</c>'s broadcast pattern. Per-channel failures are logged
    /// and swallowed so one broken channel cannot suppress the notification on the others. Returns
    /// the number of channels the message was actually sent to.
    /// </summary>
    /// <param name="topic">
    /// Dot-separated subject, e.g. <c>trading.order.accepted</c>. Null or blank delivers to every
    /// connected channel regardless of subscription.
    /// </param>
    public async Task<int> BroadcastAsync(string message, string? topic = null)
    {
        var sent = 0;
        foreach (var channel in ResolveRecipients(topic))
        {
            try
            {
                await channel.SendToTargetAsync(string.Empty, message);
                sent++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "BroadcastAsync: failed to deliver to channel {Channel}", channel.Type);
            }
        }
        return sent;
    }

    /// <summary>
    /// Like <see cref="BroadcastAsync"/>, but gives channels that support interactive UI
    /// (Discord buttons, Telegram inline keyboards) a chance to render <paramref name="actions"/>
    /// as one-click controls instead of requiring the user to type a command back.
    /// </summary>
    public async Task<int> BroadcastActionableAsync(
        string message,
        IReadOnlyList<ChannelAction> actions,
        string? topic = null)
    {
        var sent = 0;
        foreach (var channel in ResolveRecipients(topic))
        {
            try
            {
                await channel.SendActionableAsync(message, actions);
                sent++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "BroadcastActionableAsync: failed to deliver to channel {Channel}", channel.Type);
            }
        }
        return sent;
    }

    private async Task HandleMessage(Channel channel, ChannelMessage message)
    {
        var agent = _agentFactory();
        if (agent == null)
        {
            _logger?.LogWarning("HandleMessage: agent not yet available, dropping message {MessageId}", message.Id);
            return;
        }
        var messageContent = message.Content ?? string.Empty;

        // ── Trading kill switch — deterministic control command, never reaches the LLM ──
        // Intentionally coupled to the trading plugin's "trading-agent"/"killSwitch" config key
        // rather than a generic command registry: this is the one cross-plugin control-plane
        // command that needs to work even if the agent loop is busy, stuck, or misbehaving.
        if (_pluginConfigManager != null)
        {
            var ksContent = messageContent.Trim();
            var isKillSwitchCommand =
                ksContent.StartsWith("/killswitch ", StringComparison.OrdinalIgnoreCase) ||
                ksContent.StartsWith("killswitch ", StringComparison.OrdinalIgnoreCase);

            if (isKillSwitchCommand)
            {
                var rest = ksContent[(ksContent.IndexOf(' ') + 1)..].Trim();
                var spaceIdx = rest.IndexOf(' ');
                var stateWord = (spaceIdx < 0 ? rest : rest[..spaceIdx]).ToLowerInvariant();
                var reason = spaceIdx < 0 ? null : rest[(spaceIdx + 1)..].Trim();

                if (stateWord is "on" or "off")
                {
                    var active = stateWord == "on";
                    await _pluginConfigManager.MergeConfigAsync("trading-agent", new Dictionary<string, object?>
                    {
                        ["killSwitch"] = active
                    });
                    _logger?.LogWarning(
                        "[ChannelManager] Trading kill switch {State} via channel command. Reason: {Reason}",
                        active ? "ACTIVATED" : "cleared", reason ?? "(none given)");
                    await channel.SendReplyAsync(message, active
                        ? "🛑 Kill switch ACTIVATED — all trading orders blocked."
                        : "✅ Kill switch cleared — trading resumes per normal policy.");
                    return;
                }

                await channel.SendReplyAsync(message, "Usage: `/killswitch on [reason]` or `/killswitch off [reason]`");
                return;
            }
        }

        // ── HITL interception — runs before gateway/queue routing ─────────────
        if (_hitlManager != null)
        {
            var channelId = string.IsNullOrEmpty(message.ChannelId)
                ? channel.ChannelId
                : message.ChannelId;
            var content = messageContent.Trim();

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
            // Immediate receipt acknowledgement — the full agent turn can take minutes
            // (browser automation, LLM calls), so confirm up front that the message landed
            // and is being worked on. Buffered by the channel if it's momentarily down.
            try
            {
                await channel.SendReplyAsync(message, "📥 Received — processing…");
            }
            catch (Exception ackEx)
            {
                _logger?.LogWarning(ackEx, "Failed to send receipt ack for {MessageId}", message.Id);
            }

            var specialist = _agentRegistry?.ResolveForChannel(channel.Type);
            if (specialist is not null)
            {
                var sessionChannelId = string.IsNullOrEmpty(message.ChannelId)
                    ? channel.ChannelId
                    : message.ChannelId;
                var sessionId = _sessionManager?.GetOrCreateChannelSession(
                    sessionChannelId, $"{channel.Name}:{specialist.Id}", specialist.Id)
                    ?? Guid.NewGuid().ToString("N");
                string response;
                if (_commandQueue is not null)
                {
                    var command = new SpecialistAgentCommand
                    {
                        SessionKey = sessionId,
                        AgentId = specialist.Id,
                        Input = messageContent,
                        TimeoutSeconds = specialist.TimeoutSeconds
                    };
                    _commandQueue.Enqueue(command);
                    response = await command.ResultSource.Task;
                }
                else
                {
                    response = await _agentRegistry!.RunAsync(
                        specialist.Id, messageContent, sessionId);
                }
                await channel.SendReplyAsync(message, response);
                _logger?.LogInformation(
                    "Channel message {MessageId} routed directly to specialist {AgentId}.",
                    message.Id, specialist.Id);
                return;
            }

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
                    var cmd = AgentCommand.CreateMainCommand(sessionId, agent.Id, messageContent);
                    cmd.ResultSource = tcs;
                    _commandQueue.Enqueue(cmd);
                    result = await tcs.Task;
                }
                else
                {
                    result = await agent.ProcessAsync(messageContent, sessionId);
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
