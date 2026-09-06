# HarnessAgent Adoption Roadmap

## Decision

Adopt Microsoft Agent Framework's `HarnessAgent` incrementally, as an optional
execution profile behind AgentFox's existing agent facade. Do not replace the
current main-agent or trading-execution architecture wholesale.

AgentFox already references `Microsoft.Agents.AI.Harness`
(`1.13.0-preview.260703.1`, pinned centrally in `Directory.Packages.props`) in
the host, shared plugin project, and `TradingAgent`. `AsHarnessAgent` is used
only inside the `HarnessAgentFactory` adapter.

HarnessAgent provides a composed pipeline for function invocation, persistent
chat history, context compaction, todo and mode tracking, file access and
memory, skills, approval support, OpenTelemetry, and optional background
agents. These capabilities overlap with existing AgentFox features, so the
integration must preserve the current security and lifecycle boundaries.

## Architecture Principles

1. **AgentFox remains the control plane.** It owns session identity, channel
   routing, plugin activation, prompt contributors, specialist routing, tool
   lifecycle hooks, audit events, and user-facing HITL requests.
2. **TradingManager remains the execution boundary.** No model, provider, or
   harness tool may call a broker adapter directly.
3. **Policies are enforced in code, never only in prompts.** Harness modes,
   todos, and instructions improve agent behaviour; they do not replace the
   existing plan gate, risk engine, reconciliation checks, or approval checks.
4. **Capabilities are opt-in and least-privilege.** Do not accept HarnessAgent
   defaults for file access, file memory, current-directory skill discovery,
   web search, or shell access without explicit AgentFox configuration.
5. **Use an adapter boundary.** Keep the dependency on preview Harness APIs in
   a small AgentFox integration layer so upgrades do not spread through the
   agent and plugin codebase.

## Current Capability Mapping

| Harness capability | Existing AgentFox capability | Adoption guidance |
| --- | --- | --- |
| Todo list and agent modes | `PlanState`, `submit_plan`, prompt contributor, and hard mutating-tool gate | Keep AgentFox as the enforcement source; optionally use Harness modes/todos as planning UX only. |
| Tool approval | `HitlManager`, `HitlBypassPolicy`, and `WithToolApprovalGate` | Keep the host gate authoritative; do not create an alternate trading approval path. |
| File access and file memory | `WorkspaceManager`, Markdown/SQLite memory, session store | Pilot in a dedicated directory with explicit access policy. |
| Skills | AgentFox skill registry and Composio skills | Add Harness file skills for focused, versioned domain playbooks. |
| Background agents | Sub-agent manager, command lanes, notifications | Use Harness background agents only for stateless parallel research first. |
| Observability | Plugin lifecycle hooks, trading ledger/events, `Telemetry` section (`src/Agent/Telemetry/`) | SDK, listeners and exporters are registered; model calls are instrumented. Remaining work is correlating spans with existing audit records. |
| CodeAct and shell | Existing tool system and workspace enforcement | Defer for TradingAgent; use only in tightly sandboxed non-trading profiles. |

## Phased Roadmap

### Phase 0 — Foundation and Safety Contract

**Goal:** Make Harness adoption reversible and define the non-negotiable
security boundaries.

- Add a feature-flagged `Harness` configuration section. Keep it disabled by
  default.
- Introduce a small `HarnessAgentFactory`/adapter that returns the existing
  `AIAgent` abstraction while isolating `AsHarnessAgent` and preview API use.
- Adopt a preview-package version policy. The
  `Microsoft.Agents.AI.Harness` version is currently repeated in three
  `.csproj` files; move it to central package management
  (`Directory.Packages.props`), pin it, and take preview bumps only as a
  deliberate change with a named owner and a smoke-test gate (build, adapter
  compatibility tests, and the Phase 0 policy-bypass tests all green).
- Define named profiles instead of a global on/off switch:
  - `main-safe`: Harness features disabled until individually approved.
  - `trading-research`: read-only research and reporting only.
  - `developer-sandbox`: optional skills, shell, or CodeAct in a sandbox.
- Define a canonical tool-execution bridge: every Harness-exposed AgentFox tool
  must invoke AgentFox's existing tool gateway, preserving the plan gate, HITL,
  lifecycle hooks, experience learning, and plugin audit events.
- Add compatibility tests for session history, cancellation, dynamic tool
  registration, plugin hooks, and function invocation loops.

**Exit criteria:** A disabled-by-default adapter builds, has no behaviour change
when disabled, and tests demonstrate that bridged tools cannot bypass AgentFox
policy gates.

### Phase 1 — Observability and Read-Only Trading Research Pilot

**Goal:** Gain operational insight and useful research capability without
increasing trading authority.

**Collection is now wired; instrumentation coverage is not.** This bullet used to
read as though `Harness:Profiles:*:EnableOpenTelemetry` was the whole job. It is
not, and the gap was live: two profiles shipped with that flag true while the
solution referenced no OpenTelemetry SDK, so every span they wrote went to an
`ActivitySource` with no listener and was dropped with no error and no log line.
A flag decides whether spans are **written**; a registered SDK decides whether
anything **collects** them, and neither implies the other.

- ~~Register the OpenTelemetry SDK, a listener for every source AgentFox can emit
  to, and OTLP/console exporters.~~ Done — `src/Agent/Telemetry/`, bound from the
  `Telemetry` config section and off by default. The source list is *derived from
  the configured Harness profiles* rather than hard-coded, so a profile that
  renames `OpenTelemetrySourceName` is still collected.
  `TelemetryStartupReport` logs a warning for the two silent states —
  telemetry off while a profile emits, and telemetry on with no exporter — so
  this class of gap announces itself instead of being discovered later.
- ~~Instrument model requests (spans, token usage, duration).~~ Done —
  `UseOpenTelemetry` on the main and specialist chat-client pipelines, emitting to
  `AgentFox.Agent`. It is installed *inside* `DynamicAgentMiddleware`, so a span
  records the tools and prompt addons actually sent to the model. Prompt and
  response bodies are excluded unless `Telemetry:CaptureMessageContent` is set.
- ~~Instrument agent runs, tool calls, approval decisions, broker submissions and
  reconciliation runs.~~ Done. One span each, at the single choke point every
  caller takes rather than per call site, so a new tool / command / gate is traced
  the day it is written:

  | Span | Emitted at | Source |
  | --- | --- | --- |
  | `channel.message` | `ChannelMessageGateway.ProcessChannelMessageAsync` | `AgentFox.Agent` |
  | `command.execute {lane}` | `CommandProcessor.ExecuteHandlerAsync` | `AgentFox.Agent` |
  | `agent.run {agent}` | `FoxAgent.ProcessAsync` | `AgentFox.Agent` |
  | `specialist.delegate {id}` | `SpecialistAgentRegistry.RunAsync` | `AgentFox.Agent` |
  | `tool.execute {tool}` | `AgentBuilder.ExecuteToolAsync` | `AgentFox.Agent` |
  | `approval.request {trigger}` | `HitlManager.RequestApprovalAsync` | `AgentFox.Agent` |
  | `trading.execute` | `TradingManager.ExecuteGroupsAsync` | `AgentFox.Trading` |
  | `trading.reconcile` | `BrokerReconciliationWorker.RunNowAsync` | `AgentFox.Trading` |

  A **refusal is recorded as an error status with its reason**, never as a
  successful span — for a system whose safe behaviour is to decline, "why did
  nothing happen" is the question asked most, and a refusal that traces as success
  cannot answer it. No span carries prompts, tool arguments, results, symbols,
  quantities or prices; the ledger is the durable record and the credential guard
  exists to keep tool output out of anything exportable.

- ~~Propagate a correlation ID through channel message, specialist delegation,
  proposal, execution, and ledger-event records.~~ Done —
  `AgentFox.Plugins.Observability.CorrelationContext`, an `AsyncLocal` scope
  stamped on every span and written to `correlation_id` on `trade_proposals`,
  `trading_executions`, `trading_order_events` and `reconciliation_runs` (additive
  nullable columns; a row written outside any correlation stores NULL rather than a
  minted id, because a correlation group of exactly one row reads like evidence).

  Three things about it are load-bearing and none are visible at a call site:

  - **It lives in `AgentFox.Plugins`, not the host.** An `AsyncLocal` correlates
    nothing unless host and plugin share ONE static field, and `PluginLoadContext`
    resolves only that assembly from the host's context. In the host it would
    silently read null everywhere in the trading plugin.
  - **The command queue is the one hop it does not cross.** A lane loop never
    awaited the producer, so the id travels as data on `ICommand.CorrelationId`
    (captured at construction) and `CommandProcessor` re-enters a scope from it.
    `CorrelationContextTests` pins the loss *and* the repair, so if the ambient ever
    starts surviving that hop the test says so before anyone deletes the repair.
  - **`Ensure()` vs `Begin()` is a decision, not a style.** Work *caused* by
    something upstream keeps that id (`Ensure`); a reconciliation pass reads the
    whole account rather than one turn's orders and takes a fresh one (`Begin`).

- Guarded structurally: `PluginLoadContext` now delegates
  `System.Diagnostics.DiagnosticSource` to the host by name. It defines
  `ActivitySource`, and an `ActivityListener` only observes spans from the type
  identity it was registered against — so a plugin taking a `PackageReference` on
  OpenTelemetry.Api would get a second copy and every plugin span would be created
  and silently never collected. `PluginTypeIdentityTests` pins it.
- Create a dedicated `TradingResearchHarness` specialist with only:
  - market/news and portfolio-read tools;
  - a read-only portfolio/report workspace;
  - no broker credentials;
  - no `place_order` or `place_orders` tool;
  - no shell and no CodeAct.
- Use isolated, minimal background agents for per-symbol research. Their only
  output should be factual, attributable research returned to the main
  specialist for synthesis.
- Treat research output as untrusted data, never as instructions. Market and
  news content is external, attacker-reachable text; a research summary can
  carry injected directives ("ignore risk limits…") into the main specialist,
  which sits upstream of real trade proposals. Tag research output with
  provenance (source, symbol, retrieval time, producing agent), render it to
  the main specialist as quoted data, and rely on the plan gate, risk engine,
  and HITL — not the research profile's read-only scope — as the control for
  anything it influences downstream.
- Cap research fan-out with an explicit resource budget per session: maximum
  concurrent background agents, maximum tokens per research task, and an
  overall per-request budget. Harness background agents must inherit the same
  limits `SubAgentManager` enforces for existing sub-agents; a multi-symbol
  request must degrade (queue or truncate) rather than amplify cost
  unboundedly.

**Exit criteria:** Users can request multi-symbol research and report generation
with a trace linking every result to the initiating session, while the harness
profile has no route to broker execution.

**Checkpoint:** After the pilot, compare against the existing sub-agent
research path. If the harness profile shows no measurable win in reliability,
latency, token use, or audit quality, stop here: keep the adapter as a
research-only feature and do not proceed to Phases 2–5.

### Phase 2 — Governed Trading Skills and Reporting

**Goal:** Put domain procedure in version-controlled skills instead of growing
the system prompt.

- Add local `SKILL.md` packages for:
  - PSX market research;
  - signal-review checklist;
  - risk-review and proposal explanation;
  - portfolio reporting.
- Keep skill scripts disabled initially. Enable a script only after code review,
  deterministic test fixtures, a declared runtime, and a scoped workspace are
  in place.
- Treat skills as guidance, not authorization. A skill cannot approve a trade,
  relax risk limits, or change execution policy.
- Maintain a skill manifest with owner, version, permissions, tests, and
  deprecation status.
- Consider Foundry-managed skills only after tenant, retention, data
  residency, identity scope, and rollout controls are documented.

**Exit criteria:** Research and reporting procedures are versioned and tested;
the main prompt remains small; execution privileges remain unchanged.

### Phase 3 — Tool Approval Hardening

**Goal:** Make trade approval specific, immutable, and auditable before any
convenience approval feature is considered.

This phase does not depend on Harness adoption. It hardens the existing
`HitlManager`/`TradingManager` path and is valuable even if the roadmap is
abandoned at an earlier checkpoint — start it in parallel with Phase 0 rather
than sequencing it after skills.

- Continue using AgentFox HITL as the only authority for live trading approval.
- Approve an immutable, one-time order intent containing at least:
  - proposal ID and source message identity;
  - policy version;
  - symbol, side, quantity, order type, and price/limit;
  - estimated exposure and calculated risk result;
  - expiry time and integrity hash.
- Require `TradingManager` to revalidate every field, policy, market status,
  reconciliation state, and idempotency key immediately before submission.
- Record the requested decision, approver/channel, approved intent hash, and
  final broker result in the ledger.
- Allow automatic approval only for non-mutating or explicitly safe
  administrative reads. Do not use broad “always approve” or value-threshold
  auto-approval for live broker orders.

**Exit criteria:** A changed price, quantity, policy, risk result,
reconciliation state, expired intent, or replayed request is rejected before
broker submission.

### Phase 4 — Selective Main-Agent Integration

**Goal:** Evaluate a controlled HarnessAgent profile for the general agent.

- Run side-by-side evaluation against the existing `AgentBuilder` pipeline.
- Explicitly configure every Harness default:
  - provide AgentFox session-backed chat history;
  - set compaction limits per configured model. Harness compaction is a
    context-window optimization only and must never become the system of
    record: the AgentFox session store remains the complete, authoritative
    history that session recovery depends on;
  - disable file access, file memory, skill discovery, hosted web search, and
    shell until their AgentFox equivalent is intentionally bridged;
  - use a named OpenTelemetry source;
  - preserve AgentFox prompt contributors and dynamic tool updates.
- Compare reliability, latency, token use, recovery after interrupted tool
  loops, and audit completeness across representative channel tasks.
- Migrate only a capability that provides measurable benefit and passes parity
  tests.

**Exit criteria:** Harness mode matches or exceeds existing behaviour for the
approved profile without losing tool authorization, session continuity, or
plugin audit records.

**Checkpoint:** If the side-by-side evaluation shows no measurable improvement
for the main agent, keep the existing `AgentBuilder` pipeline as the default
and limit Harness to the profiles that already proved out. Phase 5 proceeds
only for capabilities with a demonstrated need.

### Phase 5 — Advanced Sandboxed Capabilities

**Goal:** Enable advanced automation only in profiles where its benefit exceeds
the additional attack surface.

- **Shell:** Restrict to a dedicated working directory, use an allow-list rather
  than only a deny-list, set short timeouts and output limits, require HITL for
  mutations, and never expose credentials or trading session files.
- **CodeAct:** Use only for non-trading analysis or developer workflows in an
  isolated sandbox. Portfolio values, sizing, risk checks, and order generation
  must remain deterministic C# services.
- **Foundry memory:** Scope memory by authenticated user/tenant, establish
  retention and deletion rules, and classify what may never be stored.
- **Foundry-managed skills:** Use for centrally governed instructions only after
  change management, version pinning/rollback, and audit requirements are met.

**Exit criteria:** Every advanced capability has an explicit owner, threat
model, integration tests, telemetry, and kill switch.

## Explicit Non-Recommendations

- Do not replace `TradingManager` with LLM or harness orchestration.
- Do not expose broker tools to background research agents.
- Do not treat a prompt, skill, todo, or agent mode as a security control.
- Do not enable default current-directory file access or skill discovery for
  the main agent.
- Do not allow arbitrary shell or model-authored code in the TradingAgent
  profile.
- Do not use Harness standing approvals for real-money order placement.

## Recommended Initial Backlog

1. ~~Add `HarnessOptions` and a disabled-by-default profile selector.~~ Done —
   `src/Agent/Harness/`, bound from the `Harness` config section.
2. ~~Design and implement immutable approval-intent records (Phase 3).~~ Done —
   `ApprovalIntent`/`ApprovalIntentRegistry`; `TradingManager` revalidates hash,
   policy version, expiry, and single-use before submission.
3. ~~Move package versions to `Directory.Packages.props` and document the
   preview-bump policy.~~ Done — full central package management at repo root.
4. ~~Implement and test the AgentFox-to-Harness tool bridge.~~ Done —
   `AgentBuilder.CreateGatewayTools()`/`ExecuteThroughGatewayAsync()`; bypass
   tests in `HarnessAdapterTests`.
5. ~~Register an OpenTelemetry SDK and exporters so emitted spans are actually
   collected, instrument the execution path, and propagate trace/correlation IDs
   through the trading proposal and execution flows.~~ Done —
   `src/Agent/Telemetry/`, `AgentFox.Plugins/Observability/`,
   `TelemetryRegistrationTests`, `CorrelationContextTests`,
   `PluginTypeIdentityTests`. What remains for the Phase 1 checkpoint is the
   evidence, not the plumbing: run the pilot and compare against the existing
   sub-agent research path.
6. Build the read-only `TradingResearchHarness` pilot, including provenance
   tagging of research output and sub-agent resource budgets.
7. Evaluate the Phase 1 checkpoint before investing in skills.
7a. ~~Add a local eval suite for the seams that regress silently.~~ Done —
   `tests/AgentFox.ChannelTests/Evals/`: tool schema as the model receives it
   (reflection-discovered, so a new tool is covered the day it is written), prompt
   assembly, the plan/approval gate decision table, and the HITL bypass table.
   Deliberately local and deterministic — no model call, so it gates on regression
   rather than on model drift. Writing it found three real defects, all fixed:
   a throwing prompt contributor failed the whole LLM call, `PromptContributorRegistry.Add`
   duplicated rather than replaced by id, and the tool-gate decision was unreachable
   for testing inside an orchestrator lambda (now `Planning.ToolGate`, a pure function).
8. Create and test the first PSX research and portfolio-report skills.

## References

- [Build your own claw and agent harness with Microsoft Agent Framework](https://devblogs.microsoft.com/agent-framework/build-your-own-claw-and-agent-harness-with-microsoft-agent-framework/)
- [Agent Harness: Working with your data, safely](https://devblogs.microsoft.com/agent-framework/agent-harness-working-with-your-data-safely/)
- [Agent Harness: Scaling the claw or harness capabilities](https://devblogs.microsoft.com/agent-framework/agent-harness-scaling-the-claw-or-harness-capabilities/)
- [Agent Harness: Making your claw production ready](https://devblogs.microsoft.com/agent-framework/agent-harness-making-your-claw-production-ready/)
  — the source of the observability-first framing above. Its Purview and Foundry
  Hosted Agents sections are deliberately **not** adopted: Purview would create a
  second approval authority beside `HitlManager` (see Phase 3), and Foundry hosting
  would trade AgentFox's single self-contained executable for an Azure dependency.
