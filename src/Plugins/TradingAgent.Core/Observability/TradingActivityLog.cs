using TradingAgent.Market;

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

/// <summary>Who an activity is for: the trader's view of the day, or the diagnostics behind it.</summary>
public enum ActivityAudience
{
    /// <summary>Touches the person's money, positions or what they were told: orders, stops, triggers, notifications.</summary>
    Trades,

    /// <summary>How the agent got there: account reads, the browser, the feed, candle sources, pass bookkeeping.</summary>
    System
}

/// <summary>One thing the trading agent did, or failed to do.</summary>
/// <param name="Seq">Monotonic id, newest highest.</param>
/// <param name="Utc">When it first happened.</param>
/// <param name="LastUtc">When it last happened — differs from <paramref name="Utc"/> only when repeated.</param>
/// <param name="Repeats">
/// How many further times this exact activity occurred inside the collapse window. Zero for the
/// usual case of something that happened once.
/// </param>
/// <param name="Source">Which part of the agent — "Broker", "Stops", "Armed", "Feed", "Orders", "Monitor", "Notified".</param>
/// <param name="Level">See <see cref="ActivityLevel"/>.</param>
/// <param name="Message">One line, written for a person reading a panel rather than a log.</param>
/// <param name="Detail">Optional second line: the reason, the error, the numbers.</param>
/// <param name="Symbol">The stock it is about, when it is about one; null otherwise. Never guessed from the message.</param>
/// <param name="Audience">"trades" or "system" — see <see cref="ActivityAudience"/> and <see cref="TradingActivityLog.AudienceFor"/>.</param>
/// <param name="Version">
/// Bumped whenever this entry is added or a repeat folds into it, from one counter for the whole log, so a reader can ask
/// for only what changed since the version it last saw.
/// </param>
public sealed record TradingActivity(
    long Seq,
    DateTime Utc,
    DateTime LastUtc,
    int Repeats,
    string Source,
    string Level,
    string Message,
    string? Detail,
    string? Symbol = null,
    string Audience = "system",
    long Version = 0);

/// <summary>
/// What the trading agent has been doing TODAY, for the UI's activity panel. Memory only, by the owner's decision
/// (2026-11-28): a restart clears it.
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
/// <b>Two audiences.</b> Each entry is either for the trader (an order, a stop, a trigger, a strategy action, a copy, a
/// notification sent to them) or a system diagnostic. <see cref="AudienceFor"/> decides, and a warning or an error is
/// always for the trader: a filter must never hide something actionable.
/// </para>
///
/// <para>
/// <b>Retention.</b> The current PSX (PKT) calendar day and at most <see cref="Capacity"/> entries, in memory; the first
/// read or write after midnight PKT empties it, and a restart drops it. The ledger, the execution events table and the
/// log file are the durable records — duplicating them would only add a way for the two to disagree.
/// </para>
/// </summary>
public sealed class TradingActivityLog
{
    /// <summary>Most entries kept: a busy trading day's worth once repeats fold, and a bound on memory.</summary>
    public const int Capacity = 1000;

    /// <summary>How long an identical activity folds into the existing entry rather than adding one.</summary>
    public static readonly TimeSpan CollapseWindow = TimeSpan.FromMinutes(10);

    /// <summary>The source every notification sent to the person is recorded under.</summary>
    public const string NotifiedSource = "Notified";

    /// <summary>Sources that are diagnostics however they are worded: how the agent reads and connects, not what it did.</summary>
    private static readonly HashSet<string> SystemSources =
        new(["Broker", "Feed", "Candles", "Monitor", "Market"], StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private readonly Func<DateTime> _now;

    /// <summary>Oldest first, so pruning is from the front and the collapse search is from the back.</summary>
    private readonly List<TradingActivity> _entries = [];

    private long _seq;
    private long _version;
    private DateOnly? _day;

    public TradingActivityLog() : this(() => DateTime.UtcNow) { }

    /// <summary>For tests: a log reading a given clock.</summary>
    public TradingActivityLog(Func<DateTime> utcNow) => _now = utcNow;

    /// <summary>Id of the newest entry.</summary>
    public long LastSeq { get { lock (_lock) return _seq; } }

    /// <summary>The newest change to any entry; see <see cref="TradingActivity.Version"/>.</summary>
    public long Version { get { lock (_lock) return _version; } }

    /// <summary>The PKT day the retained entries belong to — midnight PKT, as UTC.</summary>
    public DateTime KeptSinceUtc
    {
        get
        {
            var today = PsxTime.Today(_now());
            return TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), PsxTime.Zone);
        }
    }

    public void Info(string source, string message, string? detail = null, string? symbol = null) =>
        Record(ActivityLevel.Info, source, message, detail, symbol);

    public void Warn(string source, string message, string? detail = null, string? symbol = null) =>
        Record(ActivityLevel.Warn, source, message, detail, symbol);

    public void Error(string source, string message, string? detail = null, string? symbol = null) =>
        Record(ActivityLevel.Error, source, message, detail, symbol);

    /// <summary>
    /// Who an entry is for (pure). A warning or an error is always for the trader. Otherwise a notification is, and so is
    /// anything about one stock from a part of the agent that acts on it; the rest — reads, connections, pass bookkeeping
    /// such as "checking 3 protective stops" — is system.
    /// </summary>
    public static ActivityAudience AudienceFor(ActivityLevel level, string source, string? symbol)
    {
        if (level != ActivityLevel.Info) return ActivityAudience.Trades;
        if (string.Equals(source, NotifiedSource, StringComparison.OrdinalIgnoreCase)) return ActivityAudience.Trades;
        return symbol is not null && !SystemSources.Contains(source) ? ActivityAudience.Trades : ActivityAudience.System;
    }

    /// <summary>
    /// Records one activity. Deliberately total — it never throws and never blocks a caller for long,
    /// because every call site is on a path whose real job is placing or protecting an order, and
    /// observing that work must not be able to break it.
    /// </summary>
    public void Record(ActivityLevel level, string source, string message, string? detail = null, string? symbol = null,
        ActivityAudience? audience = null)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        var now      = _now();
        var src      = string.IsNullOrWhiteSpace(source) ? "Trading" : source.Trim();
        var text     = Truncate(message.Trim(), 300);
        var extra    = string.IsNullOrWhiteSpace(detail) ? null : Truncate(detail.Trim(), 600);
        var levelTag = level.ToString().ToLowerInvariant();
        var stock    = NormaliseSymbol(symbol);
        var forWhom  = (audience ?? AudienceFor(level, src, stock)) == ActivityAudience.Trades ? "trades" : "system";

        lock (_lock)
        {
            Prune(now);

            // Searched from the newest end: a repeat is nearly always recent, and stopping at the
            // window boundary keeps this O(few) rather than O(capacity).
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var candidate = _entries[i];
                if (now - candidate.LastUtc > CollapseWindow) break;

                if (candidate.Level == levelTag && candidate.Source == src && candidate.Symbol == stock
                    && candidate.Message == text && candidate.Detail == extra)
                {
                    // Kept in place with its original sequence rather than moved to the front. Moving
                    // it would make a recurring poll line permanently outrank the one-off events the
                    // panel exists to show.
                    _entries[i] = candidate with { LastUtc = now, Repeats = candidate.Repeats + 1, Version = ++_version };
                    return;
                }
            }

            _entries.Add(new TradingActivity(++_seq, now, now, 0, src, levelTag, text, extra, stock, forWhom, ++_version));
        }
    }

    /// <summary>
    /// Today's activities, newest first. With <paramref name="afterVersion"/> only the entries added or repeated since that
    /// version — a repeat changes an entry in place, so a reader merging by <see cref="TradingActivity.Seq"/> stays exact.
    /// <paramref name="afterSeq"/> is the older filter, kept for callers that still send it.
    /// </summary>
    public IReadOnlyList<TradingActivity> Snapshot(long afterSeq = 0, int limit = Capacity, long afterVersion = 0)
    {
        lock (_lock)
        {
            Prune(_now());
            return _entries
                .Where(e => e.Seq > afterSeq && e.Version > afterVersion)
                .OrderByDescending(e => e.Seq)
                .Take(Math.Clamp(limit, 1, Capacity))
                .ToList();
        }
    }

    /// <summary>The oldest entry still kept; a reader drops anything older it was holding. Null when empty.</summary>
    public long? OldestSeq { get { lock (_lock) { Prune(_now()); return _entries.Count == 0 ? null : _entries[0].Seq; } } }

    /// <summary>How many of today's entries are warnings and errors — the panel's badge.</summary>
    public (int Warnings, int Errors) IssueCounts()
    {
        lock (_lock)
        {
            Prune(_now());
            return (_entries.Count(e => e.Level == "warn"), _entries.Count(e => e.Level == "error"));
        }
    }

    /// <summary>
    /// Drops yesterday and anything over capacity. ASSUMES the lock is held. Pruning on write AND on read is deliberate: a
    /// quiet agent stops writing, and without the read-side pass yesterday's last entries would sit in the panel as today's.
    /// </summary>
    private void Prune(DateTime now)
    {
        var today = PsxTime.Today(now);
        if (_day != today)
        {
            // A new PKT day: the trader's "today" starts empty. Versions keep counting so a reader's last version never
            // looks newer than the log.
            _entries.Clear();
            _day = today;
        }

        if (_entries.Count > Capacity)
            _entries.RemoveRange(0, _entries.Count - Capacity);
    }

    /// <summary>A stock symbol as PSX writes it, or null for anything else — a symbol column must never show prose.</summary>
    private static string? NormaliseSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var s = symbol.Trim().ToUpperInvariant();
        return s.Length is >= 1 and <= 16 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? s : null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
