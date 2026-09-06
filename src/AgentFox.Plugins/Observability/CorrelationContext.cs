namespace AgentFox.Plugins.Observability;

/// <summary>
/// The ambient correlation id linking everything one request causes: the channel message that
/// started it, the agent turn, each tool call, an approval decision, and — in the trading plugin —
/// the proposal, execution and ledger-event rows that result.
///
/// <para>
/// <b>Why ambient rather than a parameter.</b> The causal chain crosses about twenty call sites
/// across two assemblies, most of which have no business knowing about correlation. Threading a
/// parameter through them would put the burden on every future caller to remember, and a caller
/// who forgot would produce an orphaned record with nothing to say so. Ambient means a new write
/// path is correlated by default and has to work to escape.
/// </para>
///
/// <para>
/// <b>Why it lives in AgentFox.Plugins.</b> An <see cref="AsyncLocal{T}"/> only correlates
/// anything if host and plugin share ONE static field, which means one type identity.
/// <c>PluginLoadContext</c> resolves <c>AgentFox.Plugins</c> from the host's default load context
/// rather than the plugin folder, so this assembly is the only place a shared ambient can live.
/// Put this class in the host and the trading plugin would silently get its own copy, always
/// reading null.
/// </para>
///
/// <para>
/// <b>Where the chain breaks, and what re-establishes it.</b> An <see cref="AsyncLocal{T}"/> flows
/// across <c>await</c>, but NOT across the command queue: a command is enqueued by one call chain
/// and dequeued by a long-running lane loop that never awaited the producer. That boundary is
/// bridged explicitly by carrying the id on the command (<c>ICommand.CorrelationId</c>) and
/// re-entering a scope in <c>CommandProcessor.ExecuteHandlerAsync</c>. Any other queue, timer or
/// background worker added later has the same break and needs the same two lines.
/// </para>
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The correlation id in force, or null when nothing has established one.</summary>
    public static string? Current => _current.Value;

    /// <summary>A fresh id. Short and hex so it is readable in a log line and a span attribute.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..16];

    /// <summary>
    /// Enters a correlation scope, restoring the previous value on dispose. A null or blank id
    /// mints one, so a caller at an entry point can write <c>using var _ = Begin(incoming)</c>
    /// without first deciding whether the caller upstream supplied anything.
    /// </summary>
    public static IDisposable Begin(string? correlationId = null)
    {
        var previous = _current.Value;
        _current.Value = string.IsNullOrWhiteSpace(correlationId) ? NewId() : correlationId;
        return new Scope(previous);
    }

    /// <summary>
    /// The id in force, minting and installing one if there is none. For background workers
    /// (reconciliation, feed, protective stops) whose work nothing upstream caused: their records
    /// are still worth grouping into one run, even though no channel message started them.
    /// </summary>
    public static string Ensure()
    {
        if (string.IsNullOrWhiteSpace(_current.Value))
            _current.Value = NewId();

        return _current.Value!;
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _current.Value = previous;
        }
    }
}
