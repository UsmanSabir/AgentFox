# Trading Agent and Trading Manager Plan

## Status

- Document type: architecture and implementation plan
- Scope: AgentFox gateway, dedicated trading agent, deterministic trading manager, persistence, broker execution, and operational safety
- Recommended rollout: incremental, beginning with safety fixes and paper trading
- Live autonomous trading status: not approved by this plan until all production gates are satisfied

## Executive recommendation

Convert the current TradingAgent plugin into two cooperating components:

1. A dedicated Trading Agent that understands trading conversations, parses signals, answers portfolio questions, and produces structured trade proposals.
2. A deterministic Trading Manager that owns operational state, validates risk, controls approvals, submits orders, reconciles broker state, and records an immutable audit trail.

The main AgentFox agent should route trading-related work to the dedicated Trading Agent through a single delegation boundary. It should not receive direct access to place_order or place_orders. The Trading Agent should also not submit arbitrary orders directly to the browser broker. It should submit a typed TradeIntent to the Trading Manager, which must independently validate every execution rule.

For persistence:

- Use SQLite in WAL mode for the first single-process production version.
- Move to PostgreSQL if the gateway, manager, workers, or broker adapters become separate processes or require high availability.
- Use DuckDB for analytics, backtesting, market-history queries, and reporting rather than as the primary order and position ledger.

Do not enable unrestricted autonomous trading across all stocks. Begin with configured instruments, versioned strategies, small exposure limits, paper trading, and human approval.

## Goals

- Isolate trading instructions and tools from the general-purpose main agent.
- Route trading questions and signals to a persistent specialist agent.
- Make all order execution deterministic, idempotent, auditable, and fail-closed.
- Maintain a durable view of signals, strategies, approvals, orders, fills, positions, cash, and risk.
- Reconcile local state with the broker before and after execution.
- Support paper, approval-required, and bounded-auto execution modes.
- Preserve an emergency stop that works independently of the LLM.
- Support analytical workloads without weakening the operational ledger.

## Non-goals

- High-frequency or latency-sensitive trading.
- Allowing free-form LLM output to become a broker order without validation.
- Trading every listed instrument by default.
- Treating browser automation as authoritative proof of a fill.
- Automatically changing a strategy's economic intent, such as silently lowering a take-profit target to a daily price cap.
- Replacing broker, exchange, legal, or regulatory controls.

## Current implementation summary

The existing plugin is an IAgentAwareModule. At startup it:

- Registers AhkBroker, DuplicateSignalFilter, PendingTakeProfitStore, and TakeProfitRetryWorker.
- Registers parse_signal, check_market, place_order, place_orders, and log_signal in the main agent's global tool registry.
- Injects a PSX trading workflow into the main agent's system prompt.
- Registers a WhatsApp bridge channel provider.
- Receives bridge messages through the generic webhook module and routes them through the normal main-agent command lane.
- Uses the main IChatClient to parse incoming text.
- Uses PuppeteerSharp to interact with the AHK web portal.
- Writes signals to daily JSONL files.
- Persists pending take-profit retries in a JSON file.

This means the plugin currently adds capabilities to the main agent; it does not register a separate trading agent.

## Immediate risks to fix

These items are release blockers for unattended live execution.

### 1. Protect every execution tool with HITL

The normal workflow calls place_orders, while the documented HITL configuration watches only place_order. Tool approval uses exact names.

Required changes:

- Add both place_order and place_orders to Hitl.RequireApprovalForTools while the legacy tools remain available.
- Add both tools to Plan.MutatingTools when plan enforcement is enabled.
- Prefer replacing both public tools with one manager boundary such as submit_trade_intent.
- Add a startup validation that refuses AutoExecute when any execution tool lacks the required policy.

Acceptance criteria:

- No single or batch live order can execute without the configured authorization mode.
- An automated test proves that place_orders is blocked when approval is required.

### 2. Enforce the market calendar in deterministic code

The current clock treats all weekdays as open from 09:15 to 15:30. It does not model the current Monday-to-Thursday opening time, split Friday sessions, holidays, Ramadan schedules, or emergency suspensions. Order tools rely on an LLM instruction to call check_market rather than enforcing the result.

Required changes:

- Replace the simple clock with IMarketCalendar.
- Represent regular sessions, split sessions, holidays, special schedules, and suspensions.
- Refresh the calendar from an operator-maintained or authoritative source.
- Require TradingManager to verify that the selected market and order type are currently eligible.
- Fail closed when the calendar is unavailable or stale.
- Keep check_market as a read tool, but do not treat it as an execution control.

Acceptance criteria:

- Monday-to-Thursday and Friday sessions are tested independently.
- Holiday and special-session tests are present.
- Direct invocation of an execution API outside an eligible session is rejected.

### 3. Authenticate and de-duplicate inbound signals

The WhatsApp bridge currently accepts arbitrary JSON, ignores headers, and relies on a caller-supplied group name.

Required changes:

- Add HMAC signature verification with a secret stored outside appsettings.json.
- Require a timestamp and reject stale requests.
- Require a stable source message identifier.
- Store and reject replayed source identifiers.
- Add sender and group allowlists.
- Add request size, rate, and concurrency limits.
- Record authentication failures without recording secrets.

Acceptance criteria:

- Invalid signatures, expired timestamps, replayed messages, and unauthorized senders are rejected before an agent turn is created.

### 4. Replace the in-memory duplicate filter with durable idempotency

The existing filter resets on restart, depends on an optional raw_message field, and marks a batch before broker submission has completed.

Required changes:

- Derive an idempotency key from source, source message ID, account, strategy version, and intended action.
- Store lifecycle states: received, parsed, rejected, proposed, awaiting_approval, approved, submitting, accepted, partially_filled, filled, cancelled, and failed.
- Ensure a retry resumes the existing operation rather than creating another order.
- Use a unique database constraint as the final duplicate barrier.
- Pass a stable client order ID to the broker when supported.

Acceptance criteria:

- Process restarts and repeated webhooks cannot create duplicate live orders.
- A failed submission can be safely retried.

### 5. Make runtime configuration authoritative

The plugin configuration UI can change prompt text while the tools continue reading static options.

Required changes:

- Introduce ITradingPolicyProvider as the only runtime policy source.
- Return an immutable policy snapshot with a version and timestamp.
- Use the same snapshot for prompts, proposal validation, execution, and audit.
- Reject stale or invalid policy versions.

Acceptance criteria:

- The policy shown to users and the policy enforced by the manager always have the same version.

### 6. Implement stop-loss and full exit lifecycle management

Stop-loss values are parsed and logged but are not executed or monitored.

Required changes:

- Persist stop-loss intent separately from broker order state.
- Prefer broker-native contingent orders when supported.
- If client-side monitoring is required, make it an explicit strategy with health checks, stale-price protection, and alerting.
- Reconcile quantities before creating any exit order.
- Prevent exit orders from exceeding the available position.

Acceptance criteria:

- Every managed position has an explicit exit-policy state: none, pending, active, triggered, completed, failed, or manually overridden.

## Target architecture

    Incoming channels and API
              |
              v
       Gateway intent router
          |             |
          v             v
      Main Agent    Trading Agent
                         |
                         v
                 Structured TradeIntent
                         |
                         v
                  Trading Manager
                  /      |       \
                 v       v        v
          Risk Engine  Approval  Idempotency
                  \      |       /
                         v
                   Broker Adapter
                         |
                         v
                 Broker Reconciliation
                         |
                         v
                  Operational Database
                         |
                         v
                  Analytics / DuckDB

### Component responsibilities

#### Gateway intent router

- Routes known trading channels directly to the Trading Agent.
- Routes general conversations to the main agent unless trading intent is confidently detected.
- Distinguishes read, proposal, execution, and administrative intents.
- Never authorizes a trade.
- Preserves the original authenticated caller, session, channel, and correlation identifiers.

Recommended intent classes:

- Trading.Read: portfolio, order status, configured instrument, signal, and market-calendar questions.
- Trading.Analyze: research, comparison, risk explanation, and what-if calculations.
- Trading.Propose: create a structured but non-executable trade proposal.
- Trading.Execute: request execution of an existing proposal.
- Trading.Admin: change watchlists, strategies, limits, credentials, or execution mode.
- NonTrading: remain with the main agent.

#### Trading Agent

- Has its own system prompt, identity, session namespace, and limited tool registry.
- Can query trading state and market data.
- Can parse untrusted signal text into a structured candidate.
- Can explain why a candidate passed or failed validation.
- Can create TradeProposal records.
- Cannot bypass the Trading Manager or broker policy.
- Does not inherit shell, filesystem, code execution, memory mutation, or unrestricted network tools.

#### Trading Manager

- Is a normal deterministic application service, not an LLM agent.
- Owns the transaction boundary and operational state.
- Validates account, instrument, side, quantity, price, order type, strategy, session, risk, approval, idempotency, and broker readiness.
- Generates a broker command only after all validation succeeds.
- Records every state transition.
- Reconciles accepted orders, fills, positions, and cash with the broker.
- Fails closed on stale data, ambiguous broker responses, or unavailable controls.

#### Risk engine

Minimum pre-trade controls:

- Allowed accounts and instruments.
- Allowed order types.
- Per-order notional maximum.
- Per-symbol position and concentration maximum.
- Portfolio gross and net exposure maximum.
- Available cash and holdings.
- Maximum open orders per symbol and account.
- Daily realized and unrealized loss limits.
- Drawdown and consecutive-loss limits.
- Price freshness and permitted price deviation.
- Market session, holiday, and suspension checks.
- Duplicate and self-conflicting order checks.
- Strategy-specific limits.
- Global and account-level kill switches.

Minimum post-trade controls:

- Accepted-order reconciliation.
- Fill and partial-fill reconciliation.
- Position and cash reconciliation.
- Orphaned exit-order detection.
- Unexpected broker-order detection.
- Stale pending-order alerts.
- Strategy and account P&L monitoring.

#### Broker adapter

- Prefer an official broker API if available and authorized.
- Keep Puppeteer automation behind IBrokerAdapter so it can be replaced.
- Treat a portal success message as order acceptance, not as a fill.
- Assign a correlation and idempotency identifier to every submission.
- Capture sanitized evidence for ambiguous responses.
- Never automatically retry a submit when acceptance is unknown.
- Query broker state to resolve uncertainty before another submission.

## Agent registration and routing design

The current IPluginContext cannot register an agent. Add a host-level specialist registry rather than using temporary generic subagents.

### Proposed contracts

Add to AgentFox.Plugins:

- IAgentRegistry
- IAgentDescriptor
- IAgentRoute
- IAgentInvocationContext
- IAgentResult

An AgentDescriptor should define:

- Stable ID and display name.
- Intent categories and routing hints.
- Allowed channel types.
- Dedicated model key.
- System prompt provider.
- Tool allowlist.
- Session namespace.
- Timeout and concurrency policy.
- Whether delegation is read-only, proposal-capable, or execution-capable.

Extend the plugin lifecycle with either:

- IPluginContext.RegisterAgent descriptor, or
- a separate IAgentPlugin.RegisterAgents method.

The separate IAgentPlugin interface is preferred because agent registration is a different lifecycle concern from adding tools to the main agent.

### Routing rules

1. A configured trading-signal channel routes directly to trading-agent without semantic classification.
2. Explicit commands such as portfolio status, analyze PSO, or propose a trade route to trading-agent.
3. Ambiguous educational questions may be delegated by the main agent through a single delegate_to_trading_agent tool.
4. Execution language never directly invokes a broker tool. It identifies or creates a proposal and calls the Trading Manager command API.
5. Administrative policy changes require stronger authorization than ordinary trade approval.

### Transition strategy

During migration:

- Keep the existing plugin module and channel provider.
- Add TradingManager and persistence behind the existing tools.
- Replace the existing tools with thin compatibility adapters.
- Add the dedicated Trading Agent and delegation tool.
- Stop contributing the full trading workflow to the main system prompt.
- Remove execution tools from the main global registry.
- Finally remove the compatibility adapters.

## Operational data design

### Primary database recommendation

Use SQLite in WAL mode initially because AgentFox currently runs as one host process, the workload is operational and transaction-oriented, and the repository already depends on Microsoft.Data.Sqlite.

Use PostgreSQL when any of the following becomes true:

- Multiple processes need to write concurrently.
- The broker worker is deployed separately.
- High availability or failover is required.
- Multiple AgentFox instances share accounts.
- Centralized access control, backups, or observability are required.

Use DuckDB for:

- Backtesting.
- Historical price analysis.
- Signal-quality reports.
- Strategy performance and attribution.
- Large analytical joins and columnar exports.

Populate DuckDB from immutable operational events or periodic Parquet exports. It must not become the authoritative order ledger.

### Core tables

#### Configuration and reference data

- trading_accounts
- instruments
- watchlists
- watchlist_instruments
- strategies
- strategy_versions
- risk_policies
- market_sessions
- market_holidays
- execution_modes

#### Inbound and decision data

- inbound_messages
- parsed_signals
- trade_proposals
- proposal_legs
- validation_results
- approvals
- rejections

#### Execution data

- orders
- order_attempts
- broker_orders
- executions
- fills
- cancellations
- order_events
- idempotency_keys

#### Portfolio data

- positions
- position_lots
- cash_balances
- portfolio_snapshots
- broker_snapshots
- reconciliation_runs
- reconciliation_breaks

#### Operations and audit

- policy_versions
- strategy_runs
- risk_events
- kill_switch_events
- alerts
- outbox_messages
- audit_events

### Important data rules

- Store money and quantities as fixed-precision decimals or integer minor units, never floating point.
- Store timestamps in UTC and retain the market timezone separately where needed.
- Keep broker identifiers and client identifiers distinct.
- Make order state transitions append-only in order_events.
- Derive current order and position views transactionally from authoritative events.
- Record the exact policy version, strategy version, model identifier, prompt version, and parser output used for each proposal.
- Redact credentials and secrets from all database records, logs, screenshots, and model context.

## Execution modes

Support explicit modes at account and strategy level:

### Disabled

- Parse and record signals only.
- No proposals or broker interaction.

### Paper

- Create proposals and simulated orders.
- Use recorded or live market data.
- No broker submission.

### Shadow

- Evaluate live signals and produce intended orders.
- Compare intended results against real market behavior.
- No broker submission.

### ApprovalRequired

- Create an immutable proposal.
- Require an authorized human to approve the exact proposal version.
- Invalidate approval when material fields or policy versions change.

### BoundedAuto

- Execute only an enabled, versioned strategy.
- Enforce stricter automated limits than manual limits.
- Require healthy reconciliation, market data, alerts, and kill switches.

Do not provide an unrestricted FullyAuto mode. BoundedAuto is the maximum recommended autonomy level.

## Trade lifecycle

1. Authenticate and persist the inbound message.
2. Claim its idempotency key.
3. Parse one or more candidate signals.
4. Normalize instrument identifiers and reject unknown instruments.
5. Attach an enabled strategy version and policy snapshot.
6. Create a non-executable proposal.
7. Run deterministic validation and risk checks.
8. Record rejection, paper execution, approval request, or bounded-auto authorization.
9. Revalidate immediately before submission.
10. Persist the order and outbox command in one database transaction.
11. Submit exactly once through the broker adapter.
12. Record accepted, rejected, or unknown outcome.
13. Reconcile unknown outcomes before retrying.
14. Reconcile fills, positions, cash, and exit orders.
15. Monitor until the position and all related orders are closed or manually transferred.

## Order and position behavior

### Entry orders

- Reject missing prices unless a named strategy explicitly allows price derivation.
- Record both requested and submitted prices.
- Never silently convert a limit order to market.
- Never silently change quantity because of parser ambiguity.

### Take-profit orders

- Preserve the original target as strategy intent.
- If the target is outside the current daily band, keep the intended target pending rather than permanently lowering it without authorization.
- Activate or amend the exit when broker and exchange rules permit.
- Link every exit to the position lot or entry order it protects.

### Stop-loss orders

- Prefer broker-native protection.
- If simulated client-side, require current price data, a continuously healthy worker, and explicit alerts when protection is unavailable.
- Define gap behavior and permitted slippage.

### Partial fills

- Create exits only for confirmed filled quantity.
- Adjust exits as additional fills arrive.
- Prevent cumulative sell quantity from exceeding confirmed holdings.

## Security and secrets

- Move broker username, password, PIN, webhook secret, and API credentials to environment variables or a secret provider.
- Do not expose secrets through plugin configuration endpoints.
- Encrypt sensitive local data where practical.
- Restrict the Chromium profile directory to the service identity.
- Redact HTML dumps and screenshots that can contain account information.
- Use separate roles for viewer, proposer, approver, operator, and administrator.
- Require stronger authentication for risk-policy and execution-mode changes.
- Audit every authorization and policy change.

## Reliability and recovery

- Use a transactional outbox for broker commands and notifications.
- Use leases for background work to prevent two workers processing the same item.
- Add bounded retries only for operations known to be safe to repeat.
- Do not retry an order submit with an unknown result until broker reconciliation completes.
- Persist worker checkpoints and next-attempt times.
- Make startup perform broker and database reconciliation before enabling execution.
- Automatically enter safe mode after repeated reconciliation failures.
- Back up the operational database and regularly test restoration.
- Emit health checks for database, broker login, market data, calendar freshness, worker lag, and alert delivery.

## Observability

Minimum metrics:

- Signals received, authenticated, rejected, and replayed.
- Parser success, ambiguity, and per-source signal quality.
- Proposals created, approved, rejected, expired, and invalidated.
- Orders submitted, accepted, rejected, unknown, partially filled, filled, and cancelled.
- Broker and reconciliation latency.
- Position and cash reconciliation breaks.
- Risk-limit blocks by rule.
- Open exit-protection gaps.
- Worker lag and retry counts.
- Daily P&L, exposure, and drawdown by account and strategy.

Minimum alerts:

- Kill switch activated.
- Broker response unknown.
- Reconciliation break.
- Position without expected exit protection.
- Repeated authentication failures.
- Stale market calendar or price feed.
- Daily loss or exposure threshold reached.
- Background worker unhealthy.
- Database backup or restore verification failed.

## Implementation phases

### Phase 0: Containment and safety fixes

Tasks:

- Protect place_order and place_orders with HITL and plan policies.
- Enforce market state inside order tools.
- Correct normal and Friday market sessions.
- Authenticate the WhatsApp webhook.
- Disable market orders by default.
- Move secrets out of checked configuration.
- Add focused unit and integration tests for current gates.

Exit criteria:

- Existing live execution is fail-closed.
- No documented path bypasses approval or market validation.

### Phase 1: Trading Manager core and SQLite ledger

Tasks:

- Add TradingManager, ITradingRepository, IRiskEngine, IMarketCalendar, and IBrokerAdapter.
- Create database migrations and core tables.
- Add durable inbound-message idempotency.
- Add order state machine and audit events.
- Wrap AhkBroker with an adapter.
- Convert existing tools into compatibility calls to TradingManager.

Exit criteria:

- All order attempts and outcomes are durably represented.
- Restart tests prove no duplicate submission.
- JSONL remains optional export, not source of truth.

### Phase 2: Dedicated Trading Agent and routing

Tasks:

- Add specialist-agent registration contracts to AgentFox.Plugins.
- Add AgentRegistry and AgentRouter in the host.
- Register trading-agent from the plugin.
- Give it a dedicated prompt, model key, session namespace, and tool allowlist.
- Add delegate_to_trading_agent to the main agent.
- Route whatsapp-bridge directly to trading-agent.
- Remove trading prompt contribution and raw execution tools from the main agent.

Exit criteria:

- Trading messages use an isolated agent.
- The main agent cannot directly call broker execution.
- Prompt-injection tests cannot reach general-purpose main-agent tools from the signal channel.

### Phase 3: Portfolio and broker reconciliation

Tasks:

- Add broker queries for open orders, executions, positions, and balances.
- Implement reconciliation runs and break resolution.
- Handle partial fills and cancellations.
- Link exit orders to confirmed filled quantity.
- Implement complete take-profit and stop-loss state machines.

Exit criteria:

- Local positions and cash can be proven against broker state.
- Unknown submit outcomes resolve without duplicate orders.

### Phase 4: Paper and shadow trading

Tasks:

- Add paper and shadow execution adapters.
- Record market snapshots used for decisions.
- Add strategy versioning and backtest datasets.
- Export immutable events to Parquet or DuckDB.
- Build signal-quality and strategy-performance reports.

Exit criteria:

- At least one strategy completes an agreed observation period without live submission.
- Results include fees, slippage, rejects, partial fills, and data-quality failures.

### Phase 5: Approval-required live pilot

Tasks:

- Enable a small configured instrument universe.
- Set conservative per-order, daily-loss, and portfolio limits.
- Require exact proposal approval.
- Run daily reconciliation and operational review.
- Exercise kill switch and recovery procedures.

Exit criteria:

- No unresolved reconciliation breaks.
- Approval, alerting, backup, and recovery drills pass.
- Broker and regulatory requirements are confirmed.

### Phase 6: Bounded automation

Tasks:

- Select only strategies that passed paper, shadow, and approved-live stages.
- Apply stricter automatic limits.
- Require current market data, calendar, broker, database, alert, and reconciliation health.
- Automatically downgrade to ApprovalRequired or Disabled on health failure.

Exit criteria:

- Every autonomous order is attributable to an enabled strategy and policy version.
- Automated risk, kill-switch, reconciliation, and incident-response tests pass.

## Test strategy

### Unit tests

- Position sizing and decimal rounding.
- Confidence and policy gates.
- Market sessions, Friday split sessions, holidays, and special schedules.
- Idempotency key generation and state transitions.
- Risk limits individually and in combination.
- Proposal expiry and approval invalidation.
- Partial-fill exit quantity calculations.
- Take-profit and stop-loss state machines.

### Integration tests

- Authenticated webhook to persisted inbound message.
- Duplicate webhook before and after restart.
- Proposal to approval to broker-adapter command.
- Broker timeout with an accepted order discovered during reconciliation.
- Rejected order and safe retry.
- Database transaction rollback and outbox recovery.
- Main-agent delegation and direct trading-channel routing.

### Security tests

- Forged signature and replay attempts.
- Prompt injection through an inbound signal.
- Unauthorized sender, group, role, and account.
- Secret redaction in logs, screenshots, audit events, and model context.
- Attempts to execute through unregistered tool names or batch paths.

### Failure-injection tests

- Process termination before and after broker submission.
- Database unavailable.
- Browser or broker session lost.
- Market data stale.
- Calendar unavailable.
- Notification delivery unavailable.
- Reconciliation mismatch.
- Worker duplication and lease expiry.

## Proposed configuration shape

    Trading:
      DefaultMode: Paper
      OperationalDatabase:
        Provider: Sqlite
        ConnectionString: Data Source=trading.db
        Wal: true
      Router:
        DedicatedChannels:
          - whatsapp-bridge
        MainDelegationTool: delegate_to_trading_agent
      Agent:
        Id: trading-agent
        ModelKey: TradingModel
        MaxConcurrentTurns: 1
      Risk:
        MaxOrderValuePkr: 50000
        MaxDailyLossPkr: 10000
        MaxGrossExposurePkr: 250000
        MaxOpenOrdersPerSymbol: 2
        PriceMaxAgeSeconds: 30
      Execution:
        AllowMarketOrders: false
        RequireReconciliationHealthy: true
        FailClosedOnCalendarError: true
      Webhook:
        RequireSignature: true
        MaxClockSkewSeconds: 120
      Analytics:
        Provider: DuckDb
        ExportIntervalMinutes: 60

Secrets referenced by this configuration must be resolved from a secret provider and must not be embedded in the file.

## Suggested repository changes

### AgentFox.Plugins

- Interfaces/IAgentPlugin.cs
- Interfaces/IAgentRegistry.cs
- Models/AgentDescriptor.cs
- Models/AgentRoute.cs

### Agent host

- Agents/AgentRegistry.cs
- Agents/AgentRouter.cs
- Agents/SpecialistAgentFactory.cs
- Tools/DelegateToAgentTool.cs
- Route integration in FoxAgentService and channel command handling

### TradingAgent plugin

- Agent/TradingAgentPlugin.cs
- Agent/TradingAgentPrompt.cs
- Agent/TradingAgentTools.cs
- Manager/TradingManager.cs
- Manager/TradeIntent.cs
- Manager/TradeProposal.cs
- Manager/OrderStateMachine.cs
- Risk/TradingRiskEngine.cs
- Market/IMarketCalendar.cs
- Market/PsxMarketCalendar.cs
- Broker/IBrokerAdapter.cs
- Broker/AhkBrowserBrokerAdapter.cs
- Persistence/ITradingRepository.cs
- Persistence/SqliteTradingRepository.cs
- Persistence/Migrations
- Reconciliation/BrokerReconciler.cs
- Workers/OrderOutboxWorker.cs
- Workers/ReconciliationWorker.cs
- Security/WebhookSignatureValidator.cs

Names may be adjusted to repository conventions, but the component boundaries should remain.

## Regulatory and broker readiness

Before enabling live automated execution:

- Confirm with the broker whether automated or algorithmic order submission is permitted for the account.
- Prefer a supported API and obtain written integration requirements when available.
- Review current PSX and SECP rules, notices, and market schedules.
- Determine whether registration, testing, identifiers, supervision, or reporting are required.
- Retain a complete audit trail of strategy versions, approvals, controls, submissions, and outcomes.
- Document incident response, operator accountability, and change approval.

Reference material:

- PSX trading hours: https://www.psx.com.pk/psx/exchange/general/trading-hours
- PSX legal framework: https://www.psx.com.pk/psx/regulations/legal-framework
- SECP algorithmic-trading concept paper: https://www.secp.gov.pk/document/concept-paper-regulating-algorithmic-trading-in-pakistans-capital-market/
- DuckDB concurrency: https://duckdb.org/docs/current/connect/concurrency
- SQLite WAL: https://sqlite.org/wal.html

## Definition of production ready

The system is production ready for bounded live execution only when all of the following are true:

- Trading requests are isolated from the main general-purpose agent.
- Every execution crosses the deterministic Trading Manager boundary.
- Webhook authentication and durable idempotency are enabled.
- Operational state is transactional and recoverable.
- Market calendar and market data are current and fail-closed.
- All live orders are covered by configured risk and authorization policies.
- Broker acceptance, fills, positions, and cash are reconciled.
- Partial fills, take-profit, stop-loss, cancellation, and unknown outcomes are handled.
- Secrets are externalized and sensitive artifacts are redacted.
- Kill switches, alerts, backups, restoration, and incident procedures have been tested.
- Paper, shadow, and approval-required stages have completed successfully.
- Broker and regulatory requirements have been confirmed.

## Recommended next action

Start with Phase 0 and Phase 1. Do not begin dedicated-agent routing or bounded automation by exposing more broker tools to the LLM. First make the current execution path fail-closed, then place TradingManager and its operational ledger underneath it. Once that boundary is stable and tested, registering and routing to a dedicated Trading Agent becomes a controlled architectural change rather than a trading-risk change.
