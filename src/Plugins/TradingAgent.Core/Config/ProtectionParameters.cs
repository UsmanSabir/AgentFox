using System.Globalization;
using System.Text;

namespace TradingAgent.Config;

/// <summary>One protection parameter: where it is set, what it means, and its unit.</summary>
/// <param name="Path">The property path under <c>Plugins:TradingAgent</c> (e.g. <c>Watchdog.IntervalSeconds</c>).</param>
/// <param name="RuntimeKey">The Settings (runtime) key that overrides it, when one exists.</param>
public sealed record ProtectionParameter(string Group, string Path, string Unit, string Meaning, string? RuntimeKey = null);

/// <summary>
/// Every protection parameter in one place, with its meaning, unit and default (plan step 9, R6: "every parameter in one
/// config file with its meaning and units"). The parameters stay where the code reads them — moving them would change
/// every saved configuration — and this catalog is the single description of them. It generates
/// <c>docs/protection-parameters.md</c>, and a test fails when the document, a default or a property name drifts.
/// </summary>
public static class ProtectionParameters
{
    public static readonly IReadOnlyList<ProtectionParameter> All =
    [
        new("Trading stop", "TradingStop", "Off / EntriesOff / FullStop",
            "EntriesOff refuses buys and keeps every sell (stops, take-profits, exits) working; FullStop sends nothing.", "tradingStop"),
        new("Trading stop", "KillSwitch", "true / false", "Legacy emergency stop: true is FullStop.", "killSwitch"),
        new("Trading stop", "DailyLossLimitPct", "% of start-of-day value",
            "The day's loss at which new buys switch off until the next session. 0 = off.", "dailyLossLimitPct"),
        new("Trading stop", "DailyLossLimitPkr", "PKR", "The same as an amount; the first limit reached applies. 0 = off.", "dailyLossLimitPkr"),

        new("Protective stops", "ProtectiveStopPollMinutes", "minutes", "Time between stop passes inside a session."),
        new("Protective stops", "ProtectiveStopLiveCoverage", "Off / Shadow / On",
            "Read coverage from the book order by order, re-placing a stop whose order left it mid-session."),
        new("Protective stops", "ProtectiveStopPropagationSeconds", "seconds",
            "After a placement, how long an order may be missing from the book and still count as covering."),
        new("Protective stops", "ProtectiveStopAbsenceConfirmSeconds", "seconds",
            "How long an order missing with no recorded reason must stay missing before it counts as gone."),
        new("Protective stops", "ProtectiveStopMissedGraceSeconds", "seconds",
            "How long a triggered stop may rest unfilled before it is reported missed (and sold at market if opted in)."),
        new("Protective stops", "ProtectiveStopRealertMinutes", "minutes",
            "Repeat interval for 'stop refused' alerts while the stop stays unplaced. 0 = once a session."),
        new("Protective stops", "ProtectiveStopCatastrophicPercent", "% below the trigger",
            "Default catastrophic level; a position plan may set its own."),
        new("Protective stops", "StopLimitOffsetPct", "% below the trigger",
            "Default distance of a stop-limit's limit below its trigger; a position plan may set its own."),

        new("Exit rules", "ExitRulesAction", "Off / Propose / Act",
            "What a position plan's exit rules do when they fire: propose a sale you approve, or arm it.", "exitRulesAction"),

        new("Halts", "MarketHaltHandling", "Off / Shadow / On", "Gate the workers on a detected market halt and its reopen."),
        new("Halts", "HaltIndexSymbol", "index symbol", "The index whose move starts a halt."),
        new("Halts", "HaltIndexMovePercent", "% from previous close", "The index move, either way, that starts a halt."),
        new("Halts", "HaltMaxMinutes", "minutes", "How long a halt may go unconfirmed-ended before it is Unknown."),
        new("Halts", "ReopenBackstopHoldMinutes", "minutes",
            "After a reopen, how long backstops and breached stops wait unless the price is below the catastrophic level."),
        new("Halts", "MarketStatePollSeconds", "seconds", "Time between market-state evaluations inside a session."),

        new("Reconciliation", "RequireReconciliationHealthy", "true / false",
            "Entries need a healthy, recent account read; sells need only a fresh book read."),
        new("Reconciliation", "ReconciliationIntervalSeconds", "seconds", "Time between account reads."),
        new("Reconciliation", "ReconciliationMaxAgeSeconds", "seconds", "How old an account read may be for an entry."),
        new("Reconciliation", "ReduceOnlyBookMaxAgeSeconds", "seconds", "How old the book read behind a sell may be."),

        new("Audit", "LedgerRetentionDays", "days",
            "How long orders, their order events and broker answers, stop order rows and shadow decisions are kept. 0 = forever."),
        new("Audit", "ReconciliationRetentionDays", "days", "How long account snapshots are kept. 0 = forever."),

        new("Dead-man", "Watchdog.HeartbeatUrl", "URL", "Where heartbeats go. Empty = off."),
        new("Dead-man", "Watchdog.IntervalSeconds", "seconds", "Time between heartbeats inside a session."),
        new("Dead-man", "Watchdog.FeedSilentSeconds", "seconds", "No price from any source for this long counts as feed-silent."),
    ];

    /// <summary>The default of one parameter, read from a fresh options object, as text.</summary>
    public static string DefaultOf(ProtectionParameter p)
    {
        object? value = new TradingAgentOptions();
        foreach (var part in p.Path.Split('.'))
        {
            var property = value?.GetType().GetProperty(part)
                ?? throw new InvalidOperationException($"TradingAgentOptions has no '{p.Path}'.");
            value = property.GetValue(value);
        }
        return value switch
        {
            null => "(empty)",
            string s when s.Length == 0 => "(empty)",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>The generated document.</summary>
    public static string Markdown()
    {
        var md = new StringBuilder();
        md.AppendLine("# Protection parameters");
        md.AppendLine();
        md.AppendLine("Generated from `TradingAgent.Config.ProtectionParameters` — edit the catalog, not this file.");
        md.AppendLine("Each is set under `Plugins:TradingAgent` (appsettings or environment, e.g. `Plugins__TradingAgent__DailyLossLimitPct`).");
        md.AppendLine("Where a Settings key is listed, the value saved in Settings overrides the startup value without a restart.");
        foreach (var group in All.GroupBy(p => p.Group))
        {
            md.AppendLine();
            md.AppendLine($"## {group.Key}");
            md.AppendLine();
            md.AppendLine("| Parameter | Default | Unit | Settings key | Meaning |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var p in group)
                md.AppendLine($"| `{p.Path}` | `{DefaultOf(p)}` | {p.Unit} | {(p.RuntimeKey is null ? "—" : $"`{p.RuntimeKey}`")} | {p.Meaning} |");
        }
        return md.ToString().Replace("\r\n", "\n");
    }
}
