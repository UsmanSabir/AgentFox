using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.AhlAnalytics;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;
using TradingAgent.Trading;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// The raw archive's recent gaps filled from the broker research service, and the two archive holes the PSX
/// portal's refusal of the hosting machine (2026-10-12) exposed. The broker's recent bars were measured identical
/// to the exchange's, 210 of 210 on AHL and AKD; what these pin is when that is NOT assumed.
/// </summary>
[TestClass]
public sealed class BrokerArchiveTests
{
    private static readonly DateOnly Settled = new(2026, 10, 13);
    private static readonly DateOnly Today = new(2026, 10, 13);

    private static PsxCandle Bar(string date, decimal close, long volume = 1000, bool live = false) => new()
    {
        Symbol = "OGDC", Date = DateOnly.Parse(date), Open = close - 1, High = close + 1, Low = close - 2,
        Close = close, Volume = volume, IsLive = live
    };

    private static readonly PsxCandle[] Archived = [Bar("2026-10-08", 270), Bar("2026-10-09", 272)];
    private static readonly PsxCandle[] Broker =
    [
        Bar("2026-10-08", 270), Bar("2026-10-09", 272), Bar("2026-10-12", 275), Bar("2026-10-13", 276),
    ];

    [TestMethod]
    public void A_missing_session_with_no_action_after_it_is_archived_with_its_previous_close()
    {
        var pick = BrokerArchiveRules.Select(Archived, Broker, [], Settled, Today);

        Assert.IsNull(pick.Refusal);
        CollectionAssert.AreEqual(new[] { DateOnly.Parse("2026-10-12"), DateOnly.Parse("2026-10-13") },
            pick.Bars.Select(b => b.Date).ToArray());
        Assert.AreEqual(272m, pick.Bars[0].PreviousClose, "the exchange's own close for the day before");
        Assert.AreEqual(275m, pick.Bars[1].PreviousClose);
    }

    [TestMethod]
    public void A_bar_with_a_payout_going_ex_after_it_is_not_written()
    {
        var pick = BrokerArchiveRules.Select(Archived, Broker, [DateOnly.Parse("2026-10-13")], Settled, Today);

        Assert.AreEqual(0, pick.Bars.Count(b => b.Date == DateOnly.Parse("2026-10-12")), "adjustable once the action lands");
        var exDay = pick.Bars.Single(b => b.Date == DateOnly.Parse("2026-10-13"));
        Assert.IsNull(exDay.PreviousClose, "on an ex date the previous close is the exchange's to state, not ours to derive");
    }

    [TestMethod]
    public void A_broker_bar_that_disagrees_with_the_exchange_stops_the_whole_symbol()
    {
        PsxCandle[] rebased = [Bar("2026-10-08", 135, 2000), Bar("2026-10-09", 136, 2000), Bar("2026-10-12", 137)];
        var pick = BrokerArchiveRules.Select(Archived, rebased, [], Settled, Today);

        Assert.AreEqual(0, pick.Bars.Count);
        StringAssert.Contains(pick.Refusal, "differs");
    }

    [TestMethod]
    public void Unread_corporate_actions_write_nothing_and_an_empty_history_is_unread()
    {
        Assert.IsNull(BrokerArchiveRules.ActionDates([]));
        var pick = BrokerArchiveRules.Select(Archived, Broker, null, Settled, Today);
        Assert.AreEqual(0, pick.Bars.Count);
        Assert.IsNotNull(pick.Refusal);
    }

    [TestMethod]
    public void A_forming_session_or_one_past_the_settled_day_is_never_archived()
    {
        PsxCandle[] broker = [Bar("2026-10-09", 272), Bar("2026-10-12", 275, live: true)];
        Assert.AreEqual(0, BrokerArchiveRules.Select(Archived, broker, [], Settled, Today).Bars.Count);
        Assert.AreEqual(1, BrokerArchiveRules.Select(Archived, Broker, [], new DateOnly(2026, 10, 12), Today).Bars.Count);
    }

    [TestMethod]
    public void An_action_dates_from_its_ex_date_or_its_book_closure_whichever_comes_first()
    {
        var dates = BrokerArchiveRules.ActionDates(
        [
            new AhlAnnouncement { Dividend = "6", ExDate = "2026-10-08", BookClosureFrom = "2026-10-09" },
            // NML as served: ex date EQUAL to the book-closure start, a day late.
            new AhlAnnouncement { Dividend = "2", ExDate = "2026-10-21", BookClosureFrom = "2026-10-21" },
            new AhlAnnouncement { Bonus = "800", BookClosureFrom = "2024-09-19" },
            new AhlAnnouncement { Title = "Board meeting" },
        ]);

        CollectionAssert.AreEquivalent(
            new[] { DateOnly.Parse("2026-10-08"), DateOnly.Parse("2026-10-21"), DateOnly.Parse("2024-09-19") },
            dates!.ToArray());
    }

    [TestMethod]
    public void The_52_week_range_comes_from_a_series_that_spans_a_year_and_not_otherwise()
    {
        var today = new DateOnly(2026, 10, 13);
        var year = Enumerable.Range(0, 400).Select(i => new PsxCandle
        {
            Symbol = "OGDC", Date = today.AddDays(-399 + i), Open = 1, High = 999, Low = 0.5m, Close = 100 + i % 50, Volume = 1
        }).ToList();

        var range = CandleAnalysisService.FiftyTwoWeekRange("OGDC", year, today)!;
        Assert.AreEqual(149m, range.High52Week, "closes, as the portal's summary computed it — not the 999 high");
        Assert.AreEqual(100m, range.Low52Week);
        Assert.IsNull(CandleAnalysisService.FiftyTwoWeekRange("OGDC", year.TakeLast(200).ToList(), today));
    }

    [TestMethod]
    public async Task A_real_bar_reopens_a_day_wrongly_recorded_as_closed()
    {
        using var env = Env.Create("http://127.0.0.1:9");
        var monday = new DateOnly(2026, 10, 12);
        await env.Repository.SaveNonTradingDayAsync(monday);
        Assert.AreEqual(1, (await env.Repository.GetCoveredDailyDatesAsync(monday, monday, ["PPL"])).Count,
            "the damage: a closed day is covered for every symbol, for good");

        await env.Repository.SaveDailyBarsAsync("OGDC", [Bar("2026-10-12", 275)]);

        Assert.AreEqual(1, (await env.Repository.GetCoveredDailyDatesAsync(monday, monday, ["OGDC"])).Count);
        Assert.AreEqual(0, (await env.Repository.GetCoveredDailyDatesAsync(monday, monday, ["PPL"])).Count,
            "reopened: the whole-market pass now asks for the other symbols again");
    }

    [TestMethod]
    public async Task A_portal_that_does_not_answer_records_no_day_as_closed()
    {
        // Port 9 refuses at once: the shape of the hosting machine's refusals from 2026-10-12.
        using var env = Env.Create("http://127.0.0.1:9");
        var runner = env.Runner();

        await runner.RunOnceAsync(1, CancellationToken.None);

        var settled = runner.LastSettledSession();
        Assert.AreEqual(0, (await env.Repository.GetCoveredDailyDatesAsync(settled.AddDays(-14), settled, ["OGDC"])).Count,
            "a failed request is not an empty table");
        StringAssert.Contains(runner.Progress.Message, "did not answer");
    }

    private sealed class Env : IDisposable
    {
        public required IOptions<TradingAgentOptions> Options { get; init; }
        public required SqliteTradingRepository Repository { get; init; }
        public required string Temp { get; init; }

        public static Env Create(string psxBaseUrl)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"agentfox-broker-archive-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);
            var options = Microsoft.Extensions.Options.Options.Create(new TradingAgentOptions
            {
                DatabasePath = Path.Combine(temp, "trading.db"),
                AllowedSymbols = ["OGDC"],
                PsxDataBaseUrl = psxBaseUrl
            });
            var repository = new SqliteTradingRepository(options, new ConfigurationBuilder().Build(),
                NullLogger<SqliteTradingRepository>.Instance);
            return new Env { Options = options, Repository = repository, Temp = temp };
        }

        public CandleBackfillRunner Runner() => new(
            new PsxDataClient(Options, NullLogger<PsxDataClient>.Instance),
            Repository,
            new MonitoredUniverse(Options, Repository, NullLogger<MonitoredUniverse>.Instance),
            new PsxMarketCalendar(Options, NullLogger<PsxMarketCalendar>.Instance),
            Options,
            new Lifetime(),
            NullLogger<CandleBackfillRunner>.Instance);

        public void Dispose() { try { Directory.Delete(Temp, recursive: true); } catch { /* temp */ } }
    }

    private sealed class Lifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
