using AgentFox.ChannelTests.Simulation;
using TradingAgent.Config;
using TradingAgent.Research;
using TradingAgent.Watchlist;

namespace AgentFox.ChannelTests;

/// <summary>The exit rules of plan step 10. Each fires only when the position plan opts in; every one can sell.</summary>
[TestClass]
public sealed class ExitRulesTests
{
    private static readonly DateOnly Day = new(2026, 11, 30);
    private static PositionPlan Plan(Func<PositionPlan, PositionPlan>? with = null) =>
        (with ?? (p => p))(new PositionPlan { Symbol = "PPL" });

    private static List<PsxEodClose> Closes(params decimal[] values) =>
        values.Select((v, i) => new PsxEodClose(Day.AddDays(i - values.Length + 1), v)).ToList();

    [TestMethod]
    public void TheProductDefaultIsProposalOnly() =>
        Assert.AreEqual(ExitRuleAction.Propose, new TradingAgentOptions().ExitRulesAction);

    [TestMethod]
    public void NoRuleFiresWithoutAnOptIn()
    {
        var plan = Plan();
        Assert.IsNull(ExitRules.ClosingPrice(plan, new PsxEodClose(Day, 1m)));
        Assert.IsNull(ExitRules.Gap(plan, 220m, 1m));
        Assert.IsNull(ExitRules.TimeStop(plan, Day, Closes(1m)));
        Assert.IsNull(ExitRules.RelativeStrength(plan, Closes(Enumerable.Repeat(1m, 30).ToArray()), Closes(Enumerable.Repeat(1m, 30).ToArray())));
    }

    [TestMethod]
    public void ClosingBelowTheLevelExitsAtTheNextOpen()
    {
        var plan = Plan(p => p with { ClosingRuleLevel = 215m });
        Assert.IsNull(ExitRules.ClosingPrice(plan, new PsxEodClose(Day, 215m)), "at the level is not below it");
        var exit = ExitRules.ClosingPrice(plan, new PsxEodClose(Day, 214.5m));
        Assert.IsNotNull(exit);
        Assert.IsTrue(exit.AtNextOpen);
    }

    [TestMethod]
    public void OpeningBelowTheStopIsAGap()
    {
        var plan = Plan(p => p with { GapExit = true });
        Assert.IsNotNull(ExitRules.Gap(plan, 220m, 210m));
        Assert.IsNull(ExitRules.Gap(plan, 220m, 221m));
    }

    [TestMethod]
    public void TheTimeStopFlagsWhenNoCloseReachedOneR_AndExitsOnlyWhenAskedTo()
    {
        var opened = Day.AddDays(-30);
        var plan = Plan(p => p with { TimeStopWeeks = 4, OpenedOn = opened, EntryPrice = 100m, InitialStop = 95m });
        var flat = Closes(100m, 101m, 104m);   // best 104 < 1R target 105

        var flagged = ExitRules.TimeStop(plan, Day, flat);
        Assert.IsNotNull(flagged);
        Assert.IsFalse(flagged.Value.Exit, "flag only by default");
        Assert.IsTrue(ExitRules.TimeStop(plan with { TimeStopExits = true }, Day, flat)!.Value.Exit);

        Assert.IsNull(ExitRules.TimeStop(plan, Day, Closes(100m, 106m)), "reached +1R");
        Assert.IsNull(ExitRules.TimeStop(plan, opened.AddDays(20), flat), "not yet four weeks");
    }

    [TestMethod]
    public void RelativeStrengthFiresOnTheFirstCloseBelowItsAverage_OnlyWhileTheIndexIsNotFalling()
    {
        var plan = Plan(p => p with { RelativeStrengthExit = true });
        var index = Closes(Enumerable.Range(0, 25).Select(i => 1000m + i).ToArray());          // rising
        var stock = Closes(Enumerable.Range(0, 24).Select(i => 100m + i).Append(105m).ToArray()); // lags on the last day

        Assert.IsNotNull(ExitRules.RelativeStrength(plan, stock, index));

        var falling = Closes(Enumerable.Range(0, 25).Select(i => 1100m - i).ToArray());
        Assert.IsNull(ExitRules.RelativeStrength(plan, stock, falling), "a falling market is not the stock's own weakness");
        Assert.IsNull(ExitRules.RelativeStrength(plan, stock.Take(10).ToList(), index), "too few sessions is unknown");
    }

    /// <summary>
    /// A person clicking "execute" on a proposal is an approval. In ApprovalRequired the click now carries the single-use
    /// intent that mode demands (plan step 10); before, every proposal was refused in it.
    /// </summary>
    [TestMethod]
    public async Task InApprovalRequiredAClickedApprovalExecutesTheExactOrdersShown()
    {
        using var h = new ProtectionHarness(0m, "PPL");
        h.Options.ExecutionMode = "ApprovalRequired";
        var groups = new[] { (IReadOnlyList<TradingAgent.Models.TradingSignal>)[new TradingAgent.Models.TradingSignal
            { IsSignal = true, Action = "BUY", Symbol = "PPL", Quantity = 10, OrderType = "LIMIT", EntryPrice = 225m, Confidence = "HIGH" }] };

        var unapproved = await h.Manager.ExecuteGroupsAsync(groups, "proposal:p1",
            TradingAgent.Manager.ExecutionAuthorization.Attendant("owner"));
        var approved = await h.Manager.ExecuteGroupsAsync(groups, "proposal:p1", h.Approvals.AuthorizeAttended(groups, "proposal:p1", "owner"));

        Assert.IsFalse(unapproved.Executed, "Attendant alone is still refused in ApprovalRequired");
        Assert.IsTrue(approved.Executed, approved.Reason);
    }
}
