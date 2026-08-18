using System.Collections.Concurrent;
using System.Text;
using AgentFox.Agents;
using AgentFox.Channels;
using AgentFox.Plugins.Channels;
using AgentFox.Plugins.Interfaces;
using AgentFox.Sessions;
using Microsoft.Extensions.Logging;

namespace AgentFox.Tools;

public class NotifyUserTool : BaseTool
{
    private readonly ChannelManager _channelManager;
    private readonly SessionManager? _sessionManager;
    private readonly ILogger? _logger;
    private readonly bool _allowSubAgentSends;
    private readonly TimeSpan _duplicateWindow;
    private readonly double _duplicateThreshold;

    /// <summary>
    /// Messages delivered per session, kept only for <see cref="_duplicateWindow"/> so a repeat
    /// send can be recognised. Keyed by session so two unrelated conversations never interfere.
    /// </summary>
    private readonly ConcurrentDictionary<string, List<SentMessage>> _recentSends = new();

    /// <summary>Bounds memory if a long-lived process accumulates many sessions.</summary>
    private const int MaxTrackedSessions = 256;

    private sealed record SentMessage(HashSet<string> Shingles, DateTime At);

    /// <param name="sessionManager">
    /// Used to tell a sub-agent session from a top-level one. Null disables that check, which is
    /// what tests and embedded hosts without session tracking want.
    /// </param>
    /// <param name="allowSubAgentSends">
    /// When false (the default) a sub-agent cannot deliver to the user's channels directly; its
    /// result travels back to the parent, which decides what to send. See the class remarks.
    /// </param>
    /// <param name="duplicateWindow">
    /// How long a delivered message stays on record for repeat detection. A resend inside this
    /// window that is near-identical to something already sent is refused.
    /// </param>
    /// <param name="duplicateThreshold">
    /// Similarity (0–1) above which two messages count as the same. Compared on word shingles, so
    /// a report where only a few numbers changed still scores high.
    /// </param>
    public NotifyUserTool(
        ChannelManager channelManager,
        ILogger? logger = null,
        SessionManager? sessionManager = null,
        bool allowSubAgentSends = false,
        TimeSpan? duplicateWindow = null,
        double duplicateThreshold = 0.9)
    {
        _channelManager = channelManager;
        _logger = logger;
        _sessionManager = sessionManager;
        _allowSubAgentSends = allowSubAgentSends;
        _duplicateWindow = duplicateWindow ?? TimeSpan.FromMinutes(5);
        _duplicateThreshold = duplicateThreshold;
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
                "Send a notification or message to the user via the connected channels. " +
                $"Active channels: {channelList}. " +
                "Use this for alerts, cron job results, status updates, summaries, or any message " +
                "intended for the user. Send each update ONCE — the same content is not delivered " +
                "twice for the same task.";
        }
    }

    public override Dictionary<string, ToolParameter> Parameters
    {
        get
        {
            // Computed per access, like the description: the registry grows as plugins declare
            // their topics, and a stale enum here is how the model ends up publishing to a subject
            // nothing subscribes to.
            var topics = NotificationTopics.Known
                .Select(t => t.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            return new()
            {
                ["message"] = new()
                {
                    Type = "string",
                    Description = "The message to deliver to the user. Markdown is supported on Telegram and Discord.",
                    Required = true
                },
                ["topic"] = new()
                {
                    Type = "string",
                    Description =
                        "Optional subject that decides which channels receive this. Leave empty for " +
                        $"the default '{NotificationTopics.AgentNotify}', which is right for almost " +
                        "everything. Only set it to route a message to the channels watching a " +
                        "specific subject, and only to a topic listed here — an unlisted topic that " +
                        "nobody subscribes to is silently dropped. " +
                        (topics.Count > 0 ? $"Known topics: {string.Join(", ", topics)}." : string.Empty),
                    Required = false,
                    EnumValues = topics.Count > 0 ? topics : null
                }
            };
        }
    }

    protected override async Task<ToolResult> ExecuteInternalAsync(Dictionary<string, object?> arguments)
    {
        var message = arguments.GetValueOrDefault("message")?.ToString();
        if (string.IsNullOrWhiteSpace(message))
            return ToolResult.Fail("message is required");

        var sessionKey = FoxAgent.CurrentSessionKey.Value;

        // ── Sub-agents do not talk to the user's channels ─────────────────────
        // notify_user lives in the single shared tool registry, so a sub-agent inherits it. A
        // parent that delegates "gather the data and post the update" then gets two deliveries:
        // one from the sub-agent, one from its own turn. Results are meant to travel back to the
        // parent, which owns the decision about what reaches the user.
        if (!_allowSubAgentSends && IsSubAgentSession(sessionKey))
        {
            _logger?.LogInformation(
                "notify_user: refused for sub-agent session {SessionKey}", sessionKey);
            return ToolResult.Fail(
                "Sub-agents cannot deliver to the user's channels. Return your findings as your " +
                "result instead — the agent that spawned you will decide what to send. If you " +
                "were told to notify the user directly, ignore that part of the instruction and " +
                "say in your result that you did not send anything.");
        }

        // ── Refuse a near-identical resend of something just delivered ────────
        // An auto-continuation, a retried turn, or a stale todo item that still says "share the
        // update" all lead the model back here with the same report it already sent moments ago.
        if (sessionKey != null && TryFindRecentDuplicate(sessionKey, message, out var age))
        {
            _logger?.LogWarning(
                "notify_user: suppressed a near-duplicate resend in session {SessionKey} "
                + "({Age:F0}s after the original, {Length} chars)",
                sessionKey, age.TotalSeconds, message.Length);

            return ToolResult.Ok(
                $"Already delivered. This message is materially the same as one sent "
                + $"{age.TotalSeconds:F0} seconds ago in this session, so it was not sent again. "
                + "The user has it — treat the delivery as done and do not retry. If you meant to "
                + "send genuinely new information, say what changed and send only that.");
        }

        // ── Route by topic ───────────────────────────────────────────────────
        var topic = arguments.GetValueOrDefault("topic")?.ToString();
        topic = string.IsNullOrWhiteSpace(topic)
            ? NotificationTopics.AgentNotify
            : TopicFilter.Normalize(topic);

        if (!TopicFilter.IsValidTopic(topic, out var topicError))
            return ToolResult.Fail(
                $"{topicError} Omit 'topic' to use the default '{NotificationTopics.AgentNotify}'.");

        if (_channelManager.Channels.Values.All(c => !c.IsConnected))
            return ToolResult.Fail("No channels are connected. Use manage_channel to add a channel.");

        var channels = _channelManager.ResolveRecipients(topic);

        // Distinguished from "no channels at all" above: the channels exist and are up, they just
        // do not listen on this subject. Telling the model to retry would be wrong — nothing about
        // a resend changes the routing — so this reads as a configuration fact, not a failure.
        if (channels.Count == 0)
            return ToolResult.Fail(
                $"No connected channel subscribes to '{topic}', so nothing was sent. Either send on " +
                $"'{NotificationTopics.AgentNotify}' instead, or ask the user to subscribe a channel " +
                $"to '{topic}'. Do not retry this message unchanged.");

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

        // Only record a message that actually reached someone, so a total failure stays retryable.
        if (sessionKey != null && sent.Count > 0)
            RecordSend(sessionKey, message);

        if (failed.Count == 0)
            return ToolResult.Ok($"Notification delivered via: {string.Join(", ", sent)}.");

        if (sent.Count == 0)
            return ToolResult.Fail($"Failed to deliver via all channels: {string.Join(", ", failed)}.");

        return ToolResult.Ok(
            $"Partially delivered. Sent: {string.Join(", ", sent)}. " +
            $"Failed: {string.Join(", ", failed)}.");
    }

    private bool IsSubAgentSession(string? sessionKey)
    {
        if (string.IsNullOrEmpty(sessionKey)) return false;

        var origin = _sessionManager?.GetSession(sessionKey)?.Origin;
        if (origin.HasValue) return origin.Value == SessionOrigin.SubAgent;

        // No session index available (or the session is not registered yet) — fall back to the
        // id shape SessionManager.CreateSubAgentSession produces: "{agentId}/sa_{runId}".
        return sessionKey.Contains("/sa_", StringComparison.Ordinal)
            || sessionKey.StartsWith("sa_", StringComparison.Ordinal);
    }

    // ── Duplicate detection ──────────────────────────────────────────────────

    private bool TryFindRecentDuplicate(string sessionKey, string message, out TimeSpan age)
    {
        age = TimeSpan.Zero;
        if (!_recentSends.TryGetValue(sessionKey, out var history)) return false;

        var shingles = BuildShingles(message);
        if (shingles.Count == 0) return false;

        var now = DateTime.UtcNow;
        lock (history)
        {
            history.RemoveAll(m => now - m.At > _duplicateWindow);

            foreach (var previous in history)
            {
                if (Similarity(shingles, previous.Shingles) < _duplicateThreshold) continue;
                age = now - previous.At;
                return true;
            }
        }

        return false;
    }

    private void RecordSend(string sessionKey, string message)
    {
        var shingles = BuildShingles(message);
        if (shingles.Count == 0) return;

        if (_recentSends.Count >= MaxTrackedSessions && !_recentSends.ContainsKey(sessionKey))
            PruneEmptySessions();

        var history = _recentSends.GetOrAdd(sessionKey, _ => []);
        var now = DateTime.UtcNow;
        lock (history)
        {
            history.RemoveAll(m => now - m.At > _duplicateWindow);
            history.Add(new SentMessage(shingles, now));
        }
    }

    private void PruneEmptySessions()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, history) in _recentSends.ToArray())
        {
            bool empty;
            lock (history)
            {
                history.RemoveAll(m => now - m.At > _duplicateWindow);
                empty = history.Count == 0;
            }
            if (empty) _recentSends.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Overlapping 5-word shingles of the normalized message. Word-level shingles make a re-sent
    /// report recognisable even when its figures or timestamp shifted slightly, while a genuinely
    /// different message scores far below the threshold.
    ///
    /// Note the deliberate asymmetry: a few changed tokens barely dent the shingle set of a long
    /// report but dominate a one-liner, so suppression is strongest on exactly the messages that
    /// hurt most when duplicated. Two short status lines differing only in a number stay distinct.
    /// </summary>
    private static HashSet<string> BuildShingles(string message)
    {
        const int shingleSize = 5;

        var words = Normalize(message).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shingles = new HashSet<string>(StringComparer.Ordinal);

        if (words.Length < shingleSize)
        {
            foreach (var word in words) shingles.Add(word);
            return shingles;
        }

        for (var i = 0; i + shingleSize <= words.Length; i++)
            shingles.Add(string.Join(' ', words, i, shingleSize));

        return shingles;
    }

    /// <summary>Lowercases and collapses all whitespace runs to a single space.</summary>
    private static string Normalize(string message)
    {
        var sb = new StringBuilder(message.Length);
        var lastWasSpace = false;

        foreach (var c in message)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
            lastWasSpace = false;
        }

        return sb.ToString().Trim();
    }

    /// <summary>Jaccard similarity — shared shingles over total distinct shingles.</summary>
    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        var intersection = a.Count <= b.Count
            ? a.Count(b.Contains)
            : b.Count(a.Contains);

        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}
