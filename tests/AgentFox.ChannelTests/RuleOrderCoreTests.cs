using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Analysis;
using TradingAgent.Config;
using TradingAgent.Persistence;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>
/// Core's half of <see cref="ArmedTriggerKind.Rule"/>: the edition decides whether the condition holds,
/// and core decides everything else. These pin the two refusals core keeps for itself — no evaluator,
/// and a shut market — and that the opaque condition document survives storage untouched.
/// </summary>
[TestClass]
public sealed class RuleOrderCoreTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 6, 0, 0, DateTimeKind.Utc);
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"agentfox-rule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [TestMethod]
    public void A_rule_with_no_evaluator_waits_and_says_why()
    {
        var outcome = ArmedOrderEvaluator.Evaluate(Rule(), 100m, [], Now, out var reason, marketIsOpen: true);

        Assert.AreEqual(ArmedOrderOutcome.Wait, outcome);
        StringAssert.Contains(reason, "nothing in this edition can evaluate");
    }

    [TestMethod]
    public void A_rule_never_fires_while_the_market_is_shut_whatever_the_evaluator_says()
    {
        var outcome = ArmedOrderEvaluator.Evaluate(Rule(), 100m, [], Now, out var reason,
            marketIsOpen: false, condition: new ArmedConditionDecision(true, "met"));

        Assert.AreEqual(ArmedOrderOutcome.Wait, outcome,
            "A rule can hold on a date alone, and PSX queues an order placed while shut for the next open.");
        StringAssert.Contains(reason, "market is closed");
    }

    [TestMethod]
    public void A_rule_fires_when_the_evaluator_says_so_and_reports_its_reason()
    {
        var outcome = ArmedOrderEvaluator.Evaluate(Rule(), 100m, [], Now, out var reason,
            marketIsOpen: true, condition: new ArmedConditionDecision(true, "Last 84.10 <= FIFO 86.70 - 3%"));

        Assert.AreEqual(ArmedOrderOutcome.Fire, outcome);
        Assert.AreEqual("Last 84.10 <= FIFO 86.70 - 3%", reason);
    }

    [TestMethod]
    public void A_rule_waits_when_the_evaluator_says_not_yet()
    {
        var outcome = ArmedOrderEvaluator.Evaluate(Rule(), 100m, [], Now, out var reason,
            marketIsOpen: true, condition: ArmedConditionDecision.Wait("1 of 2 true"));

        Assert.AreEqual(ArmedOrderOutcome.Wait, outcome);
        Assert.AreEqual("1 of 2 true", reason);
    }

    [TestMethod]
    public void A_rule_still_respects_its_expiry()
    {
        var expired = Rule() with { ExpiresUtc = Now.AddMinutes(-1) };

        var outcome = ArmedOrderEvaluator.Evaluate(expired, 100m, [], Now, out _,
            marketIsOpen: true, condition: new ArmedConditionDecision(true, "met"));

        Assert.AreEqual(ArmedOrderOutcome.Wait, outcome);
    }

    [TestMethod]
    public void A_rule_has_no_trigger_level_to_show()
    {
        Assert.IsNull((Rule() with { TriggerPrice = 99m }).EffectiveTriggerPrice,
            "A panel must not display a level the evaluator never consults.");
    }

    [TestMethod]
    public void A_condition_on_any_other_kind_is_ignored()
    {
        var priceOrder = Rule() with { TriggerKind = ArmedTriggerKind.PriceBelow, TriggerPrice = 90m };

        var outcome = ArmedOrderEvaluator.Evaluate(priceOrder, 100m, [], Now, out _,
            marketIsOpen: true, condition: new ArmedConditionDecision(true, "met"));

        Assert.AreEqual(ArmedOrderOutcome.Wait, outcome, "100 is above a 90 sell trigger, whatever a rule says.");
    }

    [TestMethod]
    public async Task The_condition_document_and_summary_round_trip_untouched()
    {
        var repository = NewRepository();
        const string spec = """{"v":1,"join":"all","items":[]}""";
        await repository.SaveArmedOrderAsync(Rule() with { ConditionSpec = spec, ConditionSummary = "last <= 90" });

        var stored = (await repository.GetArmedOrdersAsync()).Single(o => o.ArmedId == "rule1");

        Assert.AreEqual(ArmedTriggerKind.Rule, stored.TriggerKind);
        Assert.AreEqual(spec, stored.ConditionSpec);
        Assert.AreEqual("last <= 90", stored.ConditionSummary);
        Assert.IsNull(stored.TriggerPrice);
    }

    [TestMethod]
    public async Task An_ordinary_order_stores_no_condition()
    {
        var repository = NewRepository();
        await repository.SaveArmedOrderAsync(Rule() with { TriggerKind = ArmedTriggerKind.PriceBelow, TriggerPrice = 90m });

        var stored = (await repository.GetArmedOrdersAsync()).Single();

        Assert.IsNull(stored.ConditionSpec);
        Assert.IsNull(stored.ConditionSummary);
    }

    [TestMethod]
    public void Technical_readings_are_served_only_while_young()
    {
        var readings = new MonitorTechnicalReadings();
        readings.Record(new TechnicalSnapshot { Symbol = "OGDC", Rsi14 = 31m }, Now);

        Assert.AreEqual(31m, readings.Latest("ogdc", Now.AddMinutes(10))?.Snapshot.Rsi14, "Case-insensitive.");
        Assert.IsNull(readings.Latest("OGDC", Now + MonitorTechnicalReadings.MaxAge + TimeSpan.FromSeconds(1)),
            "A reading older than its limit is gone, never served.");
    }

    [TestMethod]
    public void Technical_readings_are_bounded()
    {
        var readings = new MonitorTechnicalReadings();
        for (var i = 0; i < MonitorTechnicalReadings.Capacity + 5; i++)
            readings.Record(new TechnicalSnapshot { Symbol = $"S{i}" }, Now.AddSeconds(i));

        Assert.IsNull(readings.Latest("S0", Now.AddSeconds(MonitorTechnicalReadings.Capacity + 10)),
            "The oldest is dropped past the cap.");
        Assert.IsNotNull(readings.Latest($"S{MonitorTechnicalReadings.Capacity + 4}",
            Now.AddSeconds(MonitorTechnicalReadings.Capacity + 10)));
    }

    [TestMethod]
    public void A_validation_is_ok_only_with_a_document_and_no_error()
    {
        Assert.IsTrue(ArmedConditionValidation.Valid("{}", "s").Ok);
        Assert.IsFalse(ArmedConditionValidation.Invalid("bad", "no").Ok);
        Assert.IsFalse(new ArmedConditionValidation(" ", "s").Ok);
    }

    private static ArmedOrder Rule() => new()
    {
        ArmedId = "rule1",
        Symbol = "OGDC",
        TriggerKind = ArmedTriggerKind.Rule,
        Action = "BUY",
        Quantity = 10,
        OrderType = "LIMIT",
        Price = 90m,
        ArmedUtc = Now.AddHours(-1),
        ExpiresUtc = Now.AddDays(10),
        OperatorOriginated = true
    };

    private SqliteTradingRepository NewRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Workspaces:0"] = _root })
            .Build();

        return new SqliteTradingRepository(
            Options.Create(new TradingAgentOptions { DatabasePath = "trading/trading.db" }),
            configuration,
            NullLogger<SqliteTradingRepository>.Instance);
    }
}
