# Specialist Trading Agent: Architecture, Runtime Flow, and UI Management

## 1. Executive summary

The TradingAgent plugin is registered as an isolated **specialist agent** named `trading-agent`. The main AgentFox agent can delegate trade-related questions to it through `delegate_to_agent`, and configured channels such as `whatsapp-bridge` can route directly to it.

The specialist has its own prompt, conversation runtime, concurrency limit, and allowlisted tools. Its public toolset is intentionally limited to research, status, and proposal creation. It cannot place a trade directly. Any execution must cross the deterministic `TradingManager` boundary, where policy, risk, market hours, idempotency, reconciliation health, mode, and kill-switch checks are enforced.

The direct channel flow follows the shared command queue using the dedicated `Specialist` lane. Delegation initiated as a tool call by the main agent executes inline behind the specialist's own semaphore; enqueueing that nested call would deadlock when the main lane is serialized.

UI management is currently **partial**:

- Generic plugin configuration and plugin-session audit APIs exist.
- The existing Plugins page uses those APIs to show session audit details and edit raw JSON configuration.
- There is no complete specialist-agent or trading-manager screen.
- There are no dedicated authenticated APIs for proposals, approvals, risk policy, reconciliation, positions, orders, or the kill switch.

The recommended solution is to add a first-class Specialist Agents area and a separate Trading Manager area. Trading controls must use explicit, audited commands rather than the generic plugin-config endpoint.

## 2. Design principles

1. The language model may research, explain, and propose; deterministic code decides whether execution is allowed.
2. Specialist tools are allowlisted. They do not inherit the main agent's entire tool registry.
3. All direct specialist requests use the shared command queue.
4. A per-specialist semaphore prevents overlapping turns for the same trading runtime.
5. A proposal is not an order. Approval and execution are separate state transitions.
6. Live execution fails closed if broker reconciliation is unsupported, unhealthy, or stale.
7. The SQLite ledger is the operational source of truth for proposals, execution attempts, events, and reconciliation runs. It is not a substitute for broker truth.
8. High-risk changes require authentication, authorization, audit logging, and optimistic concurrency/idempotency.

## 3. Component architecture

```mermaid
flowchart LR
    U["User or external channel"] --> CM["ChannelManager"]
    UI["Management UI"] --> API["Authenticated management API"]
    CM --> CQ["Shared CommandQueue"]
    CQ --> CP["CommandProcessor"]
    CP --> SR["SpecialistAgentRegistry"]
    MA["Main FoxAgent"] --> DT["delegate_to_agent tool"]
    DT --> SR

    subgraph SpecialistRuntime["Isolated trading-agent runtime"]
        TA["Trading FoxAgent"] --> ATR["Allowlisted ToolRegistry"]
        ATR --> READ["Research and status tools"]
        ATR --> PROP["Proposal tools"]
    end

    SR --> TA
    READ --> TM["TradingManager boundary"]
    PROP --> TM
    API --> TM

    TM --> RP["Risk and execution policy"]
    TM --> MC["Market calendar"]
    TM --> REC["Broker reconciliation gate"]
    TM --> DB["SQLite WAL ledger"]
    TM --> BA["Broker adapter"]
    REC --> BA
    BA --> BROKER["Broker or paper adapter"]
```

### Isolation boundary

The specialist descriptor defines:

- stable agent ID;
- description used by the main-agent router;
- dedicated instructions;
- allowed tool names;
- direct channel routes;
- maximum concurrent turns.

At activation, the host builds a separate `FoxAgent` and a separate `ToolRegistry` containing only those allowed tools. The trading specialist therefore cannot silently acquire filesystem, shell, messaging, or unrelated plugin capabilities from the main agent.

## 4. Startup and registration flow

```mermaid
sequenceDiagram
    participant Host as AgentFox host
    participant Plugin as TradingAgentModule
    participant Context as PluginContext
    participant Registry as SpecialistAgentRegistry
    participant Runtime as Trading specialist runtime
    participant Main as Main FoxAgent
    participant Channels as ChannelManager

    Host->>Plugin: RegisterServices()
    Plugin->>Host: Register manager, repository, policy, broker, reconciliation, and tools
    Host->>Main: Build main agent and main tool registry
    Host->>Plugin: OnAgentReady(context)
    Plugin->>Context: Register specialist tools
    Plugin->>Context: Register trading-agent descriptor
    Context->>Registry: Store descriptor and tool factories
    Host->>Registry: Activate specialist agents
    Registry->>Runtime: Build isolated registry and FoxAgent
    Registry->>Main: Add delegate_to_agent routing boundary
    Registry->>Channels: Publish direct channel routes
    Host->>Host: Register Specialist command-lane handler
```

Registration is code-defined during startup. Updating a generic plugin configuration file does not dynamically replace the specialist descriptor, tool allowlist, or runtime concurrency without an explicit lifecycle implementation or application restart.

## 5. Request routing flows

### 5.1 Main-agent delegation

1. A user sends a request to the main agent.
2. The main agent sees `trading-agent` in the description exposed by `delegate_to_agent`.
3. For a trading-domain request, it invokes `delegate_to_agent(agent_id, task)`.
4. The tool calls `SpecialistAgentRegistry.ExecuteAsync` inline.
5. The registry acquires the trading agent's per-agent semaphore.
6. The isolated trading agent handles the task using only its allowlisted tools.
7. The specialist response returns to the main agent, which presents the final response.

This nested delegation is intentionally not placed back onto the command queue. The main agent is already occupying the serialized `Main` lane while its tool call is pending. Waiting for a nested queued command could create a queue dependency cycle. The specialist semaphore still enforces its configured concurrency.

### 5.2 Direct channel routing

```mermaid
sequenceDiagram
    participant User
    participant Channel as Channel adapter
    participant Manager as ChannelManager
    participant Queue as CommandQueue
    participant Processor as CommandProcessor
    participant Registry as SpecialistAgentRegistry
    participant Agent as trading-agent

    User->>Channel: Trading message
    Channel->>Channel: Verify signature, timestamp, sender/group, and replay ID
    Channel->>Manager: ChannelMessage
    Manager->>Manager: Resolve HITL command and specialist route
    Manager-->>User: Optional acknowledgement
    Manager->>Queue: Enqueue SpecialistAgentCommand
    Queue->>Processor: Dequeue Specialist lane
    Processor->>Processor: Apply lane concurrency, timeout, and cancellation
    Processor->>Registry: ExecuteAsync(trading-agent, session, message)
    Registry->>Registry: Acquire per-agent semaphore
    Registry->>Agent: Run isolated turn
    Agent-->>Registry: Result
    Registry-->>Processor: Result
    Processor-->>Manager: Complete command task
    Manager-->>Channel: Send response
    Channel-->>User: Specialist response
```

The queue priority is:

1. `Main`
2. `Specialist`
3. `Subagent`
4. `Tool`
5. `Background`

The Specialist lane has its own host concurrency limit, while the descriptor's `MaxConcurrentTurns` applies an additional per-agent limit. The trading specialist currently uses a maximum of one concurrent turn to protect conversational and trading state from overlapping requests.

### 5.3 Queue fallback

`ChannelManager` can call the registry directly only when no command queue was provided. The normal hosted application supplies the queue, so production direct-channel traffic uses the Specialist lane. The fallback exists for lightweight hosts and tests; it should be monitored and preferably disabled in production validation.

## 6. Trading decision and execution flow

### 6.1 Research and proposal

1. The specialist parses the user's request and obtains market/account information from read-only tools.
2. It explains assumptions and risk.
3. It invokes the proposal tool with structured order intent.
4. `TradingManager` validates symbols, quantity/notional, policy limits, allowed order types, and market rules.
5. The repository stores an immutable proposal and audit event with a stable ID.
6. The user receives the proposal ID and status. No broker order has been submitted.

### 6.2 Approval and execution state machine

```mermaid
stateDiagram-v2
    [*] --> Proposed
    Proposed --> Rejected: operator rejects
    Proposed --> Expired: validity window ends
    Proposed --> Approved: authorized approval
    Approved --> Blocked: policy, risk, market, kill switch, or reconciliation fails
    Approved --> Submitted: idempotent broker submission
    Submitted --> PartiallyFilled: broker reconciliation
    Submitted --> Filled: broker reconciliation
    Submitted --> Cancelled: broker cancellation
    PartiallyFilled --> Filled: remaining fill reconciled
    PartiallyFilled --> Cancelled: remainder cancelled
    Filled --> [*]
    Rejected --> [*]
    Expired --> [*]
    Blocked --> [*]
    Cancelled --> [*]
```

Before any live submission, deterministic code must check:

- execution mode is permitted;
- automatic execution policy is enabled for that proposal type;
- kill switch is not active;
- proposal is current and has not already been executed;
- approval identity and proposal/version hash match;
- symbol, quantity, notional, daily-loss, concentration, and order-type limits pass;
- market session rules pass;
- broker reconciliation is supported, healthy, and fresh;
- the idempotency key has not already been submitted.

Only then may the broker adapter receive the order. Every attempt and outcome is persisted.

### 6.3 Paper, shadow, and live modes

| Mode | Behavior | Recommended use |
|---|---|---|
| Disabled | Research and proposals only; no execution | Default startup |
| Paper | Submit to a deterministic paper broker and record simulated outcomes | Development and UI validation |
| Shadow | Evaluate live data and record the decision that would have occurred, but submit nothing | Policy validation |
| Live / ApprovalRequired | Live submission only after explicit authorized approval and all gates | Controlled rollout |
| Live / BoundedAuto | Autonomous submission only inside strict configured limits and all gates | Final rollout stage only |

The current AHK broker adapter does not provide a reliable positions, balances, orders, and fills read API. It therefore reports reconciliation as unsupported. Because live modes require healthy reconciliation by default, live execution fails closed. Paper, research, status, and proposals remain usable.

### 6.4 Background management

Background actions such as take-profit evaluation must enter through `TradingManager`, not call the broker directly. They are allowed only in an explicitly enabled bounded-auto policy and must pass the same risk, reconciliation, idempotency, and audit controls. Background work should run on the `Background` command lane so interactive traffic remains responsive.

## 7. Persistence and source of truth

The local SQLite database uses WAL mode and stores operational records such as:

- proposals and their lifecycle;
- approvals and rejection/expiry events;
- execution attempts and idempotency keys;
- broker responses;
- reconciliation runs and health;
- audit events.

SQLite is recommended for the transactional control ledger. DuckDB is better suited to analytical workloads and historical market-data queries, not concurrent transactional order state. If analytics grow, add DuckDB as a separate read/analytics store fed from immutable events; do not replace the execution ledger with it.

Broker state remains authoritative for actual cash, positions, open orders, fills, and cancellations. Reconciliation compares broker truth to the local ledger and blocks live execution when the comparison cannot be trusted.

## 8. Current UI manageability

### Current support matrix

| Capability | Backend today | UI today | Status |
|---|---|---|---|
| List/update/delete generic plugin config | `/plugin-config` APIs | Plugins page provides a raw JSON editor | Available, but unsuitable for trading controls |
| Inspect plugin sessions and tool executions | `/plugin-sessions` APIs | Plugins page shows sessions, statistics, and execution detail | Available |
| View registered specialist descriptors | No dedicated endpoint | No | Missing |
| View Specialist queue depth/running commands | No dedicated endpoint | No | Missing |
| Change specialist prompt/tool allowlist/routes/concurrency | Code/startup descriptor | No | Missing |
| View trading status/mode/kill switch | Agent status tool only | No | Missing |
| Manage proposals and approvals | No dedicated web API | No | Missing |
| View orders, fills, positions, balances | No complete broker/reconciliation API | No | Missing |
| Manage risk policy | Configuration/code | No safe purpose-built API | Missing |
| Inspect reconciliation health/history | Status tool and database | No | Missing |
| Activate/deactivate kill switch | Manager/config path only | No secured operator action | Missing |
| Audit operator changes | Trading ledger covers manager actions, generic config is insufficient | No consolidated view | Partial |

Therefore, the trading specialist is operationally manageable through configuration and conversational tools, but **not yet completely manageable through the web UI**.

The generic plugin-config API should not become the trading control plane. It is suitable for low-risk presentation or prompt settings. Trading policy changes and execution commands need typed validation, authorization, audit records, and concurrency checks.

The current web startup maps the `/api` group without evident authentication or authorization middleware. That is acceptable only for a trusted local development surface. Authentication and server-side authorization are a release blocker before exposing trading mutations or binding the management server beyond localhost.

## 9. Recommended UI architecture

### 9.1 Specialist Agents page

Show a read-only runtime inventory first:

- ID, plugin, description, activation state, and startup errors;
- model/provider and prompt revision;
- allowlisted tools;
- direct channel routes;
- per-agent maximum concurrency;
- active turns, queue depth, last request, latency, and failure count;
- restart-required indicator for descriptor changes.

Allow edits only through a validated draft/publish workflow. Tool allowlist and routes should be selected from known registered values, not arbitrary strings.

### 9.2 Trading overview

Display these safety signals above portfolio metrics:

- `Disabled`, `Paper`, `Shadow`, or `Live` mode;
- kill-switch state;
- broker connectivity;
- reconciliation supported/healthy/last successful age;
- market status and timezone;
- database health;
- pending proposals, open orders, and today's realized/unrealized loss;
- visible warning that live execution is blocked when any mandatory gate is red.

### 9.3 Proposals and approvals

Provide a searchable proposal table and immutable detail page containing:

- symbol, side, quantity, order type, limit/stop values, and estimated notional;
- rationale and originating user/session/channel;
- risk-check results and policy revision;
- created/expires timestamps;
- proposal version/hash;
- approval, rejection, execution, and reconciliation history.

Approval must be a dedicated command requiring the current proposal version/hash. A stale screen must receive `409 Conflict`, forcing the operator to reload. Approval must never be a simple editable status field.

### 9.4 Orders and portfolio

Show local ledger state beside broker state and highlight discrepancies. Include orders, fills, partial fills, cancellations, positions, balances, realized/unrealized P&L, and the reconciliation run that verified each value.

### 9.5 Risk and execution policy

Use typed fields with units and validation for:

- permitted modes and symbols;
- per-order quantity/notional limits;
- daily loss and concentration limits;
- allowed order types and trading hours;
- proposal expiry;
- auto-execution bounds;
- reconciliation interval and maximum age.

Policy edits should create a new version with author, timestamp, reason, and diff. Activating a risk-loosening change should require elevated permission and optional second-person approval.

### 9.6 Audit and diagnostics

Unify specialist turns, tool calls, proposals, operator actions, broker submissions, and reconciliation events by correlation ID. Secrets, credentials, webhook signatures, and sensitive broker payload fields must be redacted.

## 10. Recommended management API

The exact route prefix may follow the existing web module convention, but the resource boundaries should be explicit:

```text
GET  /specialist-agents
GET  /specialist-agents/{agentId}
GET  /specialist-agents/{agentId}/metrics
GET  /command-queues

GET  /trading/status
GET  /trading/proposals
POST /trading/proposals
GET  /trading/proposals/{proposalId}
POST /trading/proposals/{proposalId}/approve
POST /trading/proposals/{proposalId}/reject
POST /trading/proposals/{proposalId}/expire

GET  /trading/orders
GET  /trading/executions
GET  /trading/positions
GET  /trading/balances
GET  /trading/reconciliation
POST /trading/reconciliation/run

GET  /trading/policies
POST /trading/policies/drafts
POST /trading/policies/{version}/activate
POST /trading/kill-switch/activate
POST /trading/kill-switch/deactivate
GET  /trading/audit-events
```

Mutation requirements:

- authenticated user identity and role;
- anti-CSRF protection where cookie authentication is used;
- request ID and idempotency key;
- expected resource version/hash;
- reason field for policy, rejection, and kill-switch actions;
- transactional database update and immutable audit event;
- structured error codes explaining which safety gate blocked the request.

Prefer server-sent events or the project's existing streaming mechanism for queue state, reconciliation status, proposal changes, and fills. Polling can be the initial implementation.

## 11. Roles and permissions

| Role | Suggested permissions |
|---|---|
| Viewer | Read specialist health, proposals, portfolio, reconciliation, and audit records |
| Analyst | Viewer plus create proposals; cannot approve or execute |
| Trader | Analyst plus approve/reject proposals within assigned limits |
| Risk Manager | Manage and activate policy versions; review exceptions |
| Administrator | Manage specialist lifecycle, routes, integration configuration, and access |

The kill switch should be activatable by Trader, Risk Manager, and Administrator because stopping risk should be easy. Deactivation should require Risk Manager or Administrator authority, a healthy reconciliation, and an audit reason.

## 12. UI implementation roadmap

### Phase 1: visibility and paper trading

1. Add read-only specialist inventory and queue metrics endpoints.
2. Add trading status, proposal, execution, reconciliation, and audit read endpoints.
3. Build Specialist Agents and Trading Overview pages.
4. Build proposal and paper-order history pages.
5. Run only in Disabled/Paper/Shadow modes.

### Phase 2: controlled operator actions

1. Add authentication and role-based authorization if not already enforced for these routes.
2. Add typed proposal create/reject/approve commands with version checks and idempotency.
3. Add kill-switch activation/deactivation commands.
4. Add versioned risk-policy drafts and activation.
5. Add real-time status updates and alerting.

### Phase 3: broker truth and live readiness

1. Replace or extend the AHK adapter with reliable APIs for balances, positions, open orders, fills, and cancellations.
2. Make reconciliation pass under disconnect, partial-fill, duplicate-request, restart, and stale-data tests.
3. Run Shadow mode for a defined observation period.
4. Enable ApprovalRequired live mode for a tightly limited symbol/notional set.
5. Consider BoundedAuto only after production evidence and an operational review.

## 13. Acceptance criteria

The specialist architecture is complete when:

- main-agent trade questions can be delegated to the isolated trading runtime;
- configured direct channels execute through the Specialist queue lane;
- concurrent turns respect both lane and per-agent limits;
- the trading specialist cannot access non-allowlisted tools;
- proposals cannot bypass `TradingManager`;
- duplicate approval/execution requests cannot duplicate an order;
- live execution is blocked on stale or unsupported reconciliation;
- every material decision has a correlation ID and immutable audit event.

The management UI is complete when:

- an authorized operator can view specialist, queue, broker, reconciliation, proposal, order, portfolio, policy, and audit state;
- high-risk actions use dedicated typed commands, not generic config mutation;
- stale UI actions are rejected using versions/hashes;
- permission boundaries are tested server-side;
- kill-switch activation remains available during degraded broker or agent health;
- secrets never appear in page payloads, logs, or audit details;
- Paper and Shadow workflows can be operated end-to-end before any Live control is exposed.

## 14. Repository-specific implementation plan

### Implementation status (2026-07-11)

The first safe management milestone is implemented:

- optional API-key authentication with hierarchical Viewer/Analyst/Trader/RiskManager/Administrator roles;
- authorization applied to the management `/api` group, with inbound channel webhook POSTs explicitly left to their transport-level authentication;
- browser-session API-key support in Settings;
- specialist runtime status and per-command-lane queue metrics endpoints;
- Agents UI sections for specialist isolation, tools, routes, concurrency, activity, failures, and queues;
- typed SQLite query methods for proposals, executions, reconciliation runs, and order events;
- plugin-owned read-only `/trading` endpoints;
- a read-only Trading Manager dashboard for safety state, proposals, executions, reconciliation, and audit history;
- reconciliation freshness and kill-switch state included in live-readiness calculation;
- tests for management authentication, role inheritance, queue reporting, specialist reporting, and typed ledger queries.

Management authentication remains disabled in the sample local configuration for backward-compatible localhost development. Set `Web:ManagementAuth:Enabled` to `true` and configure a strong API key before exposing the service. Proposal mutations, persisted kill-switch commands, versioned policy editing, broker portfolio reads, and live controls remain gated by the later work packages below. The current AHK adapter still cannot satisfy live reconciliation, so live execution remains fail-closed.

This plan turns the UI design into reviewable implementation slices. Each slice should compile and test independently. Do not expose live execution controls merely because their pages render; enable them only after the security and broker-readiness gates are complete.

### Work package 0: protect the management surface

**Goal:** establish an authenticated identity and server-enforced roles before adding trading mutations.

Backend work:

1. Add the chosen ASP.NET Core authentication scheme in `src/Agent/Program.cs`.
2. Add `UseAuthentication()` and `UseAuthorization()` before mapping `/api`.
3. Require a default authorization policy for management endpoints; explicitly decide whether `/health` remains anonymous.
4. Define policies for `Viewer`, `Analyst`, `Trader`, `RiskManager`, and `Administrator`.
5. Add anti-CSRF protection if browser cookies are used. If bearer tokens are used, document secure token storage and expiry.
6. Bind non-development web hosts to localhost by default and require explicit configuration to expose them remotely.
7. Add a request audit service that records actor ID, role, request ID, action, target, outcome, timestamp, and redacted metadata.

Frontend work:

1. Add authentication state and expired-session handling to `src/frontend/src/lib/stores.ts`.
2. Update the fetch helpers in `src/frontend/src/lib/api.ts` to send credentials/tokens and surface structured `401`, `403`, `409`, and safety-gate errors.
3. Hide controls the current user cannot invoke, while treating backend authorization as authoritative.

Tests:

- anonymous mutation is rejected;
- each role has exactly its intended permissions;
- read-only users cannot approve, change policy, or deactivate the kill switch;
- audit records contain actor/action/outcome but no credentials or webhook secrets;
- remote binding is opt-in.

**Exit gate:** no trading mutation endpoint is merged without these protections, except behind a development-only feature flag that is off by default.

### Work package 1: specialist runtime observability

**Goal:** make the implemented specialist registration and queue flow visible without adding control-plane mutations.

Backend work:

1. Add a runtime status DTO rather than returning `SpecialistAgentDescriptor` directly. Exclude full system prompts and sensitive configuration.
2. Extend `IAgentRegistry` or introduce a host-side diagnostics interface for activation state, active turns, last turn, failures, and latency.
3. Add read-only endpoints to `src/Agent/Modules/Web/WebModule.cs`:
   - `GET /specialist-agents`;
   - `GET /specialist-agents/{agentId}`;
   - `GET /command-queues`.
4. Return queue counts from `ICommandQueue.GetQueueCount` for every `CommandLane`.
5. Add running-command counts to `CommandProcessor`; do not expose message content in queue metrics.
6. Update the existing `/agents` response or Agents page contract so specialist agents are distinguishable from main and transient subagents.

Frontend work:

1. Add specialist and queue DTOs/methods in `src/frontend/src/lib/api.ts`.
2. Extend `src/frontend/src/routes/agents/+page.svelte` with Main, Specialist, and Subagent sections.
3. Show activation state, allowed tools, channel routes, concurrency, active turns, queued commands, last activity, latency, and last error.
4. Poll every 5–10 seconds initially. Introduce SSE only after the read model is stable.

Tests:

- registered-but-not-activated and activated states serialize correctly;
- tool allowlists and routes are returned, but system prompts are not;
- all command lanes appear, including an empty Specialist lane;
- active/running counters return to zero after cancellation and timeout;
- frontend renders loading, empty, degraded, and healthy states.

**Exit gate:** a user can verify that `trading-agent` is active, isolated, routed, and using the Specialist lane.

### Work package 2: trading read model and query APIs

**Goal:** provide stable typed queries for the dashboard, proposals, executions, reconciliation, and audit history.

Persistence work:

1. Add query DTOs and paginated methods to `src/Plugins/TradingAgent/Persistence/ITradingRepository.cs` for proposals, executions, events, and reconciliation runs.
2. Add schema migrations/version tracking to `SqliteTradingRepository`; do not rely only on ad hoc `CREATE TABLE IF NOT EXISTS` once UI contracts depend on the schema.
3. Add indexes for proposal state/created time, execution state/created time, correlation ID, idempotency key, and reconciliation time.
4. Keep raw broker payloads internal and map them to redacted response DTOs.

Service work:

1. Add `ITradingManagementService` as the single query/command facade used by web endpoints. It should call `TradingManager` and the repository rather than duplicating safety rules in `WebModule`.
2. Create a plugin-neutral endpoint registration mechanism if the host should not reference TradingAgent types directly. Recommended: add an optional plugin endpoint contributor contract and let the TradingAgent module map `/trading/*` routes.
3. Add typed read endpoints for status, proposals, executions, reconciliation, and audit events.
4. Return pagination metadata, correlation IDs, UTC timestamps, policy versions, and explicit reconciliation freshness.

Frontend work:

1. Add a `/trading` route and Trading navigation item in `src/frontend/src/lib/components/Sidebar.svelte`.
2. Add shared trading API types and calls in `src/frontend/src/lib/api.ts` or split the growing client into `src/frontend/src/lib/api/trading.ts`.
3. Build `src/frontend/src/routes/trading/+page.svelte` as a safety-first overview.
4. Build read-only proposal, execution, reconciliation, and audit views under `src/frontend/src/routes/trading/`.
5. Put mode, kill switch, broker health, and reconciliation freshness in a persistent summary banner.

Tests:

- pagination and filters are deterministic;
- no raw secrets or sensitive payload fields are returned;
- timestamps are UTC and status enums are stable;
- a database restart preserves read-model state;
- stale, unsupported, failed, and healthy reconciliation states render distinctly.

**Exit gate:** Disabled, Paper, and Shadow operation can be inspected completely through the UI without querying SQLite manually.

### Work package 3: proposal commands

**Goal:** allow authorized users to create, reject, and expire proposals safely. Approval can be implemented here for Paper mode but must remain disabled for live mode until later gates pass.

Backend work:

1. Replace the opaque proposal JSON storage contract with a versioned typed proposal model while retaining the original payload for audit compatibility if required.
2. Add proposal version/hash, expiry, actor, origin, correlation ID, and state-transition fields.
3. Implement command methods on `ITradingManagementService`:
   - create proposal;
   - reject proposal;
   - expire proposal;
   - approve proposal.
4. Require `Idempotency-Key` for creates and approvals and `If-Match` or an expected proposal version/hash for transitions.
5. Execute each transition and audit append in one SQLite transaction.
6. Return `409 Conflict` for stale versions and already-terminal proposals.
7. Ensure approval invokes `TradingManager`; the endpoint must never call `IBrokerAdapter` directly.

Frontend work:

1. Build typed proposal forms with units, symbol validation, and a calculated notional preview.
2. Build an immutable proposal detail and transition timeline.
3. Require a confirmation dialog showing exact side, symbol, quantity, order type, and notional before approval.
4. On `409`, discard the stale action, reload the proposal, and clearly explain what changed.
5. Display deterministic safety-gate failures without offering a client-side bypass.

Tests:

- duplicate create and approve requests are idempotent;
- two simultaneous approvals produce at most one execution claim;
- stale approvals return `409`;
- expired or rejected proposals cannot execute;
- UI confirmation displays the same version/hash sent to the server;
- direct broker calls are impossible from endpoint handlers.

**Exit gate:** proposal lifecycle works end-to-end in Paper mode with immutable audit history and no duplicate order.

### Work package 4: kill switch and versioned risk policy

**Goal:** replace raw JSON edits with safe, typed operational controls.

Backend work:

1. Persist kill-switch state in the trading ledger so it survives process restarts.
2. Implement activate/deactivate commands on `ITradingManagementService`.
3. Permit broad authorized activation; require elevated permission, reason, and healthy reconciliation for deactivation.
4. Create immutable risk-policy versions with draft, active, and superseded states.
5. Validate units, numeric ranges, cross-field invariants, symbol lists, market timezone, and execution-mode transitions server-side.
6. Capture a field-level diff, author, reason, and activation timestamp.
7. Stop reading execution-critical values from generic plugin config once the versioned provider is active.

Frontend work:

1. Add a persistent, high-visibility kill-switch control to the trading shell.
2. Add typed policy forms; do not expose policy JSON as the normal editing workflow.
3. Show current versus proposed policy diff and identify risk-loosening changes.
4. Add a second confirmation for kill-switch deactivation and live-mode activation.

Tests:

- kill-switch state survives restart and blocks all execution paths;
- activation works while broker/reconciliation is degraded;
- deactivation fails while reconciliation is unhealthy;
- invalid or risk-inconsistent policies cannot be saved or activated;
- a running request rechecks kill-switch state immediately before broker submission;
- generic plugin config cannot override active execution policy.

**Exit gate:** operational policy and emergency stop are manageable through typed, audited controls.

### Work package 5: broker read model and reconciliation

**Goal:** establish reliable broker truth, which is mandatory for live execution.

Broker work:

1. Implement reliable read methods for balances, positions, open orders, fills, order status, and cancellations.
2. Prefer a broker-supported API over UI/AHK scraping. If AHK remains, keep live mode prohibited until it can produce complete, stable, uniquely identified records.
3. Normalize broker identifiers and timestamps and preserve the raw source record in restricted logs/storage for investigations.
4. Make reconciliation compare local submissions with broker orders/fills and compare computed positions/balances with broker state.
5. Record discrepancies with severity and block live execution on unresolved material differences.

Frontend work:

1. Build portfolio and order pages that show local and broker values side by side.
2. Add reconciliation history and discrepancy detail.
3. Add an authorized manual reconciliation command; do not allow UI users to mark a discrepancy resolved without a recorded reason and evidence.

Tests:

- disconnect, timeout, partial fill, late fill, cancellation, duplicate callback, unknown broker order, and process restart;
- stale snapshot rejection;
- rounding and currency precision;
- unresolved discrepancy blocks approval/execution;
- reconciliation catches a locally accepted order missing at the broker.

**Exit gate:** reconciliation is supported, healthy, fresh, and proven under failure tests. Until then, live controls remain disabled in both backend and UI.

### Work package 6: live rollout and real-time operations

**Goal:** introduce live operation gradually after all safety gates pass.

1. Add SSE events for specialist health, queue counts, proposals, executions, fills, reconciliation, and kill-switch changes.
2. Run Shadow mode for an agreed observation period and compare expected versus broker-observed decisions.
3. Enable `ApprovalRequired` only for a small symbol allowlist and low notional cap.
4. Add operational alerts for reconciliation staleness, unknown executions, rejected broker submissions, daily-loss proximity, and queue backlog.
5. Document incident response, broker outage behavior, credential rotation, database backup/restore, and kill-switch drills.
6. Consider `BoundedAuto` only after ApprovalRequired live evidence is reviewed and an explicit production policy version is activated.

Tests and drills:

- browser disconnect during approval does not lose or duplicate state;
- process crash immediately before and after broker submission;
- database backup and restore followed by reconciliation;
- kill-switch activation during queue backlog and during broker latency;
- authorization revocation takes effect without waiting for a browser refresh;
- bounded-auto limits hold under concurrent signals.

**Exit gate:** live approval mode has explicit operational sign-off. BoundedAuto is a separate later decision, not an automatic consequence of completing the UI.

## 15. Suggested pull-request sequence

Keep changes small enough to review safety boundaries independently:

1. `security/web-management-auth`
2. `specialists/runtime-observability`
3. `trading/query-read-model`
4. `ui/trading-read-only-dashboard`
5. `trading/typed-proposal-lifecycle`
6. `ui/trading-proposal-workflow`
7. `trading/kill-switch-policy-versioning`
8. `ui/trading-risk-controls`
9. `trading/broker-read-reconciliation`
10. `trading/live-approval-rollout`

Every pull request should include migrations where needed, API contract tests, authorization tests, failure-path tests, frontend state handling, and an update to this document's support matrix.

## 16. Definition of done for the implementation project

The UI implementation project is done only when all of the following are true:

- repository query and command contracts are typed and versioned;
- the management API is authenticated and mutations are role-protected;
- specialist and command-queue health are visible;
- proposal lifecycle, policy, kill switch, reconciliation, orders, portfolio, and audit history are manageable in the UI;
- every mutation is idempotent, concurrency-safe, and audited;
- the UI never bypasses `TradingManager` or talks directly to a broker adapter;
- restart and failure-path tests pass;
- Disabled/Paper/Shadow workflows pass acceptance testing;
- live execution remains fail-closed until broker reconciliation satisfies Work package 5;
- operational documentation and kill-switch drills are complete.
