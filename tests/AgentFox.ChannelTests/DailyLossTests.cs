using AgentFox.ChannelTests.Simulation;
using AgentFox.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using TradingAgent.Config;
using TradingAgent.Reconciliation;
using TradingAgent.Research;
using TradingAgent.Risk;

namespace AgentFox.ChannelTests;

/// <summary>The daily loss limit (plan step 8; owner: 3%, on by default, user-overridable, auto-reset next session).</summary>
[TestClass]
public sealed class DailyLossTests
{
    private static PsxLiveQuote Q(string s, decimal last, decimal prev) => new() { Symbol = s, Current = last, PreviousClose = prev };
    private static Dictionary<string, PsxLiveQuote> Quotes(params PsxLiveQuote[] q) =>
        q.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);
    private static BrokerFill Fill(string s, string side, int qty, decimal price) => new("1", s, side, qty, price, DateTime.UtcNow);

    // ── The arithmetic ────────────────────────────────────────────────────────

    [TestMethod]
    public void HeldSharesEarnTheMoveFromYesterdaysClose()
    {
        var r = DailyLoss.Compute([new("PPL", 100)], [], Quotes(Q("PPL", 95m, 100m)), cashPkr: 0m);
        Assert.AreEqual(-500m, r.PnlPkr);
        Assert.AreEqual(10_000m, r.BasePkr);
    }

    [TestMethod]
    public void ABuyTodayEarnsFromItsOwnPrice_AndASaleBooksItsPrice()
    {
        // 100 held at the open, 50 more bought at 98, 30 sold at 99; now 120 held, last 95, previous close 100.
        var r = DailyLoss.Compute([new("PPL", 120)], [Fill("PPL", "BUY", 50, 98m), Fill("PPL", "SELL", 30, 99m)],
            Quotes(Q("PPL", 95m, 100m)), cashPkr: 1_000m);

        // open 100 → 70 kept: 70 × −5 = −350; sold 30 at 99: −30; bought 50 at 98 → 95: −150.  Total −530.
        Assert.AreEqual(-530m, r.PnlPkr);
        // base: 100 × 100 + opening cash (1,000 + 4,900 bought − 2,970 sold = 2,930)
        Assert.AreEqual(12_930m, r.BasePkr);
    }

    [TestMethod]
    public void AHeldSymbolWithoutPricesMakesTheWholeResultUnknown()
    {
        var r = DailyLoss.Compute([new("PPL", 100), new("OGDC", 10)], [], Quotes(Q("PPL", 95m, 100m)), 0m);
        Assert.IsNull(r.PnlPkr);
        StringAssert.Contains(r.Reason, "OGDC");
    }

    [TestMethod]
    public void UnreadableCashMeansASmallerBase_WhichTripsSooner()
    {
        Assert.AreEqual(10_000m, DailyLoss.Compute([new("PPL", 100)], [], Quotes(Q("PPL", 95m, 100m)), null).BasePkr);
    }

    [TestMethod]
    public void TheBreachIsWhicheverLimitComesFirst_AndZeroIsOff()
    {
        var r = new DailyResult(-310m, 10_000m, "");
        Assert.IsNotNull(DailyLoss.Breach(r, 3m, 0m), "3.1% ≥ 3%");
        Assert.IsNull(DailyLoss.Breach(r, 4m, 0m));
        Assert.IsNotNull(DailyLoss.Breach(r, 4m, 300m), "the PKR limit reached first");
        Assert.IsNull(DailyLoss.Breach(r, 0m, 0m), "both off");
        Assert.IsNull(DailyLoss.Breach(new DailyResult(50m, 10_000m, ""), 3m, 0m), "a gain never trips");
    }

    [TestMethod]
    public void TheDefaultIsThreePercent() => Assert.AreEqual(3m, new TradingAgentOptions().DailyLossLimitPct);

    // ── The guard ─────────────────────────────────────────────────────────────

    private sealed class FixedQuotes(Dictionary<string, PsxLiveQuote> quotes) : ILiveQuoteSource
    {
        public Dictionary<string, PsxLiveQuote> Quotes = quotes;
        public string Name => "fixed";
        public bool IsEnabled => true;
        public Task<LiveQuoteSnapshot> GetQuotesAsync(CancellationToken ct = default) =>
            Task.FromResult(new LiveQuoteSnapshot { Source = Name, Quotes = Quotes });
    }

    private sealed class Rig : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "dailyloss-" + Guid.NewGuid().ToString("N"));
        public readonly SimCalendar Calendar = new();
        public readonly TradingReconciliationState State = new();
        public readonly FixedQuotes Prices = new(Quotes(Q("PPL", 100m, 100m)));
        public readonly PluginConfigManager Configs;
        public readonly TradingPolicyProvider Policy;
        public readonly DailyLossGuard Guard;
        public readonly RecordingNotifier Notifier = new();

        public Rig()
        {
            var options = Microsoft.Extensions.Options.Options.Create(new TradingAgentOptions());
            Configs = new PluginConfigManager(Root, NullLogger<PluginConfigManager>.Instance);
            Policy = new TradingPolicyProvider(options, Configs);
            State.Update(new(true, true, "ok", DateTime.UtcNow) { Positions = [new("PPL", 100)], AvailableCashPkr = 0m });
            Guard = new DailyLossGuard(Calendar, State,
                new CompositeLiveQuoteSource([Prices], NullLogger<CompositeLiveQuoteSource>.Instance),
                Configs, Policy, options, NullLogger<DailyLossGuard>.Instance, Notifier);
        }

        public void Price(decimal last) => Prices.Quotes = Quotes(Q("PPL", last, 100m));
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [TestMethod]
    public async Task PastTheLimitNewBuysSwitchOff_AndStopsAreLeftWorking()
    {
        using var rig = new Rig();
        rig.Price(98m);
        Assert.AreEqual("within", await rig.Guard.CheckAsync(), "2% is inside 3%");

        rig.Price(96.5m);
        Assert.AreEqual("tripped", await rig.Guard.CheckAsync());
        Assert.AreEqual(TradingStopMode.EntriesOff, rig.Policy.Current().TradingStop, "entries only — never FullStop");
        Assert.IsTrue(rig.Notifier.Sent.Any(s => s.Topic == "trading.risk.daily-loss"));
        Assert.AreEqual("tripped-today", await rig.Guard.CheckAsync(), "not tripped twice");
    }

    [TestMethod]
    public async Task TheNextSessionSwitchesBuyingBackOn()
    {
        using var rig = new Rig();
        rig.Price(90m);
        await rig.Guard.CheckAsync();

        rig.Calendar.DayOffset++;
        Assert.AreEqual("reset", await rig.Guard.CheckAsync());
        Assert.AreEqual(TradingStopMode.Off, rig.Policy.Current().TradingStop);
    }

    [TestMethod]
    public async Task AStopTheOwnerSetIsNeverClearedByTheReset()
    {
        using var rig = new Rig();
        rig.Price(90m);
        await rig.Guard.CheckAsync();
        // The owner then took over the stop themselves.
        await rig.Configs.MergeConfigAsync("trading-agent", new() { ["tradingStop"] = "EntriesOff", ["tradingStopSetBy"] = "owner" });

        rig.Calendar.DayOffset++;
        Assert.AreNotEqual("reset", await rig.Guard.CheckAsync());
        Assert.AreEqual(TradingStopMode.EntriesOff, rig.Policy.Current().TradingStop);
    }

    [TestMethod]
    public async Task UnknownPricesNeitherTripNorClear()
    {
        using var rig = new Rig();
        rig.Prices.Quotes = new(StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("unknown", await rig.Guard.CheckAsync());
        Assert.AreEqual(TradingStopMode.Off, rig.Policy.Current().TradingStop);
    }

    [TestMethod]
    public async Task ALimitSetInSettingsOverridesTheDefault_AndZeroTurnsItOff()
    {
        using var rig = new Rig();
        await rig.Configs.MergeConfigAsync("trading-agent", new() { ["dailyLossLimitPct"] = 0m });
        rig.Price(80m);
        Assert.AreEqual("off", await rig.Guard.CheckAsync());
    }
}
