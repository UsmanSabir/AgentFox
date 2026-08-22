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
    /// Refuse an order that supplies no <c>quantity</c>, instead of sizing it from
    /// <c>Ahk.PerStockBudgetPkr</c>. Default false, which keeps budget sizing — a tip naming a stock
    /// but no share count should still be actionable.
    /// </summary>
    /// <remarks>
    /// Turn this ON whenever the caller is expected to state sizes explicitly — live testing, or any
    /// setup driven by a small local model. Observed on 2026-08-18: a model told to place 10 shares
    /// omitted the argument entirely, and budget sizing produced 75 shares (48,750 PKR). Nothing was
    /// violated — that sat inside <c>MaxOrderValuePkr</c> — which is the point: when quantity is
    /// absent the effective ceiling is the BUDGET, not whatever the requester had in mind. With this
    /// enabled the order is refused and the sizing that would have happened is reported, so the
    /// omission surfaces as an error rather than as a much larger position.
    /// </remarks>
    public bool RequireExplicitQuantity { get; set; } = false;

    /// <summary>
    /// When a paired take-profit SELL can't be placed immediately (its BUY limit hasn't filled yet, so the
    /// account shows no shares — "insufficient exposure"), persist it and retry in the background until the
    /// broker accepts it. Default true. Only transient failures are queued; a permanent rejection is not.
    /// </summary>
    public bool RetryFailedTakeProfit { get; set; } = true;

    /// <summary>Minutes between take-profit retry attempts (only while the market is open). Default 10.</summary>
    public int TakeProfitRetryIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Minutes between protective-stop passes — confirming entry fills and re-placing native stops
    /// (only while the market is open, and only while a stop is open). Default 3, clamped to 1..60.
    ///
    /// <para>
    /// Each pass reads holdings and the outstanding book, both of which drive the real browser and
    /// serialise against order submission, so this is deliberately slower than the monitor's cadence.
    /// Lowering it makes fills confirm sooner at the cost of delaying orders behind page scrapes.
    /// </para>
    /// </summary>
    public int ProtectiveStopPollMinutes { get; set; } = 3;

    /// <summary>How often active good-until-expiry order intents reconcile their DAY orders.</summary>
    public int PersistentOrderPollSeconds { get; set; } = 60;

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

    /// <summary>
    /// Symbols you operate BY HAND: automation may not originate an order for them, and you still can.
    ///
    /// <para>
    /// This is the deny list <see cref="AllowedSymbols"/> cannot express. That list answers "may this
    /// order exist at all" and every path crosses it, so removing a symbol from it does not make the
    /// symbol manual — it makes it untradable, including from the dashboard. What is wanted for a name
    /// you intend to hand-manage is the orthogonal question: "may a ROBOT originate this order". So the
    /// check deliberately does NOT live in <see cref="Risk.TradingRiskEngine"/>; it lives at the
    /// automation boundary (<see cref="Manager.TradingManager"/> and <see cref="Manager.ApprovalGate"/>),
    /// which is the only place the two questions can have different answers.
    /// </para>
    ///
    /// <para>
    /// A manual-only symbol is still charted, scanned, alerted on, and archived exactly as before —
    /// muting it is what <c>alerts_enabled</c> is for. What it loses is unattended EXECUTION: armed
    /// order triggers, protective-stop raises, take-profit retries, monitor-fired orders and strategy
    /// passes are all refused for it, entries and exits alike. That means nothing raises a stop for you
    /// on these names; hand-managing the exit is the whole point of the flag, and is why it is not the
    /// default.
    /// </para>
    ///
    /// <para>
    /// The effective deny set is this list UNION every watchlist entry with automation switched off
    /// (see <see cref="Watchlist.MonitoredUniverse.ManualOnlyAsync"/>). Config is the durable floor —
    /// it cannot be edited away over the web API — while the per-symbol watchlist toggle is for
    /// day-to-day changes. Both only ever NARROW what automation may do, which is why a runtime-editable
    /// entry is safe here when one would not be for <see cref="AllowedSymbols"/>.
    /// </para>
    /// </summary>
    public List<string> ManualOnlySymbols { get; set; } = [];

    public int MaxOrdersPerBatch { get; set; } = 10;
    public decimal MaxBatchValuePkr { get; set; } = 250_000m;

    /// <summary>Block ApprovalRequired and BoundedAuto unless broker reconciliation is healthy.</summary>
    public bool RequireReconciliationHealthy { get; set; } = true;

    public int ReconciliationIntervalSeconds { get; set; } = 60;
    public int ReconciliationMaxAgeSeconds { get; set; } = 180;

    // ── Execution alerts ──────────────────────────────────────────────────────

    /// <summary>
    /// Broadcast an alert to every connected messaging channel whenever an execution completes —
    /// accepted, failed, or unknown. Covers every path into the broker (manual tool calls, armed
    /// orders, take-profit retries, protective stops), because they all run through
    /// <c>TradingManager.ExecuteGroupsAsync</c>. Default true.
    /// </summary>
    public bool NotifyOnExecution { get; set; } = true;

    /// <summary>
    /// Include Paper/Shadow executions in those alerts. Default false: in those modes nothing
    /// reaches the broker, and alerting on every simulated fill trains you to ignore the channel.
    /// </summary>
    public bool NotifyOnSimulatedExecution { get; set; } = false;

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

    /// <summary>
    /// The user-editable monitoring universe. Every value here has a working default, so the watchlist
    /// needs no configuration to function.
    /// </summary>
    public TradingWatchlistOptions Watchlist { get; set; } = new();

    /// <summary>
    /// Continuous trend/level monitoring of the watchlist. Ready to run as configured — the defaults
    /// are chosen to alert on real transitions without flooding, and nothing here can place an order.
    /// </summary>
    public TradingMonitorOptions Monitor { get; set; } = new();

    /// <summary>Lifecycle of persisted trade proposals — the signal inbox.</summary>
    public TradingProposalOptions Proposals { get; set; } = new();

    /// <summary>
    /// When an order needs a human confirmation, and when it may be pre-authorised. Defaults to
    /// <c>Always</c>, so nothing changes for an existing install until this is deliberately relaxed.
    /// </summary>
    public TradingApprovalOptions Approval { get; set; } = new();
}

/// <summary>
/// Approval policy for order submission.
///
/// <para>
/// This layer decides only whether a HUMAN CONFIRMATION is required. It can never bypass the risk
/// engine: the kill switch, <see cref="TradingAgentOptions.AllowedSymbols"/>, the market calendar,
/// reconciliation health and the value caps apply to a pre-approved order exactly as they do to a
/// confirmed one. Every auto-redeemed approval records WHICH rule redeemed it, so a bypassed order is
/// as traceable as one someone clicked.
/// </para>
/// </summary>
public sealed class TradingApprovalOptions
{
    /// <summary>
    /// <c>Always</c> (default) — every order needs a fresh confirmation.
    /// <c>Auto</c> — orders matching every cap in <see cref="Auto"/> proceed without a prompt;
    /// anything outside them still prompts.
    /// <c>Window</c> — prompting is suspended for a bounded, explicitly opened window (see
    /// <see cref="Window"/>).
    ///
    /// <para>
    /// Note this is only consulted when <see cref="TradingAgentOptions.ExecutionMode"/> is
    /// <c>ApprovalRequired</c>. <c>BoundedAuto</c> already authorises unattended execution by
    /// definition, so approval mode does not gate it — the risk engine's limits do.
    /// </para>
    ///
    /// <para>
    /// The value was originally <c>Armed</c>, which collided badly with an "armed order" (an order
    /// waiting on a trigger) and led to exactly the question you would expect: does armed mean
    /// auto-execute? They are orthogonal — a trigger says WHEN, this says WHETHER a human must confirm —
    /// so the window mode is named for what it is.
    /// </para>
    /// </summary>
    public string Mode { get; set; } = "Always";

    public AutoApprovalOptions Auto { get; set; } = new();

    /// <summary>The time-boxed window used by <c>Window</c> mode.</summary>
    public ApprovalWindowOptions Window { get; set; } = new();
}

/// <summary>Caps that define a pre-approved order. ALL of them must hold, or the order still prompts.</summary>
public sealed class AutoApprovalOptions
{
    /// <summary>Largest order value that may skip confirmation.</summary>
    public decimal MaxOrderValuePkr { get; set; } = 25_000m;

    /// <summary>Cap on auto-approved orders per session, so a bad day cannot compound silently.</summary>
    public int MaxOrdersPerSession { get; set; } = 5;

    /// <summary>
    /// Sides eligible for auto-approval. Includes BUY as well as SELL by operator decision — worth
    /// knowing that an auto-approved entry OPENS risk while an auto-approved exit only closes it, so
    /// <see cref="MaxOrderValuePkr"/> and <see cref="MaxOrdersPerSession"/> are the real guardrails
    /// here rather than the side filter. Narrow to ["SELL"] for exits-only.
    /// </summary>
    public List<string> Sides { get; set; } = ["BUY", "SELL"];

    /// <summary>Symbols eligible; empty means any TRADABLE symbol (AllowedSymbols still applies).</summary>
    public List<string> Symbols { get; set; } = [];

    /// <summary>
    /// Only auto-approve an order originating from an alert of at least this severity. Blank allows
    /// ad-hoc orders too. Requiring an alert means a pre-approved order always has a recorded,
    /// deterministic reason behind it.
    /// </summary>
    public string MinAlertSeverity { get; set; } = "High";

    /// <summary>Never auto-approve while the market is closed.</summary>
    public bool RequireMarketOpen { get; set; } = true;
}

/// <summary>A sudo-style window during which confirmation is not requested.</summary>
public sealed class ApprovalWindowOptions
{
    /// <summary>Minutes granted when no explicit duration is asked for.</summary>
    public int DefaultMinutes { get; set; } = 30;

    /// <summary>Upper bound on any single arming request.</summary>
    public int MaxMinutes { get; set; } = 120;

    /// <summary>
    /// Disarm as soon as the market closes, on top of the window expiring. Combined with the automatic
    /// disarm on kill-switch activation and on restart, an armed window cannot outlive the session it
    /// was granted for.
    /// </summary>
    public bool DisarmAtMarketClose { get; set; } = true;
}

/// <summary>
/// Ageing rules for the proposal queue. A proposal is a plan priced at a moment; these settle how long
/// that plan stays actionable, so the queue cannot grow without bound and cannot offer a stale price as
/// though it were current.
/// </summary>
public sealed class TradingProposalOptions
{
    /// <summary>
    /// Hours a proposal stays actionable before it is expired. Default 24 — a tip parsed overnight is
    /// reasonable to act on in the morning, but not days later.
    /// </summary>
    public int TtlHours { get; set; } = 24;

    /// <summary>
    /// Expire a proposal once the live price has moved more than this percent from its stated entry.
    /// Default 3. A stale price is not a tradable plan, and offering one invites acting on a level that
    /// no longer exists. Set 0 to disable drift-based expiry and rely on the TTL alone.
    /// </summary>
    public decimal InvalidateOnDriftPercent { get; set; } = 3m;

    /// <summary>
    /// Days a TERMINAL proposal is retained before pruning. Open proposals are never pruned. Default 90.
    /// </summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// Settings for the background watchlist monitor. It only ever raises alerts: execution stays behind
/// <see cref="TradingAgentOptions.ExecutionMode"/>, the risk engine, and the kill switch.
/// </summary>
public sealed class TradingMonitorOptions
{
    /// <summary>Run the monitor at all. On by default; the watchlist is not much use unwatched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between passes while the market is open (clamped 30–3600). A pass costs ONE market-wide
    /// request regardless of how many symbols are watched, so running at the clamp floor is cheap; the
    /// limit on going faster is the portal's patience, not ours. The one per-symbol cost in a pass —
    /// AHL's daily candles — is cached for <see cref="AhlAnalyticsConfig.DailyCandleCacheMinutes"/>
    /// (hours, not seconds), so quadrupling the pass rate does not quadruple that traffic either.
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Consecutive passes a condition must hold before it becomes an alert. 1 fires immediately and
    /// will flicker when price sits on a level; 2 is the smallest value that filters that.
    /// </summary>
    public int ConfirmPasses { get; set; } = 2;

    /// <summary>
    /// How far past a level a close must be to count as breaking it, in percent. A wick through a
    /// level is noise, and reporting it as a break is how an alert feed loses its reader.
    /// </summary>
    public decimal BreakBufferPercent { get; set; } = 0.5m;

    /// <summary>
    /// Volume (as a multiple of the 30-bar average) required before a break is called confirmed. A
    /// break on thin volume is frequently retraced. Set 0 to accept any volume.
    /// </summary>
    public decimal VolumeConfirmRatio { get; set; } = 1.3m;

    /// <summary>
    /// Minutes before the same symbol + kind + level may alert again. 0 means "the rest of the
    /// session", which is the usual intent: one situation, one alert.
    /// </summary>
    public int CooldownMinutes { get; set; } = 0;

    /// <summary>
    /// Upper bound on alerts raised in a single pass. A circuit breaker for a market-wide move, where
    /// every symbol would otherwise fire at once; the excess is logged rather than silently dropped.
    /// </summary>
    public int MaxAlertsPerPass { get; set; } = 25;

    /// <summary>
    /// Run one settle pass after the close, on top of the in-session cadence, so the day's final bars
    /// are analyzed once they are settled rather than only mid-session.
    /// </summary>
    public bool RunAfterClose { get; set; } = true;

    /// <summary>
    /// Days of alert history retained. Older rows are pruned so the table has a ceiling; the ledger's
    /// own audit trail is unaffected.
    /// </summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>
/// Settings for the editable watchlist — what is WATCHED. Nothing here can widen what may be TRADED:
/// that stays <see cref="TradingAgentOptions.AllowedSymbols"/>, which the risk engine reads directly.
/// </summary>
public sealed class TradingWatchlistOptions
{
    /// <summary>
    /// Prefill the watchlist from <see cref="TradingAgentOptions.AllowedSymbols"/> the first time it is
    /// used, so a new install starts with the configured universe rather than an empty page. Applies
    /// once; afterwards the watchlist is the user's, and a changed allow-list is reported rather than
    /// merged in. Set false to start empty.
    /// </summary>
    public bool SeedFromAllowedSymbols { get; set; } = true;

    /// <summary>
    /// Upper bound on watched symbols. The monitor's per-pass cost is one market-wide request
    /// regardless of count, but each symbol still costs analysis time and archive rows.
    /// </summary>
    public int MaxSymbols { get; set; } = 150;

    /// <summary>
    /// Archive daily history for watchlist symbols too, not just the tradable ones. On by default
    /// because weekly support/resistance needs roughly two years of daily bars, and a watched symbol
    /// without them reports unknown timeframe alignment. Costs no additional portal requests — a
    /// session fetch already covers every symbol in the market — only database rows.
    /// </summary>
    public bool ArchiveWatchlistSymbols { get; set; } = true;

    /// <summary>
    /// Check an added symbol against the live PSX market watch before accepting it, so a typo is
    /// rejected at the point of entry rather than becoming a silently empty chart. When the portal is
    /// unreachable the symbol is accepted with a warning — an outage should not block editing.
    /// </summary>
    public bool ValidateAgainstMarketWatch { get; set; } = true;
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

    /// <summary>
    /// Years of daily OHLC history the background backfill archives into the trading ledger, for the
    /// configured AllowedSymbols. The exchange serves settled candles one date at a time (each request
    /// covering every symbol), so a year is ~250 requests — paced and resumable, and done once rather
    /// than on every process start. Weekly levels need roughly two years to be meaningful. Set 0 to
    /// disable the backfill entirely and rely on the shallower on-demand window.
    /// </summary>
    public int BackfillYears { get; set; } = 2;

    /// <summary>
    /// Weekly bars requested when computing higher-timeframe structure. 104 ≈ two years.
    /// </summary>
    public int WeeklyLookbackWeeks { get; set; } = 104;

    /// <summary>
    /// PKT time of day after which the current session's candles are treated as settled and may be
    /// archived. Default 17:30 — an hour after the latest scheduled close (Friday's 16:30) — so the
    /// exchange has published its final table. Archiving earlier stores a partial bar that the
    /// coverage marker would stop us from ever correcting.
    /// </summary>
    public string ArchiveSettleAfterPkt { get; set; } = "17:30";

    /// <summary>
    /// How close a weekly level must sit to a daily level to count as confirming it. Wider means more
    /// levels read as "confirmed" on weaker evidence.
    /// </summary>
    public decimal ConfluenceTolerancePercent { get; set; } = 2.0m;

    /// <summary>
    /// Archive completed intraday bars to the trading ledger. PSX serves intraday data for the CURRENT
    /// session only, so this is the only way multi-session intraday history can exist — it accumulates
    /// from the day it is switched on. Default true.
    /// </summary>
    public bool ArchiveIntradayBars { get; set; } = true;

    /// <summary>
    /// Archived intraday bars loaded per analysis, in addition to the current session rebuilt from
    /// ticks. 120 covers roughly a week and a half of 15m bars.
    /// </summary>
    public int IntradayLookbackBars { get; set; } = 120;

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
