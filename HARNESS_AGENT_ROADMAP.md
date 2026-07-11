# HarnessAgent Adoption Roadmap

## Decision

Adopt Microsoft Agent Framework's `HarnessAgent` incrementally, as an optional
execution profile behind AgentFox's existing agent facade. Do not replace the
current main-agent or trading-execution architecture wholesale.

AgentFox already references `Microsoft.Agents.AI.Harness`
(`1.11.1-preview.260625.1`) in the host, shared plugin project, and
`TradingAgent`. The package is not currently instantiated with
`AsHarnessAgent`.

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
| Observability | Plugin lifecycle hooks, trading ledger/events | Add OpenTelemetry traces that correlate with existing audit records. |
| CodeAct and shell | Existing tool system and workspace enforcement | Defer for TradingAgent; use only in tightly sandboxed non-trading profiles. |

## Phased Roadmap

### Phase 0 — Foundation and Safety Contract

**Goal:** Make Harness adoption reversible and define the non-negotiable
security boundaries.

- Add a feature-flagged `Harness` configuration section. Keep it disabled by
  default.
- Introduce a small `HarnessAgentFactory`/adapter that returns the existing
  `AIAgent` abstraction while isolating `AsHarnessAgent` and preview API use.
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

- Add OpenTelemetry instrumentation for agent runs, model requests, tool calls,
  approval decisions, broker submissions, and reconciliation runs.
- Propagate a correlation ID through channel message, specialist delegation,
  proposal, execution, and ledger-event records.
- Create a dedicated `TradingResearchHarness` specialist with only:
  - market/news and portfolio-read tools;
  - a read-only portfolio/report workspace;
  - no broker credentials;
  - no `place_order` or `place_orders` tool;
  - no shell and no CodeAct.
- Use isolated, minimal background agents for per-symbol research. Their only
  output should be factual, attributable research returned to the main
  specialist for synthesis.

**Exit criteria:** Users can request multi-symbol research and report generation
with a trace linking every result to the initiating session, while the harness
profile has no route to broker execution.

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
  - set compaction limits per configured model;
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

1. Add `HarnessOptions` and a disabled-by-default profile selector.
2. Implement and test the AgentFox-to-Harness tool bridge.
3. Add trace/correlation IDs through trading proposal and execution flows.
4. Build the read-only `TradingResearchHarness` pilot.
5. Create and test the first PSX research and portfolio-report skills.
6. Design immutable approval-intent records before exposing any Harness
   approval convenience feature.

## References

- [Build your own claw and agent harness with Microsoft Agent Framework](https://devblogs.microsoft.com/agent-framework/build-your-own-claw-and-agent-harness-with-microsoft-agent-framework/)
- [Agent Harness: Working with your data, safely](https://devblogs.microsoft.com/agent-framework/agent-harness-working-with-your-data-safely/)
- [Agent Harness: Scaling the claw or harness capabilities](https://devblogs.microsoft.com/agent-framework/agent-harness-scaling-the-claw-or-harness-capabilities/)
