# Premium Auto-Trading Agent Plan

Status: proposed implementation plan  
Prepared: 2026-08-19  
Revised: 2026-08-19, after the AHL Analytics portal integration landed  
Target: [TradingAgent.csproj](TradingAgent.csproj)

The revision does not change the architecture. It updates the data-availability assumptions the
phasing was built on, adds three hazards that the original could not have known about, and sharpens
one licensing gate. Changes are marked "Revised 2026-08-19" in place.

## 1. Executive decision

Build premium auto-trading as a separately licensed edition composed around the existing public
TradingAgent core. Do not give the LLM a broker tool and do not create a second execution path.
Every live order, regardless of strategy, must still cross TradingManager, the deterministic risk
engine, the order-window check, durable idempotency, reconciliation, and the audit ledger.

The first supported product combinations should be:

1. Delivery/Swing + Trend Following
2. Delivery/Swing + Price Action and Market Structure
3. Intraday/Swing + Breakout Momentum
4. Delivery + Value and Dividend, only after a point-in-time fundamentals and corporate-actions
   dataset is proven complete

Delivery and Intraday are portfolio/holding modes, while Trend, Breakout, Momentum, Price Action,
and Value are strategy families. Keeping those as separate axes avoids a growing set of ambiguous
mode names.

The recommended source-control and packaging model is:

- Keep all proprietary automation source in a separate private repository.
- Extract a versioned, public TradingAgent core/extension seam from this repository.
- Build one premium entry plugin in the private repository by referencing the public core packages.
- Deploy either the community entry plugin or the premium entry plugin, never both side by side.
- Publish premium artifacts from the private repository. Do not make this public repository restore
  or build a private package.
- If the strategy itself is valuable intellectual property, run its scoring/model orchestration in a
  private service and return signed trade intents. A .NET DLL keeps source out of GitHub but can be
  decompiled and is not a true secrecy boundary.

Live retail automation is not a phase-one launch target. The official SECP material located during
this review is a 2025 concept paper proposing phased access initially limited to institutional
investors. Written approval from the broker and qualified Pakistani legal/compliance review are
release gates before retail live-auto can be enabled.

## 2. What exists in this repository

The plan is based on the current implementation rather than a greenfield design.

### 2.1 Existing boundaries worth preserving

- [TradingAgentModule.cs](TradingAgentModule.cs) is the composition root. It registers the AHK
  broker, market calendar, quote sources, candle analysis, assessment service, SQLite repository,
  risk engine, reconciliation, approval gate, TradingManager, and hosted workers. It also maps the
  management API and registers the isolated specialist.
- [TradingManager.cs](Manager/TradingManager.cs) is explicitly the sole deterministic execution
  boundary. It checks AutoExecute and ExecutionMode, invokes ITradingRiskEngine, requires healthy
  reconciliation for ApprovalRequired and BoundedAuto, evaluates the broker-aware order window,
  claims a durable idempotency key, records events, and only then calls IBrokerAdapter.
- [TradingRiskEngine.cs](Risk/TradingRiskEngine.cs) already fails closed for an empty execution
  universe and enforces the kill switch, allowed symbols, order type, positive quantity, stop-limit
  geometry, per-order value, batch count, and batch value.
- [ApprovalGate.cs](Manager/ApprovalGate.cs) keeps approval separate from risk. BoundedAuto authorizes
  unattended execution, while ApprovalRequired can mint an immutable, expiring, single-use intent.
- [SqliteTradingRepository.cs](Persistence/SqliteTradingRepository.cs) and the reconciliation worker
  provide a durable operational ledger rather than relying on model conversation history.
- [TechnicalSnapshot.cs](Analysis/TechnicalSnapshot.cs) already exposes deterministic SMA20/SMA50,
  RSI14, ATR14, volume ratio, range position, pivot support/resistance, new highs/lows, trend, and
  proposed level-based entry/stop/target.
- [CandleAnalysisService.cs](Analysis/CandleAnalysisService.cs) shares analysis between tools and the
  UI and supports daily, weekly, and accumulated intraday history.
- [AlertDetector.cs](Watchlist/AlertDetector.cs) already detects support bounces, resistance
  rejections, support breaks, resistance breakouts, setup changes, trend flips, and RSI transitions.
- [StockAssessmentService.cs](Research/StockAssessmentService.cs) already follows the right AI
  pattern: deterministic evidence in, structured judgement out, no invented prices, conservative
  fallback to INSUFFICIENT_DATA, and model identity in the result.
- [IBrokerAdapter.cs](Broker/IBrokerAdapter.cs) keeps the portal/browser implementation behind a
  narrow broker interface. Reconciliation requires outstanding orders, activity, holdings, and cash
  to be readable before live bounded automation is considered healthy.
- Revised 2026-08-19: [AhlAnalyticsClient.cs](AhlAnalytics/AhlAnalyticsClient.cs) adds a read-only
  research surface — the whole market (857 equities) in one call with RSI, pivots, beta, free float,
  circuit caps and index membership; five years of daily candles per symbol in one request; native
  one-minute intraday; 41 fundamental ratios with sector medians; payout, board-meeting and
  insider-dealing feeds. [AhlMovers.cs](AhlAnalytics/AhlMovers.cs) derives screens from the cached
  snapshot. This materially reduces the data gap behind several strategies, and
  [docs/ahl-analytics-api.md](docs/ahl-analytics-api.md) records exactly what it does and does not
  provide. It carries no order path and is never on the execution path.

### 2.2 Gaps before unattended strategy execution

The existing code safely executes explicit orders, but it is not yet a portfolio-aware auto-trader.
The following are prerequisites, not optional polish:

- TradingRiskEngine is synchronous and order-local. It does not yet validate buying power, current
  position concentration, sector exposure, daily realized/unrealized loss, turnover, strategy
  cooldown, stale evidence, or the number of open positions.
- TradingSignal has order fields but no strategy identity, evidence snapshot, model decision,
  expected holding horizon, signal expiry, or exit policy.
- TradingManager accepts groups plus a source message. Automatic decisions need first-class,
  queryable provenance rather than encoding it in free text.
- PositionSizer is budget based. Automated entries need risk-at-stop sizing and portfolio caps.
- Paper and Shadow suppress broker submission, but there is no simulated portfolio with fills,
  fees, slippage, partial fills, expiry, and mark-to-market performance.
- The current watchlist monitor generates alerts only. This is a good boundary; it should not be
  mutated into an order placer.
- The broker path is an observed portal API plus browser fallback, not a documented broker-supported
  retail algorithmic API in this repository. Written AHL approval and a supported integration
  contract are required before unattended use.
- The pre-open window deserves special handling. PSX states that pre-open orders cannot be cancelled,
  modified, or suspended until the break ends. New automated entries should therefore default to
  regular open sessions only, even though the current OrderWindow can accept broker-reported OHO.
- Revised 2026-08-19: candle history now has two sources on different price bases, and a strategy that
  ignores the difference will be wrong rather than merely imprecise. AHL bars are corporate-action
  ADJUSTED; PSX bars are raw as-traded. LUCK's 2024-08-19 close is 166.29 adjusted against 853.02 as
  traded, with volume scaled by the same factor of five. Adjusted bars are the CORRECT input for
  indicators and levels — a raw series carries an artificial cliff on every split date, and any
  indicator computed across it is meaningless — and the WRONG input for anything compared against a
  fill. `CandleHistory.Sources` reports the basis per symbol; strategies and risk rules must read it
  rather than assume one.
- Revised 2026-08-19: there is no market-depth data on the analytics portal. Every order-book endpoint
  returns 500 and its websocket carries best bid/ask only. Depth remains available solely from the
  broker feed (`AhkQuoteBook`), which is session-bound. Any strategy whose liquidity or slippage model
  needs the book must therefore declare a dependency on a live broker session, not on research data.
- Revised 2026-08-19: broker login rate is an operational constraint with an incident behind it. The
  broker briefly blocked account access on 2026-08-18, attributed to roughly fifteen browser logins in
  two hours from repeated host restarts. An unattended coordinator that can cause a login — directly,
  or indirectly by demanding a session-dependent read — must budget logins explicitly and treat the
  budget as a circuit breaker.

## 3. Product model

### 3.1 Separate the three axes

An auto-trading profile should contain one value from each axis:

| Axis | Initial values | Meaning |
| --- | --- | --- |
| Account/settlement | Ready Delivery only | The market/account capability. Do not add futures, margin, blank sale, or short sale in v1. |
| Holding mode | Delivery, Intraday | Whether a position may remain overnight. |
| Horizon | Intraday, Swing, Position | Expected evaluation and holding window. |
| Strategy | Trend, Breakout Momentum, Market Structure, Value Dividend | Why a trade exists and which deterministic rules govern it. |
| Execution | Paper, Shadow, ApprovalRequired, BoundedAuto | Existing operational authorization mode. |

Example profiles:

- Delivery / Swing / Trend / ApprovalRequired
- Delivery / Swing / MarketStructure / BoundedAuto
- Intraday / Intraday / BreakoutMomentum / Shadow
- Delivery / Position / ValueDividend / ApprovalRequired

### 3.2 Strategies selected for the first roadmap

| Strategy | Deterministic entry basis | AI role | Exit basis | Readiness |
| --- | --- | --- | --- | --- |
| Trend Following | Daily and weekly trend agree; SMA20/SMA50 alignment; positive slope/return persistence; ATR and liquidity filters; pullback or continuation entry | Classify regime and evidence quality; flag contradictory news or market context; abstain on missing/conflicting evidence | ATR/structure stop, trailing stop, trend reversal, time stop | First |
| Price Action and Market Structure | Existing pivots, clustered support/resistance, range position, support bounce/resistance rejection, weekly confirmation | Judge whether the structured level evidence is coherent; identify invalidation from supplied levels only | Structure break, target level, ATR stop, max holding time | First |
| Breakout Momentum | Settled close above resistance/range high, volume confirmation, minimum liquidity, maximum extension from breakout, market/sector trend filter | Assess catalyst/news consistency and false-break risk; never decide the breakout price or size | Failed-break exit, ATR stop, trailing stop, end-of-day flatten for Intraday | Second |
| Value and Dividend | Point-in-time P/E, P/B, earnings/cash-flow quality, leverage, dividend history/coverage, valuation relative to sector, liquidity | Summarize filed fundamentals and identify anomalies or thesis risks with citations | Thesis deterioration, valuation target, dividend cut, rebalance schedule | Later |

Momentum is treated as confirmation inside Trend and Breakout rather than a fifth overlapping
strategy. Swing is treated as a horizon, not a signal generator.

Do not support scalping or high-frequency trading. The current feed, portal session, browser fallback,
60-second reconciliation cadence, and application-level scheduling are not designed for deterministic
sub-second execution. Do not support automated shorting or leverage in v1.

### 3.3 Why Value and Dividend is later

The current public implementation is strongest in price, volume, technical structure, portfolio, and
order-state data. A credible value/dividend strategy needs point-in-time fundamentals and corporate
actions with publication timestamps. Using today's restated financials in an old backtest creates
look-ahead bias. It also needs dividend ex-date, entitlement, tax/fees, sector-specific ratios, and
delisting/suspension history. Until those inputs are complete, the product may show a research score
but must not auto-enter from it.

Revised 2026-08-19: the AHL integration supplies more of this than existed before, and the remaining
gaps are now specific rather than general. What arrived: 41 ratios over TTM plus eighteen fiscal years,
each with a sector min/median/max so a symbol can be ranked without fetching peers; full payout history
with ex-dates and book-closure windows; fiscal year end, which is required to read a quarterly result
at all. What is still missing, and why it still blocks auto-entry:

- The statements are not point-in-time. They carry a `created` timestamp reflecting when the vendor
  last revised the row, not when the figure was first published, so a backtest reading them inherits
  exactly the look-ahead bias this section warns about. Restatements are invisible.
- Only UNCONSOLIDATED statements are served. The `consolidated` query parameter is accepted and
  ignored. For a holding company the gap is not a rounding difference: LUCK's FY26 profit after tax is
  46.6bn unconsolidated against 89bn consolidated. A value screen run on these figures would
  systematically misprice every group in the universe, and would do so in the direction that makes
  them look expensive.
- Analyst target prices and expected EPS are visible in the portal UI but return 403 for this account,
  so no consensus estimate is available.
- Delisting and suspension history is still absent, so the universe remains subject to survivorship
  bias.

The conclusion is unchanged — Value and Dividend stays in the last phase — but the reason is now
narrower: the data is good enough for a displayed research score and for ranking today, and not good
enough for a point-in-time backtest or for unattended entry.

## 4. AI decision design

### 4.1 Principle

AI judges a deterministic candidate; it does not create prices, quantities, indicators, market
status, or broker instructions. The local deterministic system remains authoritative for:

- market and account eligibility;
- symbol allowlist and tradability;
- indicator calculation and strategy predicates;
- entry, stop, target, expiry, and quantity;
- portfolio/risk limits;
- approval and entitlement;
- order construction and broker submission.

The AI layer may:

- classify market regime;
- compare multi-timeframe evidence;
- summarize current filed fundamentals or news;
- identify contradictions and missing evidence;
- assign a bounded quality score;
- return PROCEED, CAUTION, VETO, or ABSTAIN;
- explain the decision using evidence identifiers.

A model failure, timeout, schema failure, stale response, unsupported claim, or missing required
evidence must become ABSTAIN. It must never fall back to PROCEED.

### 4.2 Proposed contracts

These are illustrative contracts for the public seam; names can change during implementation.

    public sealed record StrategyCandidate(
        string CandidateId,
        string StrategyId,
        string StrategyVersion,
        HoldingMode HoldingMode,
        string Symbol,
        DateTimeOffset EvidenceAsOf,
        decimal ReferencePrice,
        decimal ProposedEntry,
        decimal ProposedStop,
        decimal ProposedTarget,
        IReadOnlyDictionary<string, decimal?> Features,
        IReadOnlyList<EvidenceReference> Evidence);

    public sealed record AiTradeAssessment(
        string Decision,
        int Score,
        IReadOnlyList<string> SupportingEvidenceIds,
        IReadOnlyList<string> RiskEvidenceIds,
        string Rationale,
        string? ModelId,
        string PromptVersion,
        DateTimeOffset AssessedAt,
        TimeSpan ValidFor);

    public sealed record TradeIntent(
        string IntentId,
        StrategyCandidate Candidate,
        AiTradeAssessment Assessment,
        int Quantity,
        string ExitPolicyId,
        DateTimeOffset ExpiresAt,
        string EvidenceHash,
        string? Signature);

The parser must validate enums, score range, evidence IDs, freshness, prompt version, and model ID.
The model may choose an invalidation level only from supplied level IDs, matching the existing
StockAssessmentService approach.

### 4.3 Mode-specific AI policy

- Trend: AI is optional in Paper and Shadow. For live entry it should be a veto/abstention layer,
  not a source of positive expectancy. A deterministic candidate must already exist.
- Market Structure: AI checks coherence across daily/weekly/intraday structure and highlights
  conflict. Stops and targets still come from identified levels.
- Breakout Momentum: AI may validate whether current news/catalyst evidence contradicts the move.
  It must not delay an order beyond the candidate TTL; a late answer expires.
- Value/Dividend: AI is most useful for document synthesis, but each claim must retain a filing or
  exchange evidence ID. Numeric ratios are computed in code from point-in-time fields.

Use one primary model call, not a conversational multi-agent debate, in the live decision path.
Additional model reviews add latency and correlated failure without replacing backtesting. A second
model can be used offline for evaluation, red-teaming, and disagreement analysis.

### 4.4 Model governance

Persist for every assessment:

- provider and exact model ID;
- prompt/template version;
- schema version;
- temperature and relevant inference settings;
- feature and evidence hash;
- raw response in protected diagnostic storage with retention controls;
- parsed decision;
- latency, token/cost metrics, and failure category;
- whether the result affected an entry, exit, or only an explanation.

Maintain a versioned golden evaluation set for each strategy. A model or prompt change cannot enter
Shadow until it passes schema reliability, grounding, abstention, adversarial-news, stale-data, and
counterfactual tests. It cannot enter live approval/auto modes until its Shadow results have been
reviewed over an agreed sample and market regimes.

## 5. Runtime architecture

### 5.1 Required flow

    Market data and broker state
        -> deterministic feature snapshot
        -> one or more strategy candidate generators
        -> candidate deduplication and freshness check
        -> AI assessment (optional by profile, always bounded)
        -> deterministic intent builder and risk-at-stop sizing
        -> portfolio-aware pre-trade risk rules
        -> Paper / Shadow / ApprovalRequired / BoundedAuto authorization
        -> TradingManager
        -> existing risk re-check + order window + idempotency + ledger
        -> existing IBrokerAdapter
        -> reconciliation and managed exit state

No strategy or AI component receives IBrokerAdapter, AhkBroker, AhkPortalClient, PlaceOrderTool, or
browser access through dependency injection.

### 5.2 Public extension seams to add

Add narrow public interfaces in the public core:

- ITradingMarketSnapshotProvider: candles, live quote, market state, and data freshness.
- ITradingPortfolioSnapshotProvider: cash, holdings, outstanding orders, fills, and reconciliation
  health without exposing broker credentials.
- ITradingStrategy: scans a snapshot and returns zero or more deterministic StrategyCandidate values.
- IAiTradeAssessmentService: turns a candidate/evidence bundle into a typed assessment.
- ITradeIntentBuilder: validates assessment, computes risk-at-stop quantity, and materializes an
  immutable intent.
- IPreTradeRiskRule: asynchronous, composable rule evaluated with order, portfolio, session, and
  strategy context.
- ITradingExecutionGateway: a narrow wrapper around TradingManager. It accepts a fully formed
  ExecutionRequest and exposes no broker.
- ITradingExitPolicy: deterministic stop, target, trailing, time, and end-of-day behavior.
- IAutoTradingEntitlement: checks feature and mode claims.
- ITradingDecisionRepository: persists candidate, assessment, intent, decision state, and links to
  the existing execution ID.

Avoid a service-locator or stringly typed capability registry. Versioned interfaces make drift
visible at compile time.

Revised 2026-08-19: two of these seams now have concrete implementations to extract against rather
than design in the abstract. `ITradingMarketSnapshotProvider` maps onto
`AhlAnalyticsClient.GetMarketSnapshotAsync` plus `CandleHistoryProvider`, and its freshness contract
should carry the source basis (adjusted or raw) alongside the timestamp — freshness alone is not
enough to make a series safe to use, as 2.2 now records. Phase 0 should validate the seam against this
real code, which is more informative than the no-op strategy proposed in section 13.

### 5.3 TradingManager request evolution

Replace the expanding positional arguments with a typed request:

    public sealed record TradingExecutionRequest(
        IReadOnlyList<IReadOnlyList<TradingSignal>> Groups,
        string Source,
        string CorrelationId,
        string? StrategyId,
        string? StrategyVersion,
        string? DecisionId,
        string? EvidenceHash,
        string? ModelId,
        ExecutionAuthorization? Authorization);

TradingManager must continue to own final validation and broker access. It should persist provenance
before submission and return the existing execution ID. Manual tools can populate Source = manual and
leave strategy fields null, preserving compatibility.

### 5.4 Risk engine evolution

Change the risk seam to asynchronous composite evaluation:

    Task<RiskValidationResult> ValidateAsync(
        PreTradeRiskContext context,
        CancellationToken cancellationToken);

Core rules, available to community and premium editions:

1. Existing static order validation and kill switch.
2. Reconciliation freshness and complete account snapshot.
3. Sufficient settled/available cash and sellable holdings.
4. Maximum position value and percentage per symbol.
5. Maximum sector and total invested exposure.
6. Maximum open positions and outstanding orders.
7. Maximum orders, turnover, and gross buy value per session.
8. Daily realized plus conservative unrealized loss circuit breaker.
9. Maximum strategy drawdown and consecutive-loss pause.
10. Quote/candle/evidence freshness AND provenance. Revised 2026-08-19: a rule must reject an intent
    whose levels were computed from corporate-action-adjusted bars but whose stop or limit will be
    compared against raw as-traded prices. Freshness and provenance are independent failures — a
    perfectly current adjusted series is still the wrong basis for a fill comparison — so
    `CandleHistory.Sources` has to be carried through the candidate and checked here, not assumed.
11. Limit-price distance, exchange price band, liquidity, and expected slippage.
12. Signal fingerprint cooldown and one-open-intent-per-symbol/strategy.
13. Intraday no-new-entry cutoff and mandatory flattening policy.
14. Entitlement may block new premium entries but must never block protective exits.
15. Revised 2026-08-19: broker session and login budget. A session-dependent read that would force a
    login must be refused once a configured hourly login budget is spent, and the refusal must fail
    closed for new entries. This is the rule that encodes the 2026-08-18 access block; without it the
    coordinator's own retry behaviour is the thing most likely to lose the account.
16. Revised 2026-08-19: upstream throttle state. The analytics portal answers 401 for rate-limiting
    rather than 429, while the token stays valid, so a naive client reads a throttle as an auth failure
    and re-authenticates — which on a cold broker session means a login per throttle.
    `AhlAnalyticsClient` already rate-limits itself and retries before re-handshaking; any new upstream
    added later needs the same treatment, and a sustained throttle should surface as an incident rather
    than be absorbed silently.

The persistent ledger, not an in-memory counter, must back daily limits so a restart cannot reset
them. If current portfolio, cash, loss, or outstanding-order state is unknown, new entries fail
closed. Risk-reducing exits should have a separately defined path that still enforces symbol,
quantity/holding, price-band, duplicate, and market-state safety.

### 5.5 Position sizing

Keep the existing budget sizer for manual compatibility. Add risk-at-stop sizing for automation:

    risk_budget = min(
        account_equity * risk_percent_per_trade,
        remaining_daily_risk_budget,
        remaining_strategy_risk_budget)

    per_share_risk = abs(entry - stop) + estimated_fees_and_slippage
    quantity = floor(risk_budget / per_share_risk)

Then cap quantity by available cash, per-symbol exposure, liquidity participation, order value, and
lot/tick rules. Reject if there is no valid stop, per-share risk is non-positive, or the resulting
quantity is zero. The model never performs this arithmetic.

### 5.6 Exit ownership

An automatic entry must not exist without a persisted exit plan. Store:

- initial stop and target;
- native broker stop order ID when available;
- trailing rule and highest favorable price;
- time stop and maximum holding date/time;
- intraday flatten cutoff;
- strategy invalidation;
- quantity remaining after partial fills/exits;
- entitlement-independent protective status.

Reuse the existing protective-stop, take-profit retry, broker reconciliation, and order ledger paths.
Extend them with strategy metadata instead of introducing a new premium-only exit executor.

## 6. Persistence and API

### 6.1 New tables

Add migrations for:

- strategy_profiles: user configuration, version, enabled state, rollout mode.
- strategy_candidates: immutable features, evidence hash, reference prices, expiry.
- ai_assessments: model/prompt/schema identity, parsed result, protected raw response reference.
- trade_intents: candidate link, deterministic quantity/order plan, signature, state.
- managed_positions: execution/fill links, strategy, exit policy, remaining quantity.
- strategy_daily_metrics: candidates, intents, fills, P&L, drawdown, turnover, rejects.
- entitlement_events: feature decision and reason, excluding secret token material.
- automation_incidents: circuit breakers, stale data, model failures, broker/reconciliation failures.

Link trade_intents to the existing executions and broker order/fill records. Do not create a second
ledger for premium orders.

Revised 2026-08-19: `strategy_candidates` must record evidence PROVENANCE, not just an evidence hash —
per-symbol candle source and adjustment basis, quote source, and whether depth was available. Without
it a decision cannot be replayed faithfully, because the same symbol can be served adjusted today and
raw tomorrow depending on whether an analytics token was held, and the resulting levels differ by the
corporate-action factor rather than by a rounding step.

### 6.2 State machine

    observed
      -> candidate
      -> ai_pending
      -> vetoed | abstained | approved
      -> risk_rejected | awaiting_approval | intent_ready
      -> executing
      -> accepted | partial | failed | unknown
      -> managed_position
      -> exiting
      -> closed | manual_intervention

Every transition is compare-and-set and auditable. A restart resumes from durable state. Unknown
broker outcomes never auto-retry; reconciliation or an operator resolves them, matching the current
TradingManager behavior.

### 6.3 Premium management API

Add endpoints under /trading/automation and use existing management authorization policies:

- GET /status
- GET and PUT /profiles
- POST /profiles/{id}/enable and /disable
- GET /candidates
- GET /decisions
- POST /run-once for Paper/Shadow diagnostics
- GET /positions
- POST /pause and /resume
- POST /flatten-preview and /flatten with TradingTrader authorization
- GET /performance with explicit simulated/live separation
- GET /incidents

Enabling BoundedAuto, changing risk caps, or flattening positions must require the stronger trading
role, write an audit event, and show the effective diff. The UI must display data age,
reconciliation health, entitlement state, current mode, last model decision, and the reason the most
recent candidate did or did not progress.

Never label simulated returns as live performance.

## 7. Keeping premium code private

### 7.1 Why a second private plugin cannot simply reference this plugin side by side

The host currently creates a separate PluginLoadContext for every DLL that has its own .deps.json.
Only AgentFox.Plugins, selected Microsoft.Extensions assemblies, Newtonsoft.Json, and Polly are
shared with the default context. .NET treats the same named type loaded in two AssemblyLoadContext
instances as different types.

Therefore, a separately loaded PremiumAutoTrading plugin must not directly exchange TradingManager,
TradingSignal, or another TradingAgent-defined interface with the community TradingAgent plugin.
The types can have identical names and still fail assignment/casting. This follows both the local
[PluginLoadContext.cs](../../Agent/Modules/Loaders/PluginLoadContext.cs) implementation and the
official .NET AssemblyLoadContext type-identity rules.

Do not solve this with reflection over private fields, a global static service locator, or duplicate
HTTP calls to management endpoints.

### 7.2 Recommended edition-composition structure

Public repository:

    src/Plugins/TradingAgent.Abstractions/
      stable strategy, intent, evidence, execution-gateway contracts

    src/Plugins/TradingAgent.Core/
      broker, market data, analysis, risk, manager, persistence
      TradingAgentRuntime.AddCore(...)
      TradingAgentRuntime.MapCoreEndpoints(...)
      TradingAgentRuntime.RegisterSpecialist(...)

    src/Plugins/TradingAgent/
      community entry module and public UI
      references Abstractions + Core

Private repository:

    src/TradingAgent.Premium.Strategies/
      proprietary strategy implementations
      proprietary prompts/evaluators if run locally

    src/TradingAgent.Premium.ServiceClient/
      optional entitlement and hosted-decision client
      signature verification and circuit breaker

    src/TradingAgent.Premium.Plugin/
      the only deployed entry module
      calls TradingAgentRuntime for all public core behavior
      adds premium strategies, coordinator, endpoints, and UI

    tests/
      contract, backtest, shadow, failure-injection, and packaging tests

The premium repository consumes versioned public NuGet packages. It produces a single publish
bundle whose entry assembly has one .deps.json and whose dependencies include the public core
assemblies. It is loaded into one PluginLoadContext, so public contracts and private implementations
share type identity.

The community and premium entry plugins are mutually exclusive deployment artifacts. Add a startup
guard that fails fast if both entry modules are discovered.

### 7.3 Repository and package rules

- Use a private organization repository with branch protection, required review, CODEOWNERS, secret
  scanning, dependency review, and restricted Actions permissions.
- Publish premium NuGet packages/artifacts from the private repository. Keep tokens in Actions/OIDC
  secret storage, never in NuGet.config or this public repository.
- The public repository should not have read access to the private package. GitHub warns that forks
  of a public repository may be able to access private packages when that access is granted.
- Pin public core versions in the private repository. Test against the next core version before
  upgrading; do not consume an unbounded floating version.
- Sign premium assemblies and release manifests. Verify hashes/signatures during installation.
- Keep license/entitlement secrets out of appsettings and SQLite. Store them through the existing
  protected plugin-secret mechanism or environment secret provider.
- Never commit premium source, generated source maps, symbols containing proprietary source paths,
  raw prompts, private backtest datasets, or private package credentials to this repository.

Do not use a private git submodule from this public repository. It complicates public forks and CI
and invites accidental access grants. If a submodule is ever used, the safe direction is a public
core submodule inside the private repository, but versioned packages are easier to release and
reproduce.

### 7.4 Binary versus service trade-off

| Option | Source private | Strategy hard to extract | Offline/latency | Operations | Recommendation |
| --- | --- | --- | --- | --- | --- |
| Private repository + shipped DLL | Yes | No; .NET is decompilable | Best | Simple | Good for source separation, not strong IP secrecy |
| Obfuscated/signed DLL | Yes | Raises effort only | Best | Moderate | Defense in depth, not a secrecy boundary |
| Private hosted decision service + thin local plugin | Yes | Yes | Network dependent | Highest | Use for genuinely proprietary strategy/model logic |
| Private fork of whole public repo | Yes | Yes in GitHub, but high drift | Best | High maintenance | Avoid |
| Private submodule pulled by public build | Fragile | Varies | Best | Credential/fork risk | Avoid |

Recommended hybrid:

- deterministic safety, broker execution, and explainable baseline features remain local and public;
- entitlement, proprietary ranking, and premium model orchestration may run in the private service;
- the service returns a short-lived signed intent containing account/tenant, strategy/version,
  symbol, side, entry bounds, stop, target, evidence hash, expiry, and nonce;
- the local plugin verifies signature, entitlement, account binding, freshness, nonce, and evidence
  hash, then independently rebuilds quantity and re-runs every local risk rule;
- service unavailability stops new entries but never disables locally managed protective exits.

Revised 2026-08-19: state the latency cost of this choice rather than leaving it implicit. A hosted
decision service puts a network round trip inside a decision path that section 4.3 already says
expires on a late answer, so ordinary network variance will drop entries. That is the correct
failure direction, but it must be budgeted and MEASURED — publish a decision-latency and
expired-candidate rate per strategy — or the service will be blamed for poor strategy performance that
is actually missed fills. If the measured expiry rate is material, the honest options are a longer
candidate TTL or moving the ranking back in-process, not silently widening the window.

## 8. Premium entitlement behavior

An entitlement claim should identify tenant/user, account binding, allowed strategies, maximum
automation mode, issue/expiry, and key ID. It must not contain broker credentials.

Fail-closed behavior:

- no entitlement or invalid signature: premium UI is read-only and no new premium candidate starts;
- expired entitlement: no new entries; existing positions keep all protective exits;
- entitlement service unavailable: use a short configured grace period only for already validated
  claims, then block new entries;
- downgrade from BoundedAuto: immediately fall back to ApprovalRequired or Shadow, never silently
  remain live;
- revocation: pause new entries and alert the user; do not liquidate automatically merely because a
  subscription ended.

Entitlement checks occur both when the coordinator starts a decision and when the execution gateway
accepts an intent. This closes the time-of-check/time-of-use gap.

## 9. Configuration sketch

Defaults must be disabled and conservative:

    Plugins:
      TradingAgent:
        AutoExecute: false
        ExecutionMode: Disabled

      PremiumAutoTrading:
        Enabled: false
        DefaultHoldingMode: Delivery
        NewEntriesDuringPreOpen: false
        RequireBrokerAlgoApproval: true
        RequireEntitlement: true
        MaxDataAgeSeconds: 30
        Risk:
          RiskPercentPerTrade: 0.25
          MaxDailyLossPercent: 1.0
          MaxTotalInvestedPercent: 50
          MaxSymbolPercent: 10
          MaxSectorPercent: 25
          MaxOpenPositions: 5
          MaxOrdersPerSession: 10
        Ai:
          RequiredForLiveEntry: true
          FailurePolicy: Abstain
          DecisionTtlSeconds: 60
        Profiles: []

These values are examples for configuration shape, not recommended investment limits. Product and
compliance owners must approve actual defaults after broker constraints and backtests are known.

## 10. Regulatory, broker, and market gates

This is an engineering plan, not a legal opinion.

Primary-source findings as of 2026-08-19:

- SECP's May 30, 2025 concept paper proposed registration/testing, unique identifiers, broker
  controls, audit/governance, kill switches, segregated testing, continuous AI/ML validation, and a
  phased start limited to institutional investors.
- The official SECP press release says expansion to retail investors was contemplated only later,
  subject to market readiness, risk assessment, and experience.
- The current PSX legal-framework page exposes a rule book updated February 9, 2026. The research
  performed for this plan did not locate a later official retail-algorithm approval. Absence from
  this search is not proof that no broker-specific approval exists.
- PSX/NCCPL moved eligible trades to T+1 effective February 9, 2026.
- PSX states that orders entered in pre-open cannot be cancelled, modified, or suspended until the
  matching break completes.
- PSX's investor guidance makes customers responsible for clear order instructions and online
  access-code security and distinguishes Ready Delivery from leveraged/short-sale products.

Required launch evidence:

1. Written broker confirmation that the account may use unattended automation and that the chosen
   API/session mechanism is supported.
2. Pakistani securities counsel/compliance sign-off on user type, account type, strategies,
   disclosures, logging, and whether an algorithm identifier/registration is required.
3. Documented data licensing rights for quotes, candles, fundamentals, news, and model use.
   Revised 2026-08-19: this gate is now concrete and should be treated as the most likely one to be
   overlooked. The AHL analytics integration reaches `data.arifhabibltd.com` through an SSO handshake
   whose intended use is a human clicking "AHL Analytics" in the terminal, and the client it produces
   is capable of systematic, unattended consumption — a whole-market snapshot plus five years of
   candles per symbol. Nothing about the technical access implies a right to use it that way. Written
   confirmation from AHL that programmatic and automated use of the research portal is permitted, and
   on what terms, belongs in the same package as the algorithmic-trading approval. Until then the
   integration should stay in its current position: operator- and agent-initiated reads, off by
   default, with no scheduled polling.
4. Broker sandbox/test environment or an agreed non-live conformance process.
5. Incident, change-management, audit, retention, and kill-switch procedures.
6. A user agreement that clearly distinguishes research, automation, execution risk, fees,
   slippage, connectivity risk, and the user's responsibility for credentials/account permissions.

Until all live gates pass, expose only Paper, Shadow, and optionally human-approved proposal flow.

## 11. Delivery phases

### Phase 0: architectural extraction

- Add TradingAgent.Abstractions and TradingAgent.Core projects.
- Move composition behind TradingAgentRuntime methods without changing behavior.
- Keep the current community plugin as a thin entry module.
- Introduce ITradingExecutionGateway and typed TradingExecutionRequest.
- Add a packaging test proving one entry .deps.json and no duplicate plugin discovery.
- Add a startup test proving community and premium entries cannot run together.
- Preserve all current safety and UI tests.

Exit: community plugin behavior is unchanged; public core can be consumed by a private composite
entry plugin without cross-AssemblyLoadContext type duplication.

### Phase 1: durable paper engine

- Add strategy profile, candidate, assessment, intent, position, and incident persistence.
- Add event-driven/scheduled AutoTradingCoordinator with a single-flight lock.
- Implement Trend and Market Structure candidates using existing daily/weekly analysis.
- Add risk-at-stop sizing and the portfolio-aware risk rules.
- Build a simulated broker/portfolio with configurable fees, slippage, partial fills, and rejected
  orders.
- Add deterministic replay from stored point-in-time evidence.

Exit: restart-safe Paper results with no broker call and reproducible decision records.

### Phase 2: AI assessment and Shadow

- Generalize the existing StockAssessmentService pattern into IAiTradeAssessmentService.
- Add strict schema, evidence IDs, prompt/model versioning, TTL, and ABSTAIN failure behavior.
- Add golden-set and adversarial evaluation tests.
- Run candidates against live data in Shadow, recording would-be orders and later outcomes.
- Add premium read-only dashboard panels and incident alerts.

Exit: model and deterministic versions can be compared offline; no live submission exists.

### Phase 3: human-approved live pilot

- Add signed entitlement checks.
- Add ApprovalRequired flow for generated intents.
- Revalidate price, evidence, entitlement, portfolio, and policy after human approval and immediately
  before TradingManager.
- Limit pilot to Ready Delivery, liquid allowlisted symbols, Trend/Market Structure, limit orders,
  regular open session, and very low configured caps.
- Require native/persisted protective exits and operational on-call coverage.

Exit: a small approved cohort can place human-confirmed orders with complete provenance and
reconciliation.

### Phase 4: bounded auto pilot

- Enable BoundedAuto only for accounts with broker/legal approval.
- Add durable daily loss, drawdown, turnover, concentration, and stale-data circuit breakers.
- Add remote pause/revocation and local emergency controls.
- Require a minimum Shadow history and human-approved-live history per strategy/model version.
- Roll out by entitlement allowlist, with automatic rollback to ApprovalRequired on incident
  thresholds.

Exit: unattended entries are possible only within hard local bounds.

### Phase 5: Breakout Momentum and Intraday

- Validate multi-session intraday archive quality and clock alignment.
- Add transaction-cost, slippage, liquidity-participation, and failed-break modeling.
- Add entry cutoff, end-of-day flattening, and auction/pre-open exclusions.
- Load-test feed, reconciliation, and order serialization.

Exit: intraday remains disabled if the system cannot prove it can flatten and reconcile within the
defined operational envelope.

### Phase 6: Value and Dividend

- Acquire and license point-in-time fundamentals and corporate actions.
- Implement deterministic factor calculation and sector normalization.
- Add rebalance cadence, turnover budget, dividend entitlement/ex-date handling, and delisting
  history.
- Use AI only for cited filing synthesis and anomaly/thesis review.

Exit: backtests are point-in-time and include dividends, taxes/fees, liquidity, and survivorship.

## 12. Verification and launch gates

### 12.1 Unit and contract tests

- Strategy output is identical for identical snapshots and versions.
- No strategy/AI service can resolve IBrokerAdapter or AhkBroker.
- AI schema rejects unknown decisions, invented evidence IDs, stale TTL, and unsupported levels.
- Model failure always abstains.
- Risk-at-stop sizing obeys all smaller caps and never rounds upward.
- Entitlement loss blocks entries but not protective exits.
- Every risk rule fails closed on missing required state.
- Execution provenance survives serialization and links to the ledger.
- Public/private package versions fail fast on incompatibility.

### 12.2 Backtest integrity

- Use point-in-time universes including delisted/suspended symbols.
- Prevent look-ahead in candles, fundamentals, corporate actions, news, and index membership.
- Revised 2026-08-19: keep the price basis consistent and stated end to end. Signals may be generated
  on adjusted bars, but fills, fees, price bands, tick sizes and P&L must be evaluated on the raw
  as-traded prices for that date, with the adjustment factor applied explicitly at the boundary. A
  backtest that computes a stop on adjusted history and then assumes it was fillable at that number
  will report profits on trades that could not have existed. Test this directly with a symbol that had
  a corporate action inside the window — LUCK across its 5:1 is a ready-made case, and any engine that
  cannot reproduce the 0.195 price and 5.0 volume relationship across that date is silently mixing
  bases.
- Revised 2026-08-19: the AHL statement data is not point-in-time (see 3.3), so it must not be used as
  the fundamentals input to a backtest at all. Either license a point-in-time source or restrict
  fundamental strategies to forward-testing.
- Model fees, levies, spread, slippage, tick size, price bands, volume participation, partial fills,
  rejected orders, and T+1 settlement constraints.
- Separate training/tuning, validation, and untouched out-of-sample periods.
- Walk forward through bullish, bearish, volatile, illiquid, and halted regimes.
- Report drawdown, turnover, exposure, hit rate, payoff ratio, expected shortfall, rejected-order
  rate, stale-data rate, and results after all costs. Do not select solely on headline return.

### 12.3 Failure injection

- quote/candle feed stale or contradictory;
- model timeout, malformed JSON, prompt injection in news, or provider outage;
- broker login expiry, partial endpoint failure, portal HTML/API change;
- unknown order outcome and delayed fill;
- database locked/full/corrupt;
- process restart after candidate, approval, submission, and partial fill;
- clock skew and PSX schedule override;
- entitlement expiry/revocation and signature-key rotation;
- duplicate scheduler execution and multi-click approval replay;
- network loss immediately before and after broker submission;
- Revised 2026-08-19: upstream throttle presenting as an auth failure (the analytics portal's
  401-not-429 behaviour), asserting that the client backs off and does NOT re-handshake into a broker
  login;
- Revised 2026-08-19: analytics token revoked or expired mid-session, asserting that candle reads fall
  back to PSX and that the source change is reported rather than silent;
- Revised 2026-08-19: the same symbol served adjusted on one pass and raw on the next, asserting that a
  candidate carrying one basis cannot be executed against the other;
- Revised 2026-08-19: login budget exhausted, asserting that new entries fail closed while protective
  exits continue.

### 12.4 Operational go/no-go

No strategy may progress to the next mode unless:

- regulatory/broker gates for that mode are documented;
- all automated tests pass;
- Paper accounting reconciles;
- Shadow has the approved minimum observations and days;
- model/prompt/strategy versions are frozen and signed;
- risk/incident dashboards and alerts are live;
- a tested kill switch and manual reconciliation runbook exist;
- rollback returns accounts to ApprovalRequired or Shadow without disabling exits;
- an owner signs the release record.

## 13. Immediate next implementation slice

The smallest useful change is architectural, not a strategy:

1. Add TradingAgent.Abstractions with StrategyCandidate, TradeIntent, evidence, strategy, AI
   assessment, execution gateway, and entitlement contracts.
2. Add a typed TradingExecutionRequest and adapt current manual/proposal callers.
3. Add TradingAgentRuntime composition methods and leave TradingAgentModule as a thin wrapper.
4. Add a private-repository sample premium entry module in the private repository, not here.
5. Add loader/packaging tests that prove only the premium entry assembly is discovered.
6. Implement a no-op/paper strategy in the private module to prove the full candidate-to-ledger path
   without touching IBrokerAdapter.

Revised 2026-08-19: two amendments to this slice, both cheap and both preventing rework.

- Add the source/adjustment basis to the evidence contract in step 1, not later. It is a field on
  `StrategyCandidate` and a check in the risk seam; retrofitting it after candidates are persisted
  means a migration plus a backfill of rows whose basis is no longer knowable.
- Use the AHL analytics client as the first real implementation behind
  `ITradingMarketSnapshotProvider` in step 6, instead of a purely no-op strategy. A no-op proves the
  wiring but exercises none of the interesting constraints — freshness, provenance, an upstream that
  throttles, a source that can become unavailable mid-run. A read-only candidate generator over the
  existing snapshot exercises all four and still cannot place an order, because it never receives a
  broker.

Only after that seam is stable should Trend and Market Structure strategy code begin.

## 14. Sources

Repository evidence:

- [TradingAgent project](TradingAgent.csproj)
- [TradingAgent composition root](TradingAgentModule.cs)
- [Deterministic execution boundary](Manager/TradingManager.cs)
- [Current pre-trade risk engine](Risk/TradingRiskEngine.cs)
- [Approval policy](Manager/ApprovalGate.cs)
- [AI assessment pattern](Research/StockAssessmentService.cs)
- [Technical evidence model](Analysis/TechnicalSnapshot.cs)
- [Plugin load context](../../Agent/Modules/Loaders/PluginLoadContext.cs)

External primary sources:

- [SECP: Concept Paper—Regulating Algorithmic Trading in Pakistan's Capital Market](https://www.secp.gov.pk/document/concept-paper-regulating-algorithmic-trading-in-pakistans-capital-market/?wpdmdl=57444)
- [SECP press release: proposed algorithmic-trading framework](https://www.secp.gov.pk/wp-content/uploads/2025/05/Press-Release-SECP-Proposes-Regulatory-Framework-for-Algorithmic-Trading-in-Pakistan-002.pdf)
- [PSX legal framework and current rule book](https://www.psx.com.pk/psx/regulations/legal-framework)
- [PSX trading hours and pre-open restriction](https://www.psx.com.pk/psx/exchange/general/trading-hours)
- [PSX/NCCPL: T+1 transition effective February 9, 2026](https://www.nccpl.com.pk/press-releases/pakistan-capital-market-successfully-transitions-to-the-t1-settlement-cycle)
- [PSX Investor Awareness Guide](https://www.psx.com.pk/psx/resources-and-tools/investors/investor-awareness-guide)
- [Microsoft: AssemblyLoadContext and type identity](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
- [GitHub: package access control and public-fork warning](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility)
- [GitHub: NuGet registry authentication and private visibility](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry)
- [NIST AI Risk Management Framework](https://www.nist.gov/itl/ai-risk-management-framework)
- [NIST Generative AI Profile](https://nvlpubs.nist.gov/nistpubs/ai/NIST.AI.600-1.pdf)
- [NBER: Momentum Strategies](https://www.nber.org/papers/w5375)
- [Moskowitz, Ooi, and Pedersen: Time Series Momentum](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2089463)
- [Kenneth French Data Library: value and dividend-yield portfolio definitions](https://mba.tuck.dartmouth.edu/pages/faculty/ken.french/data_Library.html)

Research papers motivate which families deserve evaluation; they do not establish that a strategy
will work on PSX, after local costs, or in the future. Only point-in-time PSX testing and controlled
rollout can answer that.

## 15. Revision log

### 2026-08-19 — after the AHL Analytics integration

The architecture was not changed. The judgements that survived review unaltered are: one deterministic
execution path through TradingManager, AI as a veto/abstain layer rather than a source of expectancy,
deterministic risk-at-stop sizing, no strategy receiving a broker, exits persisted before entry, and
live retail automation gated behind written broker and legal approval. No strategy was promoted to an
earlier phase.

What changed, and why:

| Section | Change | Cause |
| --- | --- | --- |
| 2.1 | Records the analytics portal as an existing capability | It landed |
| 2.2 | Adds three hazards: mixed price basis, no depth outside the broker feed, broker login rate | Discovered during the integration |
| 3.3 | Replaces a general "fundamentals are missing" argument with the four specific gaps that remain | The data now partly exists |
| 5.2 | Points Phase 0 at real code for two seams | Implementations now exist |
| 5.4 | Adds provenance to rule 10; adds login-budget and throttle rules | The 2026-08-18 access block, and the 401-not-429 behaviour |
| 6.1 | Requires provenance on `strategy_candidates` | Replay is otherwise not faithful |
| 7.4 | Requires the hosted-service latency cost to be measured and published | It was implicit |
| 10 | Makes the data-licensing gate specific to the analytics portal | The client built this session can consume it systematically |
| 12.2 | Requires a consistent, stated price basis and names a concrete test case | Adjusted/raw is a distinct bug class from look-ahead |
| 12.3 | Adds four failure-injection cases | Each corresponds to an observed upstream behaviour |
| 13 | Moves the provenance field into the first slice; replaces the no-op strategy with a read-only one | Cheaper now than as a migration |

The one item a reader should not skim is the licensing gate in section 10. Every other change makes the
system more careful about data it already has. That one asks whether the system is entitled to use the
data at all, and technical access does not answer it.
