using System.Globalization;
using Microsoft.Extensions.Logging;
using TradingAgent.AhlAnalytics;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;
using TradingAgent.Watchlist;

namespace TradingAgent.Trading;

/// <summary>
/// Which of the broker research service's daily bars may enter <c>daily_bars</c>, the RAW archive. Pure.
///
/// <para>
/// <b>Why any may, MEASURED 2026-10-13.</b> The service's history is corporate-action adjusted, which is why
/// <see cref="AhlCandleSource"/> never writes to the archive. But an adjustment only reaches bars BEFORE an action:
/// 10 stocks, sessions 2026-09-25 → 10-09, compared with the PSX bars already archived, were identical in open,
/// high, low, close and volume, 210 of 210, on AHL's and AKD's hosts alike — OGDC included, across its own
/// 2026-10-08 dividend. So a recent bar with no corporate action after it IS the raw bar. That mattered the day PSX
/// stopped answering the hosting machine (2026-10-12): every hosted agent's archive had stopped at 10-09.
/// </para>
///
/// <para>
/// <b>Three refusals, each the safe direction.</b> A bar with any payout ex date (cash, bonus or rights) after it is
/// not written, stricter than measured, until an adjustment is seen happening. A bar is not written at all for a
/// symbol whose broker series disagrees with ANY raw bar already archived for the same date: that means the series
/// was re-based, and the whole symbol waits. And a symbol whose payout history could not be read writes nothing,
/// because no history is not the same as no actions.
/// </para>
/// </summary>
public static class BrokerArchiveRules
{
    /// <summary>How far back a missing session is filled: about six trading weeks.</summary>
    public const int WindowDays = 45;

    public sealed record Selection(IReadOnlyList<PsxCandle> Bars, string? Refusal);

    public static Selection Select(
        IReadOnlyList<PsxCandle> archived,
        IReadOnlyList<PsxCandle> broker,
        IReadOnlyList<DateOnly>? actionDates,
        DateOnly settledThrough,
        DateOnly today)
    {
        if (actionDates is null) return new([], "its corporate actions could not be read");

        var raw = archived.ToDictionary(b => b.Date);
        foreach (var b in broker)
            if (raw.TryGetValue(b.Date, out var r) && !Same(r, b))
                return new([], $"the broker's {b.Date:yyyy-MM-dd} bar differs from the exchange's (close {b.Close} vs {r.Close})");

        var from = settledThrough.AddDays(-WindowDays);
        var ordered = broker.OrderBy(b => b.Date).ToList();
        var bars = new List<PsxCandle>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            if (b.IsLive || b.IsIntraday || b.Date > settledThrough || b.Date < from || raw.ContainsKey(b.Date)) continue;
            if (actionDates.Any(a => a > b.Date && a <= today)) continue;

            // Yesterday's close is the exchange's previous close only when no action went ex today; otherwise unknown.
            var prior = i > 0 ? ordered[i - 1] : null;
            var previous = prior is not null && !actionDates.Contains(b.Date)
                ? raw.TryGetValue(prior.Date, out var rp) ? rp.Close : prior.Close
                : (decimal?)null;
            bars.Add(new PsxCandle
            {
                Symbol = b.Symbol, Date = b.Date, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close,
                PreviousClose = previous, Volume = b.Volume
            });
        }
        return new(bars, null);
    }

    /// <summary>The dates a payout went (or goes) ex: the stated ex date, or the book-closure start when none is
    /// stated, whichever is earlier. Null when the history could not be read (an empty answer: every listed company
    /// has results rows, so empty is not "no actions").</summary>
    public static IReadOnlyList<DateOnly>? ActionDates(IReadOnlyList<AhlAnnouncement> payouts)
    {
        if (payouts.Count == 0) return null;
        var dates = new List<DateOnly>();
        foreach (var p in payouts)
        {
            if (!(Positive(p.Dividend) || Positive(p.Bonus) || Positive(p.RightPrice))) continue;
            var ex = Date(p.ExDate);
            var book = Date(p.BookClosureFrom);
            var at = ex is { } e && book is { } bk ? (e < bk ? e : bk) : ex ?? book;
            if (at is { } d) dates.Add(d);
        }
        return dates;
    }

    private static bool Same(PsxCandle raw, PsxCandle broker) =>
        Near(raw.Open, broker.Open) && Near(raw.High, broker.High) && Near(raw.Low, broker.Low)
        && Near(raw.Close, broker.Close) && raw.Volume == broker.Volume;

    private static bool Near(decimal a, decimal b) => Math.Abs(a - b) < 0.005m;

    private static bool Positive(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d > 0;

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// Fills the raw archive's recent gaps from the broker research service, per symbol, before each backfill pass
/// (<see cref="CandleBackfillRunner"/>; no worker of its own). It asks the broker only about symbols with a missing
/// session in the window, and only when a research token is already held — it never performs the sign-in itself,
/// for the same reason <see cref="AhlCandleSource.ReadyWithoutHandshake"/> gives. Rules: <see cref="BrokerArchiveRules"/>.
/// Writes go through <see cref="ITradingRepository.SaveDailyBarsAsync"/>, which records coverage for that symbol
/// only. Stores nothing of its own.
/// </summary>
public sealed class BrokerDailyBarFeeder(
    AhlCandleSource candles,
    AhlAnalyticsClient analytics,
    ITradingRepository repository,
    MonitoredUniverse universe,
    IMarketCalendar calendar,
    ILogger<BrokerDailyBarFeeder> logger)
{
    public sealed record Result(int Symbols, int Bars, IReadOnlyList<string> Refused, string? Skipped);

    public async Task<Result> FillAsync(DateOnly settledThrough, CancellationToken ct)
    {
        if (!candles.ReadyWithoutHandshake)
            return new(0, 0, [], "the broker's research service is not signed in");

        var today = DateOnly.FromDateTime(PsxTime.Now());
        var from = settledThrough.AddDays(-BrokerArchiveRules.WindowDays);
        var sessions = new List<DateOnly>();
        for (var d = from; d <= settledThrough; d = d.AddDays(1))
            if (calendar.IsTradingDay(d)) sessions.Add(d);

        var symbols = 0; var written = 0; var refused = new List<string>();
        foreach (var symbol in await universe.ForArchiveAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            var archived = (await repository.GetDailyBarsAsync(symbol, 60, ct)).Where(b => b.Date >= from).ToList();
            var have = archived.Select(b => b.Date).ToHashSet();
            if (sessions.All(have.Contains)) continue;

            var broker = await candles.GetDailyAsync(symbol, 60, ct);
            if (broker.Count == 0) continue;
            var actions = BrokerArchiveRules.ActionDates(await analytics.GetPayoutHistoryAsync(symbol, ct));
            var pick = BrokerArchiveRules.Select(archived, broker, actions, settledThrough, today);
            if (pick.Refusal is not null)
            {
                refused.Add($"{symbol}: {pick.Refusal}");
                logger.LogWarning("[CandleBackfill] Not archiving {Symbol}'s missing sessions from the broker: {Reason}.",
                    symbol, pick.Refusal);
                continue;
            }
            if (pick.Bars.Count == 0) continue;
            await repository.SaveDailyBarsAsync(symbol, pick.Bars, ct);
            symbols++; written += pick.Bars.Count;
        }

        if (written > 0)
            logger.LogInformation("[CandleBackfill] Archived {Bars} missing sessions for {Symbols} symbols from the broker.",
                written, symbols);
        return new(symbols, written, refused, null);
    }
}
