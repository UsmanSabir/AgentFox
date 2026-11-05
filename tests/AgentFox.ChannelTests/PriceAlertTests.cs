using AgentFox.Plugins.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Research;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class PriceAlertTests
{
    private static readonly DateTime Open = new(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void TargetEqualityAndIndexAliases()
    {
        var above = PriceAlert.Create(new(" kse-100 ", "above", 160_000), Open)!;
        Assert.AreEqual("KSE100", above.Symbol);
        Assert.IsFalse(above.Matches(159_999.99m));
        Assert.IsTrue(above.Matches(160_000m));
        var below = PriceAlert.Create(new("OGDC", "below", 250m), Open)!;
        Assert.IsTrue(below.Matches(250m));
        Assert.IsFalse(below.Matches(250.01m));
        Assert.IsNull(PriceAlert.Create(new("OGDC", "below", 0m), Open));
    }

    [TestMethod]
    public async Task StaleQuoteAndClosedMarketDoNotFireAndPendingDeliverySurvivesRestart()
    {
        var directory = Directory.CreateTempSubdirectory("price-alerts-");
        try
        {
            var options = Options.Create(new TradingAgentOptions { DatabasePath = Path.Combine(directory.FullName, "alerts.db") });
            var config = new ConfigurationBuilder().Build();
            SqliteTradingRepository Repository() => new(options, config, NullLogger<SqliteTradingRepository>.Instance);
            var quotes = new Quotes();
            var composite = new CompositeLiveQuoteSource([quotes], NullLogger<CompositeLiveQuoteSource>.Instance);
            var psx = new PsxDataClient(options, NullLogger<PsxDataClient>.Instance);
            var calendar = new Calendar();
            var clock = new Clock { Now = Open };
            var notifier = new Notifier();
            PriceAlertWorker Worker(SqliteTradingRepository db) => new(db, composite, psx, calendar,
                NullLogger<PriceAlertWorker>.Instance, notifier, clock: clock);
            var db = Repository();
            var rule = PriceAlert.Create(new("OGDC", "above", 250m), Open.AddHours(-1))!;
            Assert.IsTrue(await db.WritePriceAlertAsync(rule, null, default));
            Assert.IsFalse(await db.WritePriceAlertAsync(rule with { Id = Guid.NewGuid() }, null, default));
            using var worker = Worker(db);

            quotes.Quote = new() { Current = 251m, Volume = 10, RetrievedAtUtc = Open.AddMinutes(-3), Source = "ahk" };
            await worker.TickAsync(Open, default);
            Assert.AreEqual("watching", (await db.ReadPriceAlertsAsync(default)).Single().State);
            quotes.Quote = quotes.Quote with { RetrievedAtUtc = Open };
            calendar.IsOpen = false;
            await worker.TickAsync(Open, default);
            Assert.AreEqual(0, notifier.Attempts);
            calendar.IsOpen = true;
            await worker.TickAsync(Open, default);
            var pending = (await db.ReadPriceAlertsAsync(default)).Single();
            Assert.AreEqual("pending", pending.State);
            Assert.AreEqual(251m, pending.Trigger!.Value);
            Assert.IsFalse(await db.WritePriceAlertAsync(rule with { State = "cancelled" }, rule, default));

            var restartedDb = Repository();
            using var restarted = Worker(restartedDb);
            quotes.Quote = quotes.Quote with { Current = 245m };
            clock.Now = Open.AddMinutes(2);
            notifier.Recipients = 1;
            await restarted.TickAsync(clock.Now, default);
            var delivered = (await restartedDb.ReadPriceAlertsAsync(default)).Single();
            Assert.AreEqual("delivered", delivered.State);
            Assert.AreEqual(2, notifier.Attempts);
            StringAssert.Contains(notifier.Message, "251.00");
            await restarted.TickAsync(clock.Now, default);
            Assert.AreEqual(2, notifier.Attempts);

            await restartedDb.PrunePriceAlertsAsync(Open.AddDays(8), default);
            Assert.AreEqual(0, (await restartedDb.ReadPriceAlertsAsync(default)).Count);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class Quotes : ILiveQuoteSource
    {
        public PsxLiveQuote Quote { get; set; } = new();
        public string Name => "ahk";
        public bool IsEnabled => true;
        public Task<LiveQuoteSnapshot> GetQuotesAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveQuoteSnapshot { Quotes = new Dictionary<string, PsxLiveQuote> { ["OGDC"] = Quote } });
    }

    private sealed class Calendar : IMarketCalendar
    {
        public bool IsOpen { get; set; } = true;
        public MarketStatus GetStatus(DateTime? utcNow = null) => new(IsOpen, PsxTime.Now(utcNow), "");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTime Now { get; set; }
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class Notifier : IUserNotifier
    {
        public int Attempts { get; private set; }
        public int Recipients { get; set; }
        public string Message { get; private set; } = "";
        public Task<int> NotifyAsync(string message, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<int> NotifyAsync(string message, string? topic, CancellationToken ct = default)
        {
            Assert.AreEqual(TradingAgent.TradingTopics.PriceAlert, topic);
            Attempts++;
            Message = message;
            return Task.FromResult(Recipients);
        }
    }
}
