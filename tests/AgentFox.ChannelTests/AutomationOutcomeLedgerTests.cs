using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingAgent.Config;
using TradingAgent.Persistence;

namespace AgentFox.ChannelTests;

/// <summary>
/// The outcome ledger, and specifically the three things that can only go wrong once real money has
/// been traded through it.
///
/// <para>
/// <b>Idempotency.</b> A campaign observed as closed again after a restart must not be counted twice.
/// The rollup is incremented inside the same transaction as the insert precisely so that one guard
/// covers both, and this is the test that proves the guard is actually load-bearing.
/// </para>
///
/// <para>
/// <b>Retention that does not destroy the record.</b> Raw rows expire; the rollup does not. If the
/// rollup were derived by querying rows, pruning would silently erase months of history — so these
/// tests prune aggressively and then assert the aggregate survived intact.
/// </para>
///
/// <para>
/// <b>Unknown is not zero.</b> An outcome whose net result could not be computed must not read as a
/// break-even trade. It counts as neither a win nor a loss, contributes nothing to the sums, and is
/// excluded from <c>measured</c> so an average has an honest denominator.
/// </para>
/// </summary>
[TestClass]
public sealed class AutomationOutcomeLedgerTests
{
    private string _root = "";

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "agentfox-outcomes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

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

    private static AutomationOutcomeRecord Outcome(
        string campaignId = "c1",
        string symbol = "OGDC",
        string profileId = "pullback-balanced",
        string mode = "BoundedAuto",
        bool simulated = false,
        decimal? netPkr = 1_500m,
        decimal? r = 1.5m,
        DateTime? closedUtc = null) => new(
        CampaignId: campaignId,
        Symbol: symbol,
        ProfileId: profileId,
        EntryStrategyId: "confluence-pullback",
        ExitPlanId: "target-scale-out-then-atr",
        Mode: mode,
        Simulated: simulated,
        OpenedUtc: (closedUtc ?? new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc)).AddDays(-5),
        ClosedUtc: closedUtc ?? new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc),
        SessionsHeld: 5,
        PlannedEntry: 118.80m,
        PlannedStop: 115.89m,
        PlannedTarget: 124.75m,
        InitialRiskPerShare: 2.91m,
        Quantity: 346,
        DeployedPkr: 41_104m,
        AverageCost: 118.80m,
        RealisedNetPkr: netPkr,
        RealisedR: r,
        CloseReason: "TargetScaleOut",
        RegimeAtEntry: "RiskOn",
        RecordedUtc: new DateTime(2026, 8, 20, 10, 5, 0, DateTimeKind.Utc));

    // ── Recording ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task An_outcome_is_recorded_and_rolled_up_together()
    {
        var repository = NewRepository();

        Assert.IsTrue(await repository.SaveAutomationOutcomeAsync(Outcome()));

        var rows = await repository.GetAutomationOutcomesAsync();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("OGDC", rows[0].Symbol);
        Assert.AreEqual(1.5m, rows[0].RealisedR);
        Assert.AreEqual("RiskOn", rows[0].RegimeAtEntry);

        var daily = await repository.GetAutomationOutcomeDailyAsync();
        Assert.AreEqual(1, daily.Count);
        Assert.AreEqual("2026-08-20", daily[0].Day);
        Assert.AreEqual(1, daily[0].Trades);
        Assert.AreEqual(1, daily[0].Wins);
        Assert.AreEqual(0, daily[0].Losses);
        Assert.AreEqual(1, daily[0].Measured);
        Assert.AreEqual(1.5m, daily[0].SumR);
    }

    /// <summary>
    /// The restart case. Without the conflict guard this would double every figure the promotion
    /// decision reads, and it would do so silently.
    /// </summary>
    [TestMethod]
    public async Task Recording_the_same_campaign_twice_changes_nothing()
    {
        var repository = NewRepository();

        Assert.IsTrue(await repository.SaveAutomationOutcomeAsync(Outcome()));
        Assert.IsFalse(await repository.SaveAutomationOutcomeAsync(Outcome()),
            "The second write must report that it recorded nothing.");

        Assert.AreEqual(1, (await repository.GetAutomationOutcomesAsync()).Count);

        var daily = await repository.GetAutomationOutcomeDailyAsync();
        Assert.AreEqual(1, daily[0].Trades, "The rollup must not have been incremented twice.");
        Assert.AreEqual(1.5m, daily[0].SumR);
    }

    /// <summary>
    /// Paper and Shadow submit nothing, so their results are modelled. Keeping them in separate
    /// rollup rows is what stops a promising shadow record flattering a live one into promotion.
    /// </summary>
    [TestMethod]
    public async Task Simulated_and_live_results_roll_up_separately()
    {
        var repository = NewRepository();

        await repository.SaveAutomationOutcomeAsync(Outcome(campaignId: "c1", mode: "BoundedAuto"));
        await repository.SaveAutomationOutcomeAsync(
            Outcome(campaignId: "c2", mode: "Shadow", simulated: true));

        var daily = await repository.GetAutomationOutcomeDailyAsync();
        Assert.AreEqual(2, daily.Count);
        Assert.AreEqual(1, daily.Single(d => d.Mode == "BoundedAuto").Trades);
        Assert.AreEqual(1, daily.Single(d => d.Mode == "Shadow").Trades);

        var shadow = (await repository.GetAutomationOutcomesAsync())
            .Single(o => o.CampaignId == "c2");
        Assert.IsTrue(shadow.Simulated);
    }

    [TestMethod]
    public async Task A_loss_counts_as_a_loss_and_lowers_the_sum()
    {
        var repository = NewRepository();

        await repository.SaveAutomationOutcomeAsync(Outcome(campaignId: "win", netPkr: 1_000m, r: 1m));
        await repository.SaveAutomationOutcomeAsync(Outcome(campaignId: "loss", netPkr: -500m, r: -1m));

        var daily = (await repository.GetAutomationOutcomeDailyAsync()).Single();
        Assert.AreEqual(2, daily.Trades);
        Assert.AreEqual(1, daily.Wins);
        Assert.AreEqual(1, daily.Losses);
        Assert.AreEqual(0m, daily.SumR);
        Assert.AreEqual(500m, daily.SumNetPkr);
    }

    /// <summary>
    /// An adopted holding has no entry plan, so it has no R. That must not read as a flat trade: it
    /// is counted as a trade, is neither a win nor a loss, and is left out of <c>measured</c>.
    /// </summary>
    [TestMethod]
    public async Task An_unknown_result_is_a_trade_but_not_a_measurement()
    {
        var repository = NewRepository();

        await repository.SaveAutomationOutcomeAsync(
            Outcome(campaignId: "adopted", netPkr: null, r: null));

        var daily = (await repository.GetAutomationOutcomeDailyAsync()).Single();
        Assert.AreEqual(1, daily.Trades);
        Assert.AreEqual(0, daily.Wins);
        Assert.AreEqual(0, daily.Losses);
        Assert.AreEqual(0, daily.Measured, "An unknown result is not a measurement of zero.");
        Assert.AreEqual(0m, daily.SumR);
    }

    // ── Gate rejections ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Gate_rejection_counts_accumulate_across_passes_within_a_day()
    {
        var repository = NewRepository();

        await repository.AddAutomationGateRejectionsAsync(
            "2026-08-20", "confluence-pullback",
            new Dictionary<string, int> { ["net-reward-risk"] = 12, ["weekly-confirmation"] = 5 });
        await repository.AddAutomationGateRejectionsAsync(
            "2026-08-20", "confluence-pullback",
            new Dictionary<string, int> { ["net-reward-risk"] = 8 });

        var rows = await repository.GetAutomationGateRejectionsAsync();
        Assert.AreEqual(20, rows.Single(r => r.GateCode == "net-reward-risk").Count);
        Assert.AreEqual(5, rows.Single(r => r.GateCode == "weekly-confirmation").Count);
    }

    [TestMethod]
    public async Task Gate_rejections_are_returned_most_common_first()
    {
        var repository = NewRepository();

        await repository.AddAutomationGateRejectionsAsync(
            "2026-08-20", "confluence-pullback",
            new Dictionary<string, int> { ["rsi"] = 3, ["net-reward-risk"] = 41, ["turnover"] = 17 });

        var rows = await repository.GetAutomationGateRejectionsAsync();
        CollectionAssert.AreEqual(
            new[] { "net-reward-risk", "turnover", "rsi" },
            rows.Select(r => r.GateCode).ToArray());
    }

    [TestMethod]
    public async Task An_empty_rejection_batch_writes_nothing()
    {
        var repository = NewRepository();

        await repository.AddAutomationGateRejectionsAsync(
            "2026-08-20", "confluence-pullback", new Dictionary<string, int>());

        Assert.AreEqual(0, (await repository.GetAutomationGateRejectionsAsync()).Count);
    }

    // ── Retention ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The property the whole two-tier design exists for. Prune hard enough to remove every raw row,
    /// then assert the aggregate is still there and still correct — if the rollup were computed from
    /// the rows rather than written alongside them, this is where months of evidence would vanish.
    /// </summary>
    [TestMethod]
    public async Task Pruning_raw_outcomes_leaves_the_rollup_intact()
    {
        var repository = NewRepository();
        var old = DateTime.UtcNow.AddDays(-500);

        await repository.SaveAutomationOutcomeAsync(
            Outcome(campaignId: "ancient", closedUtc: old, netPkr: 2_000m, r: 2m));

        var (outcomes, daily, _) = await repository.PruneAutomationOutcomesAsync(
            outcomeRetentionDays: 400,
            outcomeMaxRows: 500,
            dailyRetentionDays: 1095,
            gateRejectionRetentionDays: 90);

        Assert.AreEqual(1, outcomes, "The raw row was past its retention date.");
        Assert.AreEqual(0, daily, "The rollup is kept far longer and must not have been touched.");

        Assert.AreEqual(0, (await repository.GetAutomationOutcomesAsync()).Count);

        var rollup = (await repository.GetAutomationOutcomeDailyAsync()).Single();
        Assert.AreEqual(1, rollup.Trades);
        Assert.AreEqual(2m, rollup.SumR, "The evidence must survive the rows it came from.");
    }

    [TestMethod]
    public async Task Recent_outcomes_survive_a_prune()
    {
        var repository = NewRepository();

        await repository.SaveAutomationOutcomeAsync(
            Outcome(campaignId: "recent", closedUtc: DateTime.UtcNow.AddDays(-3)));

        var (outcomes, _, _) = await repository.PruneAutomationOutcomesAsync(400, 500, 1095, 90);

        Assert.AreEqual(0, outcomes);
        Assert.AreEqual(1, (await repository.GetAutomationOutcomesAsync()).Count);
    }

    /// <summary>
    /// Age alone is not enough: a burst of activity could keep the table far above its budget between
    /// sweeps while every row is still comfortably inside the retention window.
    /// </summary>
    [TestMethod]
    public async Task The_row_count_cap_binds_even_when_every_row_is_recent()
    {
        var repository = NewRepository();

        for (var i = 0; i < 10; i++)
        {
            await repository.SaveAutomationOutcomeAsync(Outcome(
                campaignId: $"c{i}",
                closedUtc: DateTime.UtcNow.AddDays(-i)));
        }

        var (outcomes, _, _) = await repository.PruneAutomationOutcomesAsync(
            outcomeRetentionDays: 400, outcomeMaxRows: 4,
            dailyRetentionDays: 1095, gateRejectionRetentionDays: 90);

        Assert.AreEqual(6, outcomes);

        var remaining = await repository.GetAutomationOutcomesAsync();
        Assert.AreEqual(4, remaining.Count);
        // The newest are the ones worth keeping: an old row's detail is the least useful thing in
        // the table, since its contribution to the aggregate is already banked.
        CollectionAssert.AreEqual(
            new[] { "c0", "c1", "c2", "c3" },
            remaining.Select(o => o.CampaignId).ToArray());
    }

    [TestMethod]
    public async Task Old_gate_rejection_counts_expire()
    {
        var repository = NewRepository();
        var stale = DateTime.UtcNow.AddDays(-120).ToString("yyyy-MM-dd");
        var fresh = DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-dd");

        await repository.AddAutomationGateRejectionsAsync(
            stale, "confluence-pullback", new Dictionary<string, int> { ["rsi"] = 5 });
        await repository.AddAutomationGateRejectionsAsync(
            fresh, "confluence-pullback", new Dictionary<string, int> { ["rsi"] = 7 });

        var (_, _, gates) = await repository.PruneAutomationOutcomesAsync(400, 500, 1095, 90);

        Assert.AreEqual(1, gates);
        var rows = await repository.GetAutomationGateRejectionsAsync();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(7, rows[0].Count);
    }

    [TestMethod]
    public async Task Pruning_an_empty_ledger_is_safe_and_repeatable()
    {
        var repository = NewRepository();

        var first = await repository.PruneAutomationOutcomesAsync(400, 500, 1095, 90);
        var second = await repository.PruneAutomationOutcomesAsync(400, 500, 1095, 90);

        Assert.AreEqual((0, 0, 0), first);
        Assert.AreEqual((0, 0, 0), second);
    }

    // ── Reading back ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Outcomes_can_be_filtered_by_symbol_and_by_plan()
    {
        var repository = NewRepository();

        await repository.SaveAutomationOutcomeAsync(
            Outcome(campaignId: "a", symbol: "OGDC", profileId: "pullback-balanced"));
        await repository.SaveAutomationOutcomeAsync(
            Outcome(campaignId: "b", symbol: "NETSOL", profileId: "breakout-trail"));

        Assert.AreEqual(1, (await repository.GetAutomationOutcomesAsync(symbol: "OGDC")).Count);
        Assert.AreEqual(1,
            (await repository.GetAutomationOutcomesAsync(profileId: "breakout-trail")).Count);
        Assert.AreEqual(2, (await repository.GetAutomationOutcomesAsync()).Count);
    }

    [TestMethod]
    public async Task A_symbol_filter_is_case_insensitive_like_every_other_symbol_lookup()
    {
        var repository = NewRepository();
        await repository.SaveAutomationOutcomeAsync(Outcome(symbol: "OGDC"));

        Assert.AreEqual(1, (await repository.GetAutomationOutcomesAsync(symbol: "ogdc")).Count);
    }
}
