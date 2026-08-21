using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingAgent.Config;

namespace TradingAgent.Market;

/// <summary>
/// Deterministic PSX regular-market calendar. Regular sessions follow the published PSX schedule:
/// Monday-Thursday 09:32-15:30 PKT and Friday 09:17-12:00 plus 14:32-16:30 PKT.
/// Holidays and temporary schedules are supplied through configuration and take precedence.
/// </summary>
public sealed class PsxMarketCalendar : IMarketCalendar
{
    private static readonly TimeZoneInfo Pkt = PsxTime.Zone;
    private readonly IOptions<TradingAgentOptions> _options;
    private readonly ILogger<PsxMarketCalendar> _logger;

    public PsxMarketCalendar(
        IOptions<TradingAgentOptions> options,
        ILogger<PsxMarketCalendar> logger)
    {
        _options = options;
        _logger = logger;
    }

    public MarketStatus GetStatus(DateTime? utcNow = null)
    {
        var utc = DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc);
        var pktNow = TimeZoneInfo.ConvertTimeFromUtc(utc, Pkt);
        var opts = _options.Value;

        try
        {
            var holidays = ParseHolidays(opts.MarketHolidays);
            var overrides = ParseOverrides(opts.MarketSessionOverrides);
            var date = DateOnly.FromDateTime(pktNow);

            if (overrides.TryGetValue(date, out var dayOverride))
            {
                if (dayOverride.Closed)
                    return new(false, pktNow, $"PSX is closed by configured override on {date:yyyy-MM-dd}.",
                        ScheduleSource: "override");

                return Evaluate(pktNow, dayOverride.Sessions, "override");
            }

            if (holidays.Contains(date))
                return new(false, pktNow, $"PSX is closed for configured holiday {date:yyyy-MM-dd}.",
                    ScheduleSource: "holiday");

            var sessions = RegularSessions(pktNow.DayOfWeek);
            return sessions.Count == 0
                ? new(false, pktNow, $"PSX is closed on {pktNow.DayOfWeek}s.")
                : Evaluate(pktNow, sessions, "regular");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MarketCalendar] Invalid market calendar configuration.");
            if (opts.FailClosedOnCalendarError)
                return new(false, pktNow, $"Market calendar unavailable: {ex.Message}",
                    ScheduleSource: "error");

            return Evaluate(pktNow, RegularSessions(pktNow.DayOfWeek), "regular-fallback");
        }
    }

    private static MarketStatus Evaluate(
        DateTime pktNow,
        IReadOnlyList<(TimeOnly Start, TimeOnly End)> sessions,
        string source)
    {
        var now = TimeOnly.FromDateTime(pktNow);
        foreach (var session in sessions)
        {
            if (now >= session.Start && now < session.End)
                return new(true, pktNow,
                    $"PSX regular market is open until {session.End:HH:mm} PKT.",
                    ScheduleSource: source);

            if (now < session.Start)
            {
                var next = pktNow.Date.Add(session.Start.ToTimeSpan());
                return new(false, pktNow,
                    $"PSX regular market is closed. Next session opens at {session.Start:HH:mm} PKT.",
                    next, source);
            }
        }

        return new(false, pktNow, "PSX regular market is closed for the day.",
            ScheduleSource: source);
    }

    private static IReadOnlyList<(TimeOnly Start, TimeOnly End)> RegularSessions(DayOfWeek day) =>
        day switch
        {
            DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday =>
            [
                (new TimeOnly(9, 32), new TimeOnly(15, 30))
            ],
            DayOfWeek.Friday =>
            [
                (new TimeOnly(9, 17), new TimeOnly(12, 0)),
                (new TimeOnly(14, 32), new TimeOnly(16, 30))
            ],
            _ => []
        };

    private static HashSet<DateOnly> ParseHolidays(IEnumerable<string> values)
    {
        var result = new HashSet<DateOnly>();
        foreach (var value in values)
        {
            if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                throw new InvalidOperationException($"Invalid market holiday '{value}'. Expected yyyy-MM-dd.");
            result.Add(date);
        }
        return result;
    }

    private static Dictionary<DateOnly, (bool Closed, IReadOnlyList<(TimeOnly Start, TimeOnly End)> Sessions)>
        ParseOverrides(IEnumerable<MarketSessionOverride> values)
    {
        var result = new Dictionary<DateOnly, (bool, IReadOnlyList<(TimeOnly, TimeOnly)>)>();
        foreach (var value in values)
        {
            if (!DateOnly.TryParseExact(value.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                throw new InvalidOperationException($"Invalid market override date '{value.Date}'.");

            var sessions = value.Sessions.Select(ParseSession).OrderBy(s => s.Start).ToList();
            if (!value.Closed && sessions.Count == 0)
                throw new InvalidOperationException($"Market override {value.Date} must be closed or define sessions.");

            result[date] = (value.Closed, sessions);
        }
        return result;
    }

    private static (TimeOnly Start, TimeOnly End) ParseSession(string text)
    {
        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end)
            || start >= end)
            throw new InvalidOperationException($"Invalid market session '{text}'. Expected HH:mm-HH:mm.");

        return (start, end);
    }

}
