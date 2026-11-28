using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Market;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>Position plans and the protection parameter catalog (plan step 9).</summary>
[TestClass]
public sealed class PositionPlanTests
{
    private string _root = "";

    [TestInitialize]
    public void Init() { _root = Path.Combine(Path.GetTempPath(), "plans-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }

    [TestCleanup]
    public void Cleanup() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch (IOException) { } }

    private IPositionPlanStore Store() => new SqliteTradingRepository(
        Options.Create(new TradingAgentOptions { DatabasePath = Path.Combine(_root, "t.db") }),
        new ConfigurationBuilder().Build(), NullLogger<SqliteTradingRepository>.Instance);

    [TestMethod]
    public async Task APlanIsSavedReadAndVersioned_AndAStaleWriteIsRefused()
    {
        var store = Store();
        var first = await store.TrySavePositionPlanAsync(new PositionPlan { Symbol = "ppl", EntryPrice = 230m, InitialStop = 220m }, 0);
        Assert.AreEqual(1, first!.Version);
        Assert.AreEqual("PPL", first.Symbol);

        var second = await store.TrySavePositionPlanAsync(first with { CatastrophicTrigger = 200m }, expectedVersion: 1);
        Assert.AreEqual(2, second!.Version);
        Assert.IsNull(await store.TrySavePositionPlanAsync(first with { Note = "stale" }, expectedVersion: 1), "another write came first");
        Assert.IsNull(await store.TrySavePositionPlanAsync(new PositionPlan { Symbol = "PPL" }, 0), "a 'new' plan over an existing one");

        Assert.AreEqual(200m, (await store.GetPositionPlanAsync("ppl"))!.CatastrophicTrigger);
        Assert.IsTrue(await store.DeletePositionPlanAsync("PPL"));
        Assert.AreEqual(0, (await store.GetPositionPlansAsync()).Count);
    }

    [TestMethod]
    public void ValidationNamesEveryProblem()
    {
        var problems = PositionPlans.Validate(new PositionPlan
        {
            Symbol = "PPL", Mode = "SWING", EntryPrice = 100m, InitialStop = 105m, StopLimitOffsetPct = 15m, TimeStopWeeks = 0
        });
        Assert.AreEqual(4, problems.Count, string.Join(" | ", problems));
        Assert.AreEqual(0, PositionPlans.Validate(new PositionPlan { Symbol = "PPL" }).Count, "an empty plan is valid and changes nothing");
    }

    [TestMethod]
    public void EveryRuleThatCanSellIsOffInANewPlan()
    {
        var plan = new PositionPlan { Symbol = "PPL" };
        Assert.IsNull(plan.ClosingRuleLevel);
        Assert.IsNull(plan.TimeStopWeeks);
        Assert.IsFalse(plan.TimeStopExits);
        Assert.IsFalse(plan.RelativeStrengthExit);
        Assert.IsFalse(plan.LowerLockQueue);
    }

    [TestMethod]
    public void TheCatastrophicLevelAndLimitComeFromThePlanOrTheDefault()
    {
        Assert.AreEqual(209m, PositionPlans.CatastrophicFor(220m, null, 5m));
        Assert.AreEqual(200m, PositionPlans.CatastrophicFor(220m, new PositionPlan { Symbol = "PPL", CatastrophicTrigger = 200m }, 5m));
        Assert.AreEqual(217.80m, PositionPlans.LimitFor(220m, null, 1m), "the 0.99 that was hard-coded");
        Assert.AreEqual(215.60m, PositionPlans.LimitFor(220m, new PositionPlan { Symbol = "PPL", StopLimitOffsetPct = 2m }, 1m));
    }

    [TestMethod]
    public void APlansOwnCatastrophicLevelDecidesTheReopenHold()
    {
        var reopen = new MarketPhaseReading(MarketPhase.ReopenSettling, DateTime.UtcNow, "broker", "reopened");
        var backstop = new ArmedOrder
        {
            ArmedId = "b", Symbol = "PPL", TriggerKind = ArmedTriggerKind.PriceBelow, TriggerPrice = 220m,
            Action = "SELL", Quantity = 100, OrderType = "STOPLOSS", ProtectiveStopId = "s"
        };
        var plan = new PositionPlan { Symbol = "PPL", CatastrophicTrigger = 215m };

        Assert.IsNotNull(WatchlistMonitorWorker.HaltHoldReason(reopen, backstop, 212m, 5m, 10), "default level 209: held");
        Assert.IsNull(WatchlistMonitorWorker.HaltHoldReason(reopen, backstop, 212m, 5m, 10, plan), "its own level 215: fires");
    }

    // ── The parameter catalog ─────────────────────────────────────────────────

    [TestMethod]
    public void EveryCatalogedParameterExistsAndHasADefault()
    {
        foreach (var p in ProtectionParameters.All) Assert.IsFalse(string.IsNullOrEmpty(ProtectionParameters.DefaultOf(p)), p.Path);
        Assert.AreEqual("3", ProtectionParameters.DefaultOf(ProtectionParameters.All.Single(p => p.Path == "DailyLossLimitPct")));
    }

    /// <summary>
    /// The document must match the catalog. To regenerate after a deliberate change, run this test with
    /// AGENTFOX_UPDATE_DOCS=1 set.
    /// </summary>
    [TestMethod]
    public void TheParameterDocumentMatchesTheCatalog()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/protection-parameters.md"));
        var expected = ProtectionParameters.Markdown();
        if (Environment.GetEnvironmentVariable("AGENTFOX_UPDATE_DOCS") == "1") File.WriteAllText(path, expected);

        Assert.IsTrue(File.Exists(path), $"{path} is missing; run with AGENTFOX_UPDATE_DOCS=1 to create it.");
        Assert.AreEqual(expected, File.ReadAllText(path).Replace("\r\n", "\n"),
            "docs/protection-parameters.md is out of date; run this test with AGENTFOX_UPDATE_DOCS=1.");
    }
}
