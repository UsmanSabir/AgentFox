namespace TradingAgent.Watchlist;

/// <summary>
/// The owner's protection plan for one held position (plan step 9, R1): the levels and rules the agent keeps for it,
/// independent of what the broker shows. <see cref="ProtectiveStop"/> rows remain the materialised hard stop; this is the
/// record of what SHOULD exist and why, which later rules (step 10: closing-price stop, gap rule, time stop, relative
/// strength, lower-lock queue) read.
///
/// <para>
/// <b>Every rule that can sell defaults to OFF</b> (owner, 2026-11-28: new sell rules are off or proposal-only for
/// existing accounts and opt-in per user). A plan with no fields set changes nothing; its only effect today is a
/// per-position <see cref="CatastrophicTrigger"/>, which only RELAXES the reopen hold for that position.
/// </para>
/// </summary>
public sealed record PositionPlan
{
    public required string Symbol { get; init; }

    /// <summary><c>TRADE</c> (ATR and trail rules) or <c>INVESTMENT</c> (a wide catastrophic stop, weekly-close and
    /// thesis-break rules).</summary>
    public string Mode { get; init; } = "TRADE";

    /// <summary>The price the position was entered at, for R.</summary>
    public decimal? EntryPrice { get; init; }

    /// <summary>The stop at entry: R = entry − initial stop. Never moved; trailing moves the stop, not R.</summary>
    public decimal? InitialStop { get; init; }

    /// <summary>
    /// How far below the trigger a stop-limit's limit sits, in percent (replaces the hard-coded 0.99). Null = the
    /// default (<c>StopLimitOffsetPct</c>).
    /// </summary>
    public decimal? StopLimitOffsetPct { get; init; }

    /// <summary>
    /// The price below which the reopen hold no longer applies and a breached stop or backstop acts at once. Null = the
    /// stop's trigger × (1 − <c>ProtectiveStopCatastrophicPercent</c>), the owner's "trigger × 0.95".
    /// </summary>
    public decimal? CatastrophicTrigger { get; init; }

    /// <summary>Closing-price rule level: a settled close below it queues an exit at the next open. Inactive until step 10.</summary>
    public decimal? ClosingRuleLevel { get; init; }

    /// <summary>Trail k in k × ATR14 below the highest settled close. Inactive until step 10.</summary>
    public decimal? TrailAtrMultiple { get; init; }

    /// <summary>Highest settled close since entry, for the trail. Written by the rule, not by a person.</summary>
    public decimal? HighestClose { get; init; }

    /// <summary>No +1R within this many weeks → flag (or exit, if <see cref="TimeStopExits"/>). Inactive until step 10.</summary>
    public int? TimeStopWeeks { get; init; }

    public bool TimeStopExits { get; init; }

    /// <summary>Relative-strength exit against the KSE-100. Off by default.</summary>
    public bool RelativeStrengthExit { get; init; }

    /// <summary>When the position was opened, for the time stop. Null = the time stop has nothing to count from.</summary>
    public DateOnly? OpenedOn { get; init; }

    /// <summary>Opens below the hard stop's trigger → propose (or, opted in, arm) an exit. Off by default.</summary>
    public bool GapExit { get; init; }

    /// <summary>Queue a sell at the lower circuit when the stock locks. Off, and behind its global flag, until the broker
    /// confirms such a sell is accepted.</summary>
    public bool LowerLockQueue { get; init; }

    public string? Note { get; init; }

    /// <summary>Optimistic concurrency: a save names the version it read, and is refused if another write came first.</summary>
    public int Version { get; init; }

    public DateTime UpdatedUtc { get; init; }
    public string? UpdatedBy { get; init; }
}

public static class PositionPlans
{
    public static readonly IReadOnlyList<string> Modes = ["TRADE", "INVESTMENT"];

    /// <summary>Every problem with a plan, in words; empty when it may be saved.</summary>
    public static IReadOnlyList<string> Validate(PositionPlan plan)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(plan.Symbol)) problems.Add("A symbol is required.");
        if (!Modes.Contains(plan.Mode)) problems.Add("Mode must be TRADE or INVESTMENT.");
        if (plan.EntryPrice is <= 0) problems.Add("The entry price must be positive.");
        if (plan.InitialStop is <= 0) problems.Add("The initial stop must be positive.");
        if (plan.EntryPrice is { } e && plan.InitialStop is { } s && s >= e)
            problems.Add("The initial stop must be below the entry price, or R is not a risk.");
        if (plan.StopLimitOffsetPct is < 0 or > 10) problems.Add("The stop-limit offset must be between 0% and 10%.");
        if (plan.CatastrophicTrigger is <= 0) problems.Add("The catastrophic level must be positive.");
        if (plan.ClosingRuleLevel is <= 0) problems.Add("The closing-rule level must be positive.");
        if (plan.TrailAtrMultiple is < 0.5m or > 10m) problems.Add("The trail multiple must be between 0.5 and 10.");
        if (plan.TimeStopWeeks is < 1 or > 52) problems.Add("The time stop must be between 1 and 52 weeks.");
        return problems;
    }

    /// <summary>The catastrophic level for a stop: the plan's own, or trigger × (1 − default percent / 100).</summary>
    public static decimal CatastrophicFor(decimal stopTrigger, PositionPlan? plan, decimal defaultPercent) =>
        plan?.CatastrophicTrigger is { } level && level > 0 ? level : stopTrigger * (1m - defaultPercent / 100m);

    /// <summary>The limit for a stop-limit at <paramref name="trigger"/>: the plan's offset, or the default.</summary>
    public static decimal LimitFor(decimal trigger, PositionPlan? plan, decimal defaultOffsetPct) =>
        Math.Round(trigger * (1m - (plan?.StopLimitOffsetPct ?? defaultOffsetPct) / 100m), 2, MidpointRounding.AwayFromZero);
}
