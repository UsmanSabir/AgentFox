namespace TradingAgent.Config;

public class TradingAgentOptions
{
    public const string SectionName = "TradingAgent";

    public bool AutoExecute { get; set; } = false;

    // HIGH, MEDIUM, or LOW
    public string MinConfidence { get; set; } = "HIGH";

    // Reserved: will resolve via IModelClientFactory once added to AgentFox.Plugins.
    // Currently the default IChatClient (from DI) is used for signal parsing.
    public string ParserModelKey { get; set; } = "CheapModel";

    public int DuplicateWindowMinutes { get; set; } = 60;

    /// <summary>
    /// When a BUY tip also specifies a target ("buy at 50, sell at 55"), automatically place a
    /// take-profit SELL limit order at the target after the BUY succeeds (default true). The follow-up
    /// sell is only attempted when the BUY actually succeeded. Set false to place only the BUY.
    /// </summary>
    public bool AutoPlaceTargetSell { get; set; } = true;

    /// <summary>
    /// How to handle a BUY tip that names a stock with a clear buy intent but gives NO entry price
    /// (e.g. "accumulate on dips"). When true, the entry is resolved from the live market price less
    /// <c>Ahk.DipDiscountPercent</c> and the order is placed (budget-sized). When false (default) the tip
    /// is recognized and logged but NOT executed, so a human can place it manually. Because a
    /// dip-discounted limit rests below market and may not fill, no take-profit SELL is auto-paired for
    /// these orders even when a target is given.
    /// </summary>
    public bool AutoBuyWithoutEntryPrice { get; set; } = false;

    /// <summary>
    /// When a paired take-profit SELL can't be placed immediately (its BUY limit hasn't filled yet, so the
    /// account shows no shares — "insufficient exposure"), persist it and retry in the background until the
    /// broker accepts it. Default true. Only transient failures are queued; a permanent rejection is not.
    /// </summary>
    public bool RetryFailedTakeProfit { get; set; } = true;

    /// <summary>Minutes between take-profit retry attempts (only while the market is open). Default 10.</summary>
    public int TakeProfitRetryIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Seconds an approval intent stays valid between validation and broker submission
    /// (ApprovalRequired mode). Expired intents are rejected and need re-approval. Default 120,
    /// floored at 10.
    /// </summary>
    public int ApprovalIntentTtlSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum take-profit retry attempts before giving up (logged). At the default 10-min interval, 36
    /// attempts ≈ one full trading session. Attempts accrue only while the market is open.
    /// </summary>
    public int TakeProfitRetryMaxAttempts { get; set; } = 36;

    /// <summary>
    /// Operational mode for the deterministic trading manager. Supported values are Disabled,
    /// Paper, Shadow, ApprovalRequired, and BoundedAuto. The legacy AutoExecute switch is still
    /// honoured as an additional hard off-switch; both controls must allow execution.
    /// </summary>
    public string ExecutionMode { get; set; } = "Disabled";

    /// <summary>SQLite database path used by the operational trading ledger.</summary>
    public string DatabasePath { get; set; } = "trading/trading.db";

    /// <summary>
    /// Explicit PSX holidays in yyyy-MM-dd form. The calendar fails closed for an invalid entry.
    /// Keep this list current until an authoritative calendar feed is configured.
    /// </summary>
    public List<string> MarketHolidays { get; set; } = [];

    /// <summary>
    /// Date-specific market overrides for holidays, Ramadan timings, and emergency schedule changes.
    /// </summary>
    public List<MarketSessionOverride> MarketSessionOverrides { get; set; } = [];

    /// <summary>
    /// Reject execution when calendar configuration contains an invalid date or time range.
    /// </summary>
    public bool FailClosedOnCalendarError { get; set; } = true;

    /// <summary>Require signed WhatsApp bridge webhooks by default.</summary>
    public bool RequireSignedWebhooks { get; set; } = true;

    /// <summary>Emergency execution stop independent of AutoExecute and the LLM.</summary>
    public bool KillSwitch { get; set; }

    /// <summary>Only these PSX symbols may pass deterministic risk validation.</summary>
    public List<string> AllowedSymbols { get; set; } = [];

    /// <summary>Fail closed when AllowedSymbols is empty (recommended).</summary>
    public bool RequireConfiguredSymbols { get; set; } = true;

    public int MaxOrdersPerBatch { get; set; } = 10;
    public decimal MaxBatchValuePkr { get; set; } = 250_000m;

    /// <summary>Block ApprovalRequired and BoundedAuto unless broker reconciliation is healthy.</summary>
    public bool RequireReconciliationHealthy { get; set; } = true;

    public int ReconciliationIntervalSeconds { get; set; } = 60;
    public int ReconciliationMaxAgeSeconds { get; set; } = 180;
}

public sealed class MarketSessionOverride
{
    public string Date { get; set; } = "";
    public bool Closed { get; set; }
    public List<string> Sessions { get; set; } = [];
}
