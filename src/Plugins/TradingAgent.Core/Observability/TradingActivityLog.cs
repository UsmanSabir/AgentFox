namespace TradingAgent.Observability;

/// <summary>How much attention an activity deserves.</summary>
public enum ActivityLevel
{
    /// <summary>Routine progress: a pass started, a page was read, an order went in.</summary>
    Info,

    /// <summary>Something did not work but the system carried on — a read failed, a stop was refused.</summary>
    Warn,

    /// <summary>Something the operator has to know about: an order or a protection is not where it should be.</summary>
    Error
}

/// <summary>One thing the trading agent did, or failed to do.</summary>
/// <param name="Seq">Monotonic id, newest highest.</param>
/// <param name="Utc">When it first happened.</param>
/// <param name="LastUtc">When it last happened — differs from <paramref name="Utc"/> only when repeated.</param>
/// <param name="Repeats">
/// How many further times this exact activity occurred inside the collapse window. Zero for the
/// usual case of something that happened once.
/// </param>
/// <param name="Source">Which part of the agent — "Broker", "Stops", "Armed", "Feed", "Orders", "Monitor".</param>
/// <param name="Level">See <see cref="ActivityLevel"/>.</param>
/// <param name="Message">One line, written for a person reading a panel rather than a log.</param>
/// <param name="Detail">Optional second line: the reason, the error, the numbers.</param>
public sealed record TradingActivity(
    long Seq,
    DateTime Utc,
    DateTime LastUtc,
    int Repeats,
    string Source,
    string Level,
    string Message,
    string? Detail);

/// <summary>
/// A small, self-pruning ring of what the trading agent has been doing, for the UI's activity panel.
///
/// <para>
/// <b>Why this is not just the log file.</b> The host binds <c>ILogger&lt;T&gt;</c> straight to a file
/// or console logger, so nothing in the process can observe log output — and the file's default
/// minimum level is Warning, which hides exactly the routine progress ("reading holdings", "browser
/// opened") that answers "is it doing anything right now". So the handful of moments worth watching
/// record here explicitly, in the words a person needs, rather than being scraped back out of logs.
/// </para>
///
/// <para>
/// <b>Repeats collapse.</b> The workers here are pollers: the protective-stop pass alone would post
/// the same half-dozen lines every three minutes and bury everything else within the hour. An
/// activity identical to one already recorded inside <see cref="CollapseWindow"/> bumps that entry's
/// count instead of adding a row — which also turns "it keeps opening the browser" from a wall of
/// identical lines into one line with a number on it, which is the more useful form of that fact.
/// </para>
///
/// <para>
/// <b>It forgets on purpose.</b> This is a live view, not an audit trail — the ledger, the execution
/// events table and the log file are the durable records, and duplicating them in memory would only
/// add a way for the two to disagree. Entries are dropped once there are more than
/// <see cref="Capacity"/> of them or they are older than <see cref="Retention"/>.
/// </para>
/// </summary>
public sealed class TradingActivityLog
{
    /// <summary>Most entries kept. A panel nobody scrolls does not need more, and this bounds memory.</summary>
    public const int Capacity = 120;

    /// <summary>How far back entries stay interesting. Older ones say nothing about what is happening now.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(2);

    /// <summary>How long an identical activity folds into the existing entry rather than adding one.</summary>
    public static readonly TimeSpan CollapseWindow = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();

    /// <summary>Oldest first, so pruning is from the front and the collapse search is from the back.</summary>
    private readonly List<TradingActivity> _entries = [];

    private long _seq;

    /// <summary>Id of the newest entry.</summary>
    public long LastSeq { get { lock (_lock) return _seq; } }

    public void Info(string source, string message, string? detail = null) =>
        Record(ActivityLevel.Info, source, message, detail);

    public void Warn(string source, string message, string? detail = null) =>
        Record(ActivityLevel.Warn, source, message, detail);

    public void Error(string source, string message, string? detail = null) =>
        Record(ActivityLevel.Error, source, message, detail);

    /// <summary>
    /// Records one activity. Deliberately total — it never throws and never blocks a caller for long,
    /// because every call site is on a path whose real job is placing or protecting an order, and
    /// observing that work must not be able to break it.
    /// </summary>
    public void Record(ActivityLevel level, string source, string message, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var now      = DateTime.UtcNow;
        var src      = string.IsNullOrWhiteSpace(source) ? "Trading" : source.Trim();
        var text     = Truncate(message.Trim(), 300);
        var extra    = string.IsNullOrWhiteSpace(detail) ? null : Truncate(detail.Trim(), 600);
        var levelTag = level.ToString().ToLowerInvariant();

        lock (_lock)
        {
            Prune(now);

            // Searched from the newest end: a repeat is nearly always recent, and stopping at the
            // window boundary keeps this O(few) rather than O(capacity).
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var candidate = _entries[i];
                if (now - candidate.LastUtc > CollapseWindow) break;

                if (candidate.Level == levelTag && candidate.Source == src
                    && candidate.Message == text && candidate.Detail == extra)
                {
                    // Kept in place with its original sequence rather than moved to the front. Moving
                    // it would make a recurring poll line permanently outrank the one-off events the
                    // panel exists to show.
                    _entries[i] = candidate with { LastUtc = now, Repeats = candidate.Repeats + 1 };
                    return;
                }
            }

            _entries.Add(new TradingActivity(++_seq, now, now, 0, src, levelTag, text, extra));
        }
    }

    /// <summary>
    /// The recent activities, newest first. <paramref name="afterSeq"/> lets a caller ask only for
    /// entries it has not seen; note that a collapsed repeat updates an existing entry in place, so a
    /// client that wants live repeat counts should read the whole window instead.
    /// </summary>
    public IReadOnlyList<TradingActivity> Snapshot(long afterSeq = 0, int limit = Capacity)
    {
        lock (_lock)
        {
            Prune(DateTime.UtcNow);
            return _entries
                .Where(e => e.Seq > afterSeq)
                .OrderByDescending(e => e.Seq)
                .Take(Math.Clamp(limit, 1, Capacity))
                .ToList();
        }
    }

    /// <summary>How many of the retained entries are warnings and errors — the panel's badge.</summary>
    public (int Warnings, int Errors) IssueCounts()
    {
        lock (_lock)
        {
            Prune(DateTime.UtcNow);
            return (_entries.Count(e => e.Level == "warn"), _entries.Count(e => e.Level == "error"));
        }
    }

    /// <summary>
    /// Drops what is over capacity or past retention. ASSUMES the lock is held. Pruning on write AND
    /// on read is deliberate: a quiet agent stops writing, and without the read-side pass its last few
    /// entries would sit in the panel looking current hours after the fact.
    /// </summary>
    private void Prune(DateTime now)
    {
        // Age is measured from the LAST occurrence, so a line that is still recurring stays.
        var cutoff = now - Retention;
        _entries.RemoveAll(e => e.LastUtc < cutoff);

        if (_entries.Count > Capacity)
            _entries.RemoveRange(0, _entries.Count - Capacity);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
