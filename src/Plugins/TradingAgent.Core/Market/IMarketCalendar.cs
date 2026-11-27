namespace TradingAgent.Market;

public readonly record struct MarketStatus(
    bool IsOpen,
    DateTime PktNow,
    string Reason,
    DateTime? NextOpenPkt = null,
    string ScheduleSource = "regular",
    DateTime? SessionOpenPkt = null);

public interface IMarketCalendar
{
    MarketStatus GetStatus(DateTime? utcNow = null);

    /// <summary>
    /// Whether <paramref name="date"/> is a trading session at all, independent of the time of day.
    ///
    /// <para>
    /// Needed to project FUTURE session timestamps — a chart projection has to land on real sessions,
    /// never on a weekend or a configured holiday. It cannot be derived from
    /// <see cref="GetStatus"/> by probing a future date at some fixed clock time, because the session
    /// windows differ by day: a probe at 13:00 would read Friday as closed and drop a real trading
    /// day from the projection.
    /// </para>
    ///
    /// <para>
    /// The default covers the weekly shape only and knows nothing about holidays. That is adequate
    /// for a test double; a real calendar MUST override it, as PsxMarketCalendar does, or projections
    /// will be drawn on market holidays.
    /// </para>
    /// </summary>
    bool IsTradingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// The PKT sessions (start, end) the calendar holds the market open for on <paramref name="date"/>, in order, with the same
    /// precedence <see cref="GetStatus"/> applies: an override, then a holiday (no sessions), then the regular weekly shape. An
    /// empty list means no session that day.
    ///
    /// <para>
    /// The default answers <c>null</c>, "not known", rather than inventing hours: a test double that never declared its hours must
    /// not be read as one that has none. A real calendar overrides it, as <see cref="PsxMarketCalendar"/> does.
    /// </para>
    /// </summary>
    IReadOnlyList<(TimeOnly Start, TimeOnly End)>? SessionsOn(DateOnly date) => null;
}
