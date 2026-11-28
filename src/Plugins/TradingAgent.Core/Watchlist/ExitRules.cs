using TradingAgent.Research;

namespace TradingAgent.Watchlist;

/// <summary>What an exit rule does when it fires (plan step 10).</summary>
public enum ExitRuleAction
{
    /// <summary>Not evaluated.</summary>
    Off = 0,

    /// <summary>Writes a trade proposal and alerts; a person approves the sale. The default for every account (owner,
    /// 2026-11-28: new sell rules are proposal-only until a user opts in).</summary>
    Propose = 1,

    /// <summary>Arms the sale. In ApprovalRequired it still waits for approval at the gate.</summary>
    Act = 2
}

/// <summary>One exit a rule wants, and why.</summary>
public sealed record ExitCandidate(string Rule, string Symbol, string Reason, bool AtNextOpen);

/// <summary>
/// The exit rules of plan step 10, pure: each takes settled data and a position plan and says whether it fires. A rule
/// with no opt-in in the plan never fires — every one of them can sell.
/// </summary>
public static class ExitRules
{
    /// <summary>Closing-price stop: the settled close is below the plan's level → exit at the next open.</summary>
    public static ExitCandidate? ClosingPrice(PositionPlan plan, PsxEodClose settledClose) =>
        plan.ClosingRuleLevel is { } level && settledClose.Close < level
            ? new("closing-price", plan.Symbol,
                $"{plan.Symbol} closed at {settledClose.Close:0.##} on {settledClose.Date:yyyy-MM-dd}, below its "
                + $"closing-price level {level:0.##}.", AtNextOpen: true)
            : null;

    /// <summary>
    /// Gap: the session's first price is below the hard stop's trigger. The native stop then triggers at once and may
    /// strand above the market; this proposes the exit in words rather than leaving the strand to speak for itself.
    /// </summary>
    public static ExitCandidate? Gap(PositionPlan plan, decimal stopTrigger, decimal openingPrice) =>
        plan.GapExit && openingPrice < stopTrigger
            ? new("gap", plan.Symbol,
                $"{plan.Symbol} opened at {openingPrice:0.##}, below its {stopTrigger:0.##} stop.", AtNextOpen: false)
            : null;

    /// <summary>
    /// Time stop: <see cref="PositionPlan.TimeStopWeeks"/> after <see cref="PositionPlan.OpenedOn"/>, no settled close
    /// has reached entry + 1R. Flags only, unless <see cref="PositionPlan.TimeStopExits"/>. Null when the plan lacks
    /// what R needs.
    /// </summary>
    public static (ExitCandidate Candidate, bool Exit)? TimeStop(
        PositionPlan plan, DateOnly today, IReadOnlyList<PsxEodClose> closesSinceOpen)
    {
        if (plan is not { TimeStopWeeks: { } weeks, OpenedOn: { } opened, EntryPrice: { } entry, InitialStop: { } stop }
            || stop >= entry || today < opened.AddDays(7 * weeks) || closesSinceOpen.Count == 0)
            return null;

        var oneR = entry + (entry - stop);
        var best = closesSinceOpen.Where(c => c.Date >= opened).Select(c => c.Close).DefaultIfEmpty(0m).Max();
        if (best >= oneR) return null;

        return (new ExitCandidate("time-stop", plan.Symbol,
            $"{plan.Symbol} has not closed at +1R ({oneR:0.##}) in the {weeks} week(s) since {opened:yyyy-MM-dd}; "
            + $"its best close was {best:0.##}.", AtNextOpen: true), plan.TimeStopExits);
    }

    /// <summary>
    /// Relative strength: the stock / KSE-100 ratio closes below its own 20-session average for the first time (it was
    /// at or above it the session before) while the KSE-100 is flat or up over those 20 sessions — the stock is lagging
    /// a market that is not falling. Needs 21 sessions of both, aligned by date; fewer is unknown and never fires.
    /// </summary>
    public static ExitCandidate? RelativeStrength(
        PositionPlan plan, IReadOnlyList<PsxEodClose> stock, IReadOnlyList<PsxEodClose> index, int window = 20)
    {
        if (!plan.RelativeStrengthExit) return null;

        var byDate = index.ToDictionary(c => c.Date, c => c.Close);
        var ratios = stock.Where(c => byDate.TryGetValue(c.Date, out var i) && i > 0 && c.Close > 0)
            .OrderBy(c => c.Date).Select(c => (c.Date, Ratio: c.Close / byDate[c.Date])).ToList();
        if (ratios.Count < window + 1) return null;

        decimal Average(int end) => ratios.Skip(end - window + 1).Take(window).Average(r => r.Ratio);
        var last = ratios.Count - 1;
        var brokeToday = ratios[last].Ratio < Average(last) && ratios[last - 1].Ratio >= Average(last - 1);

        var indexWindow = index.OrderBy(c => c.Date).TakeLast(window).ToList();
        var indexFlatOrUp = indexWindow.Count == window && indexWindow[^1].Close >= indexWindow[0].Close;

        return brokeToday && indexFlatOrUp
            ? new("relative-strength", plan.Symbol,
                $"{plan.Symbol} has fallen below its {window}-session relative strength against the KSE-100 while the "
                + "index is flat or up.", AtNextOpen: true)
            : null;
    }
}
