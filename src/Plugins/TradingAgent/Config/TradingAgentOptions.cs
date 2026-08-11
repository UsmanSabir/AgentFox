namespace TradingAgent.Config;

public class TradingAgentOptions
{
    public const string SectionName = "TradingAgent";

    public bool AutoExecute { get; set; } = false;

    // HIGH, MEDIUM, or LOW
    public string MinConfidence { get; set; } = "HIGH";

    // Named model key (from the Models config section) the trading specialist runs on.
    // Leave commented/missing or empty to fall back to the default LLM chat client.
    public string ParserModelKey { get; set; } = "";

    /// <summary>
    /// Specialist memory mode: Shared uses AgentFox memory, Isolated uses a private persistent
    /// trading-agent store, and Disabled turns memory off for the specialist. Default: Shared.
    /// </summary>
    public string MemoryMode { get; set; } = "Shared";

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

    // ── Stock research (research_stock tool) ──────────────────────────────────

    /// <summary>Base URL of the official PSX data portal used for quotes and price history.</summary>
    public string PsxDataBaseUrl { get; set; } = "https://dps.psx.com.pk";

    /// <summary>
    /// Also pull recent company and market headlines (Google News RSS, keyless) into the research
    /// evidence. Disable to research from PSX price data only (e.g. no outbound internet policy).
    /// </summary>
    public bool ResearchNewsEnabled { get; set; } = true;

    /// <summary>Maximum headlines per news query fed to the research assessment. Default 8.</summary>
    public int ResearchHeadlineCount { get; set; } = 8;

    /// <summary>Expose the configured provider-backed read-only web research tool to the specialist.</summary>
    public bool ResearchWebEnabled { get; set; } = true;

    /// <summary>Maximum provider results returned by the specialist web research tool.</summary>
    public int ResearchWebMaxResults { get; set; } = 5;

    /// <summary>Provider search depth. Supported values are basic and advanced.</summary>
    public string ResearchWebSearchDepth { get; set; } = "basic";

    /// <summary>Maximum characters retained from each external result snippet.</summary>
    public int ResearchWebMaxContentCharacters { get; set; } = 4000;

    /// <summary>
    /// Wall-clock budget for a single trading-agent turn (specialist lane timeout). A turn can chain
    /// AHK browser automation (get_portfolio) with PSX/news fetches and an LLM verdict across several
    /// tool-call iterations, which routinely exceeds the platform's 300s specialist default. Default 600.
    /// </summary>
    public int SpecialistTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Candle-based technical scanning (scan_watchlist / analyze_candles) and the support/resistance
    /// thresholds that turn a candle series into a buy-at-support or sell-at-resistance setup.
    /// </summary>
    public TradingScanOptions Scan { get; set; } = new();
}

/// <summary>
/// Settings for the deterministic candle scanner. Nothing here can place an order: the scanner
/// only ranks candidates, and execution stays behind <see cref="TradingAgentOptions.ExecutionMode"/>
/// and the risk engine.
/// </summary>
public sealed class TradingScanOptions
{
    /// <summary>
    /// Settled trading sessions of OHLC history loaded per scan (clamped to 5–250). Support and
    /// resistance are only as meaningful as the window they are drawn from; 60 sessions ≈ 3 months.
    /// Each session costs ONE market-wide portal request regardless of how many symbols are scanned,
    /// and settled sessions are cached, so only a new trading day is ever fetched again.
    /// </summary>
    public int LookbackDays { get; set; } = 60;

    /// <summary>Settled sessions kept in the in-memory cache (each holds a row per traded symbol).</summary>
    public int MaxCachedMarketDays { get; set; } = 120;

    /// <summary>Concurrent portal requests while warming a cold candle cache (clamped to 1–8).</summary>
    public int MarketDayFetchConcurrency { get; set; } = 4;

    /// <summary>Seconds a live market-watch snapshot is reused before refetching (clamped to 5–900).</summary>
    public int MarketWatchCacheSeconds { get; set; } = 60;

    /// <summary>Within this percent of a support level counts as "at support" (buy zone).</summary>
    public decimal SupportProximityPercent { get; set; } = 2.5m;

    /// <summary>Within this percent of a resistance level counts as "at resistance" (sell zone).</summary>
    public decimal ResistanceProximityPercent { get; set; } = 2.5m;

    /// <summary>
    /// Minimum (target − entry) / (entry − stop) for a buy-at-support candidate to be reported.
    /// Candidates below the floor are still analyzed; they are simply not offered as setups.
    /// </summary>
    public decimal MinRewardRisk { get; set; } = 1.5m;

    /// <summary>
    /// Minimum 30-session average volume for a scan candidate. Thinly traded symbols produce
    /// support/resistance levels that cannot actually be traded at the quoted price.
    /// </summary>
    public long MinAverageVolume { get; set; } = 25_000;

    /// <summary>Maximum candidates returned per side by scan_watchlist.</summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>Sessions used for the range-position and new-low/new-high comparisons.</summary>
    public int RangeWindow { get; set; } = 20;

    /// <summary>Bars either side of a bar for it to count as a swing pivot high/low.</summary>
    public int PivotWindow { get; set; } = 3;

    /// <summary>Levels within this percent of each other are merged into one level (touch count).</summary>
    public decimal LevelClusterPercent { get; set; } = 1.5m;

    /// <summary>ATR multiple placed below the entry when suggesting a protective stop.</summary>
    public decimal StopAtrMultiple { get; set; } = 1.0m;

    /// <summary>RSI(14) at or below this reads as oversold (supports a bounce case).</summary>
    public decimal RsiOversold { get; set; } = 35m;

    /// <summary>RSI(14) at or above this reads as overbought (supports taking profit).</summary>
    public decimal RsiOverbought { get; set; } = 70m;

    /// <summary>
    /// Consecutive down sessions that, combined with a fresh range low, mark a breakdown rather
    /// than a support test — the "falling knife" guard that keeps a collapsing stock out of the
    /// buy list even though its price is at the bottom of its range.
    /// </summary>
    public int BreakdownDownDays { get; set; } = 3;
}

public sealed class MarketSessionOverride
{
    public string Date { get; set; } = "";
    public bool Closed { get; set; }
    public List<string> Sessions { get; set; } = [];
}
