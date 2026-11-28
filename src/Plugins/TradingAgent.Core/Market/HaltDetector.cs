namespace TradingAgent.Market;

/// <summary>Where the venue's trading session stands, as far as the agent can tell.</summary>
public enum MarketPhase
{
    /// <summary>Outside a scheduled session.</summary>
    Closed,

    /// <summary>Inside a scheduled session and trading.</summary>
    Open,

    /// <summary>
    /// Inside a scheduled session, but trading has stopped market-wide. PSX halts when the KSE-30 moves 5% from the
    /// previous close (owner, 2026-11-28; worst case assumed until the broker answers: every resting order is
    /// cancelled and new ones are refused).
    /// </summary>
    Halted,

    /// <summary>
    /// Just reopened after a halt: native stops are re-armed where the price is still above them, while local
    /// backstops wait out the settling minutes unless the price is below the catastrophic level.
    /// </summary>
    ReopenSettling,

    /// <summary>
    /// A halt nothing has confirmed ending within <c>HaltMaxMinutes</c>. Workers act normally — holding protection back
    /// on an unconfirmed belief is worse than an order the venue refuses — and the owner is told.
    /// </summary>
    Unknown
}

/// <summary>A source's reading and when it was taken. A null token means the source said nothing.</summary>
public sealed record StateObservation(string? Token, DateTime? ObservedUtc);

/// <summary>The KSE-30's move from the previous close, and when the index last ticked.</summary>
public sealed record IndexMove(decimal? ChangePercent, DateTime? LastTickUtc);

/// <summary>Everything one evaluation reads.</summary>
/// <param name="CalendarOpen">The scheduled session is running.</param>
/// <param name="SessionOpenedUtc">When the current scheduled session (or Friday's half) opened, for the edge grace.</param>
public sealed record HaltInputs(
    DateTime NowUtc,
    bool CalendarOpen,
    DateTime? SessionOpenedUtc,
    StateObservation Broker,
    StateObservation Analytics,
    IndexMove Kse30);

/// <param name="IndexMovePercent">The KSE-30 move that starts a halt, either direction. Default 5.</param>
/// <param name="ReadingFreshness">How old a state reading may be and still count. Default 3 minutes.</param>
/// <param name="SessionEdgeGrace">After a session opens, a broker "Break" is the scheduled pre-open break running
/// a few seconds late, not a halt (AKD: Break 09:30, OPN 09:32). Default 3 minutes.</param>
public sealed record HaltOptions(
    decimal IndexMovePercent = 5m,
    TimeSpan? ReadingFreshness = null,
    TimeSpan? SessionEdgeGrace = null,
    TimeSpan? ReopenSettling = null,
    TimeSpan? HaltMax = null)
{
    public TimeSpan Freshness => ReadingFreshness ?? TimeSpan.FromMinutes(3);
    public TimeSpan EdgeGrace => SessionEdgeGrace ?? TimeSpan.FromMinutes(3);
    public TimeSpan Settling => ReopenSettling ?? TimeSpan.FromMinutes(10);
    public TimeSpan MaxHalt => HaltMax ?? TimeSpan.FromMinutes(90);
}

/// <summary>The phase, since when, what said so, and the session's halt history.</summary>
/// <param name="IndexHaltSpent">An index move already started a halt this session. The move persists after the reopen, so
/// letting it trigger again would read every later minute as halted; after the first, only a state source may.</param>
/// <param name="Conflict">A fresh broker reading contradicted the index (Q6: the broker is trusted, the owner told).</param>
public sealed record MarketPhaseReading(
    MarketPhase Phase,
    DateTime SinceUtc,
    string Source,
    string Reason,
    DateOnly? Session = null,
    bool IndexHaltSpent = false,
    string? Conflict = null)
{
    public static MarketPhaseReading Initial(DateTime nowUtc) =>
        new(MarketPhase.Closed, nowUtc, "start", "Nothing has been read yet.");
}

/// <summary>
/// Decides the market phase from the broker's own state, the analytics feed's state and the KSE-30's move. Pure: every
/// input, including the clock and the previous reading, is passed in, so each rule has a test.
///
/// <para><b>What starts a halt</b>, only inside a scheduled session and outside its opening grace:</para>
/// <list type="bullet">
/// <item>a fresh broker reading of HALT, HALTED, SUS, SUSPENDED or Break — the TradeCast reader's own "closed" tokens
/// minus the session's scheduled ends;</item>
/// <item>a fresh analytics reading of SUS;</item>
/// <item>the KSE-30 at or beyond the threshold either way, once per session — unless a broker reading taken at or after
/// the index's last tick says the market is open, in which case the broker is trusted and the conflict reported
/// (owner Q6, 2026-11-28).</item>
/// </list>
///
/// <para><b>What ends one:</b> a fresh broker reading that accepts orders, or a fresh analytics OPN. The index cannot:
/// its move outlasts the halt. Nothing confirming a reopen within <see cref="HaltOptions.MaxHalt"/> gives
/// <see cref="MarketPhase.Unknown"/>.</para>
///
/// <para>
/// <b>Not measured, and said so:</b> which token a TradeCast house actually sends during a market-wide halt, and what
/// the analytics feed's state shows. Both are read if they come; neither is required. The index rule is what does not
/// depend on either.
/// </para>
/// </summary>
public static class HaltDetector
{
    private static readonly HashSet<string> BrokerHaltTokens = new(StringComparer.OrdinalIgnoreCase)
        { "HALT", "HALTED", "SUS", "SUSPENDED", "BREAK" };

    private static readonly HashSet<string> BrokerOpenTokens = new(StringComparer.OrdinalIgnoreCase)
        { "OPN", "OPEN", "NORMAL", "CONTINUOUS" };

    public static MarketPhaseReading Evaluate(HaltInputs input, MarketPhaseReading previous, HaltOptions options)
    {
        var now = input.NowUtc;
        var session = DateOnly.FromDateTime(now.AddHours(5));
        // The index may start one halt per session; a new session forgets the last.
        var indexSpent = previous.Session == session && previous.IndexHaltSpent;

        if (!input.CalendarOpen)
            return Keep(previous, MarketPhase.Closed, now, "calendar", "Outside a scheduled session.", session, indexSpent);

        bool Fresh(StateObservation o) => o.ObservedUtc is { } at && now - at <= options.Freshness;
        var brokerSaysHalt = Fresh(input.Broker) && input.Broker.Token is { } b && BrokerHaltTokens.Contains(b.Trim());
        var brokerSaysOpen = Fresh(input.Broker) && input.Broker.Token is { } o && BrokerOpenTokens.Contains(o.Trim());
        var analyticsSaysHalt = Fresh(input.Analytics) && string.Equals(input.Analytics.Token?.Trim(), "SUS", StringComparison.OrdinalIgnoreCase);
        var analyticsSaysOpen = Fresh(input.Analytics) && string.Equals(input.Analytics.Token?.Trim(), "OPN", StringComparison.OrdinalIgnoreCase);

        var inEdgeGrace = input.SessionOpenedUtc is { } opened && now - opened < options.EdgeGrace;

        var indexBreached = input.Kse30.ChangePercent is { } move && Math.Abs(move) >= options.IndexMovePercent
                            && input.Kse30.LastTickUtc is { } tick && DateOnly.FromDateTime(tick.AddHours(5)) == session;
        // The broker outranks the index only with a reading taken at or after the index's last tick (Q6).
        var brokerOverrulesIndex = brokerSaysOpen && input.Broker.ObservedUtc >= input.Kse30.LastTickUtc;

        switch (previous.Phase)
        {
            case MarketPhase.Halted:
                if (brokerSaysOpen || analyticsSaysOpen)
                    return new(MarketPhase.ReopenSettling, now, brokerSaysOpen ? "broker" : "analytics",
                        $"Trading resumed: {(brokerSaysOpen ? $"the broker reports {input.Broker.Token}" : "the analytics feed reports OPN")}.",
                        session, indexSpent);
                if (now - previous.SinceUtc >= options.MaxHalt)
                    return new(MarketPhase.Unknown, now, "timeout",
                        $"Halted since {previous.SinceUtc:HH:mm} UTC and nothing has confirmed a reopen for "
                        + $"{options.MaxHalt.TotalMinutes:0} minutes. Acting normally; orders the venue refuses are refused.",
                        session, indexSpent);
                return previous with { Session = session, IndexHaltSpent = indexSpent };

            case MarketPhase.ReopenSettling:
                if (brokerSaysHalt || analyticsSaysHalt)
                    return Halt(brokerSaysHalt ? "broker" : "analytics", Describe(input, brokerSaysHalt), indexSpent);
                if (now - previous.SinceUtc >= options.Settling)
                    return new(MarketPhase.Open, now, "settled",
                        $"{options.Settling.TotalMinutes:0} minutes since the reopen; trading normally.", session, indexSpent);
                return previous with { Session = session, IndexHaltSpent = indexSpent };
        }

        // Open, Closed (a session just started) or Unknown.
        if (!inEdgeGrace && brokerSaysHalt)
            return Halt("broker", Describe(input, brokerSaysHalt: true), indexSpent);
        if (!inEdgeGrace && analyticsSaysHalt)
            return Halt("analytics", Describe(input, brokerSaysHalt: false), indexSpent);

        if (indexBreached && !indexSpent)
        {
            if (brokerOverrulesIndex)
                return new(MarketPhase.Open, previous.Phase == MarketPhase.Open ? previous.SinceUtc : now, "broker",
                    $"The KSE-30 is {input.Kse30.ChangePercent:+0.00;-0.00}% from yesterday's close, past the "
                    + $"{options.IndexMovePercent:0.##}% halt level, but the broker reports {input.Broker.Token} after it; "
                    + "the broker is trusted.", session, indexSpent,
                    Conflict: $"KSE-30 {input.Kse30.ChangePercent:+0.00;-0.00}% vs broker {input.Broker.Token}");
            return Halt("index",
                $"The KSE-30 is {input.Kse30.ChangePercent:+0.00;-0.00}% from yesterday's close, past the "
                + $"{options.IndexMovePercent:0.##}% level at which PSX halts trading.", indexSpent: true);
        }

        if (previous.Phase == MarketPhase.Unknown && !(brokerSaysOpen || analyticsSaysOpen))
            return previous with { Session = session, IndexHaltSpent = indexSpent };

        return Keep(previous, MarketPhase.Open, now, "calendar", "Inside a scheduled session.", session, indexSpent);

        MarketPhaseReading Halt(string source, string reason, bool indexSpent) =>
            new(MarketPhase.Halted, now, source, reason, session, indexSpent);
    }

    private static string Describe(HaltInputs input, bool brokerSaysHalt) => brokerSaysHalt
        ? $"The broker reports the market as {input.Broker.Token} during a scheduled session."
        : "The analytics feed reports the market as suspended during a scheduled session.";

    /// <summary>The same phase keeps its original "since"; a change starts a new one.</summary>
    private static MarketPhaseReading Keep(
        MarketPhaseReading previous, MarketPhase phase, DateTime now, string source, string reason, DateOnly session, bool indexSpent) =>
        previous.Phase == phase
            ? previous with { Session = session, IndexHaltSpent = indexSpent, Conflict = null }
            : new(phase, now, source, reason, session, indexSpent);
}
