# Agent Skills, Remote Registry, and CodeAct — Adoption Plan

Status: proposed
Date: 2026-07-23
Companion to: `HARNESS_AGENT_ROADMAP.md` (this plan implements parts of that roadmap's
"Skills" and "CodeAct and shell" rows, and closes out its "Background agents" row)

---

## 1. Decision

Three tracks, in this order:

1. **Adopt `AgentSkillsProvider`** (Microsoft Agent Framework, already on our
   dependency graph) as AgentFox's single skills mechanism, and **migrate the
   existing custom `SkillRegistry` onto it**.
2. **Add remote skill acquisition** through two sources: a **Git-backed source**
   (covers skills.sh, agentskills.io, and github/awesome-copilot, which are all
   Git-resolved) and the **MCP skills source** (`Microsoft.Agents.AI.Mcp`, the
   MS-native `skill://index.json` path).
3. **Adopt CodeAct** via `Microsoft.Agents.AI.Hyperlight`, replacing the
   unisolated `Runtime/CodeExecution.cs` sandbox.

**Background agents are already implemented in AgentFox** — see §4. No adoption
work is required; the recommendation is explicitly *not* to also turn on the
Harness `BackgroundAgents` provider.

---

## 2. Verified facts

Everything below was verified by **restoring the packages and reflecting over the
assemblies on 2026-07-23**, not from documentation. Several published docs are
stale; where they disagree with this section, this section is right.

### 2.1 Skills — already available, already referenced, not experimental

`Microsoft.Agents.AI` **1.15.0** — which `Directory.Packages.props` already pins —
ships the complete skills stack. **No new package reference is needed for Phase 1.**
No type in it carries `[Experimental]`, so **no MAAI001 suppression is required**.

| Type | Role |
| --- | --- |
| `AgentSkillsProvider : AIContextProvider` | Advertises skills; registers `load_skill`, `read_skill_resource`, `run_skill_script` |
| `AgentSkillsProviderBuilder` | `UseFileSkill(s)`, `UseSkill(s)`, `UseSource`, `UseFilter`, `UseFileScriptRunner`, `UsePromptTemplate`, `UseOptions`, `DisableCaching`, `UseCachingOptions`, `Build` |
| `AgentSkillsSource` (abstract) | `Task<...> GetSkillsAsync(AgentSkillsSourceContext, CancellationToken)` — **the extension point for a remote registry** |
| `AgentFileSkillsSource` | Discovers `SKILL.md` on disk; ctor takes paths + script runner + `AgentFileSkillsSourceOptions` |
| `AgentInlineSkill`, `AgentClassSkill<T>` | Code-defined and class-based skills; `[AgentSkillResource]` / `[AgentSkillScript]` attributes |
| `AggregatingAgentSkillsSource`, `DeduplicatingAgentSkillsSource`, `CachingAgentSkillsSource`, `FilteringAgentSkillsSource`, `DelegatingAgentSkillsSource` | Decorators |
| `AgentSkillFrontmatter` | `Name`, `Description`, `License`, `Compatibility`, `AllowedTools`, `Metadata` + static `ValidateName/ValidateDescription/ValidateCompatibility` |
| `AgentSkillsProviderOptions` | `SkillsInstructionPrompt`, `IncludeDetailedErrors`, `DisableLoadSkillApproval`, `DisableReadSkillResourceApproval`, `DisableRunSkillScriptApproval` |
| `AgentSkillsProvider.ReadOnlyToolsAutoApprovalRule` / `.AllToolsAutoApprovalRule` | Static approval rules |

Two gaps versus the docs:

- **`SubprocessScriptRunner` does not exist in the shipped assembly.** The Learn
  article documents it, but it is sample code, not API. We must supply our own
  runner. This is a benefit, not a problem — see §6.2.
- The script-runner delegate is:
  ```csharp
  Task<object?> AgentFileSkillScriptRunner(
      AgentFileSkill skill,
      AgentFileSkillScript script,
      JsonElement? arguments,
      IServiceProvider? serviceProvider,
      CancellationToken cancellationToken);
  ```

`HarnessAgentOptions` exposes both `AgentSkillsSource` and
`DisableAgentSkillsProvider`, so any source we build also plugs into the Harness
profile in `Harness/HarnessAgentFactory.cs`.

### 2.2 MCP skills — published as alpha, restores clean

`Microsoft.Agents.AI.Mcp` **is** on nuget.org as an alpha line, currently
`1.15.0-alpha.260722.1` — version-aligned with our pinned 1.15.0 family. Verified
restore + reflection:

```
Microsoft.Agents.AI.AgentSkillsProviderBuilderMcpExtensions
   static AgentSkillsProviderBuilder UseMcpSkills(
       AgentSkillsProviderBuilder builder, McpClient client, AgentMcpSkillsSourceOptions options)
Microsoft.Agents.AI.AgentMcpSkillsSourceOptions
   ArchiveSkillsDirectory, ArchiveResourceExtensions, ArchiveResourceSearchDepth,
   ArchiveMaxFileCount, ArchiveMaxSizeBytes, ArchiveMaxUncompressedSizeBytes
```

Notes that shape the design:

- It takes `ModelContextProtocol.Client.McpClient`. AgentFox pins
  `ModelContextProtocol.Core` 1.4.1 and has its own `MCP/MCPClient.cs` — the
  client-type compatibility must be confirmed before committing (§7, Risk R3).
- Two index entry types: `skill-md` (fetched on demand) and `archive`
  (ZIP/TAR downloaded and unpacked).
- **Scripts bundled in archive-type skills are never executed by the framework.**
  That is the correct default and we keep it.
- `alpha` is a lower maturity bar than the `preview` line we already accepted for
  Harness. It gets its own phase (Phase 4) and its own version-bump gate.

### 2.3 CodeAct / Hyperlight — published as preview, restores clean

`Microsoft.Agents.AI.Hyperlight` **1.15.0-preview.260722.1** restores successfully
today. The Learn article's warning that
*"`Hyperlight.HyperlightSandbox.Api` … is not yet published to nuget.org [so] the
project will fail to restore"* **is out of date** — that dependency is published
(0.4.0 and 0.5.0 are both on nuget.org) and a three-package restore of
`Microsoft.Agents.AI` + `.Mcp` + `.Hyperlight` completed cleanly.

Reflected surface:

```
HyperlightCodeActProvider : AIContextProvider, IDisposable   .ctor(HyperlightCodeActProviderOptions)
HyperlightCodeActProviderOptions
   static CreateForWasm(string modulePath), static CreateForJavaScript()
   Backend, ModulePath, HeapSize, StackSize, Tools, ApprovalMode,
   HostInputDirectory, FileMounts, AllowedDomains
HyperlightExecuteCodeFunction : AIFunction, IDisposable      .ctor(HyperlightCodeActProviderOptions)
CodeActApprovalMode { AlwaysRequire, NeverRequire }
FileMount(hostPath, mountPath)      AllowedDomain(target, methods)
```

Runtime prerequisites — these are real deployment constraints, not footnotes:

- Native binaries ship via `Hyperlight.HyperlightSandbox.PInvoke`, which contains
  **only `runtimes/win-x64/native` and `runtimes/linux-x64/native`**. No arm64, no
  macOS.
- Requires hardware virtualization: **Windows Hypervisor Platform (WHP)** on
  Windows, **KVM** on Linux. Sandbox creation fails without it.
- The Wasm backend needs a Python guest module at `HYPERLIGHT_PYTHON_GUEST_PATH`.
  **That guest module is not distributed on nuget.org** — it comes from
  `hyperlight-dev/hyperlight-sandbox` releases. Sourcing and shipping it is a
  prerequisite task, not an afterthought (§7, Risk R5).
- One provider per agent (fixed state key); `IDisposable`.

---

## 3. Current AgentFox skills implementation

`src/Agent/Skills/` — 4,943 lines across 10 files.

| File | Lines | Contents |
| --- | --- | --- |
| `SkillSystem.cs` | 1,550 | `Skill` base, `SkillRegistry`, 8 built-in skills, ~25 `ITool` implementations |
| `ComposioClient.cs` | 767 | Composio.dev REST client |
| `ComposioSkillProvider.cs` | 528 | `ComposioSkillAdapter : Skill, ISkillPlugin`, `ComposioToolWrapper : ITool` |
| `ComposioSkillsExample.cs` | 366 | Sample/demo code |
| `SkillIntelligence.cs` | 400 | ⚠️ **`SystemPromptBuilder` + `SkillFilter` + `AgentRouter` — mostly not skill-specific** |
| `SkillPlugin.cs` | 322 | `ISkillPlugin`, `SkillManifest`, `SkillRegistrationContext` |
| `Skills.cs` | 325 | `SkillMetadata`, `SkillPermission`, `EnhancedSkillExecutionContext` |
| `SkillMetrics.cs` | 280 | `SkillMetricsCollector`, `ResilientSkillExecutor` |
| `SkillContext.cs` | 276 | Execution context types |
| `LoadSkillTool.cs` | 129 | `load_skill` tool (**name collides — see §5.1**) |

Plus three on-disk skill folders (`git/`, `docker/`, `deployment/`) using a
**non-standard** `skill.json` + `skill.md` pair, not `SKILL.md` with frontmatter.

`SkillRegistry` is referenced from 14 files: `Agents/Agent.cs`,
`Agents/AgentMiddleware.cs`, `Agents/AgentOrchestrator.cs`,
`Doctor/Checks/SkillHealthCheck.cs`, `LLM/SystemPromptManager.cs`,
`LLM/SystemPromptQualityCheck.cs`, `Models/AgentModels.cs`,
`Modules/Cli/CliWorker.cs`, `Modules/Web/WebModule.cs`, `Program.cs`, and the
Skills files themselves. That is the migration blast radius.

---

## 4. Background agents — confirmed, already implemented

**Yes, AgentFox already has background agents.** No work needed. Verified:

| Component | Location |
| --- | --- |
| Dedicated priority lane | `Agents/CommandQueue.cs:60` — `CommandLane.Background` |
| Parallel lane policy | `Agents/CommandProcessor.cs:62,78` — `LanePolicy.Parallel(maxConcurrency: 3, pollingDelayMs: 20)` |
| Lane handler registration | `Agents/AgentOrchestrator.cs:869` |
| Spawn tool | `Tools/SpawnBackgroundSubAgentTool.cs` — `spawn_background_subagent` |
| Lifecycle + policy | `Agents/SubAgentManager.cs` — `MaxSpawnDepth`, `MaxConcurrentSubAgents`, `CancelSubAgentAsync`, `ForceCleanupAllAsync` |
| Result aggregation | `Agents/ResultAnnouncementCommand.cs` — local / parent-agent / channel announcements |
| Session routing | `FoxAgent.CurrentSessionKey` ambient value, so web-originated spawns announce back to the right session |

This is a **superset** of the Harness `BackgroundAgents` feature: AgentFox adds
depth/concurrency policy, cancellation, cross-channel announcement, and session
affinity.

**Recommendation: leave `HarnessAgentOptions.BackgroundAgents` unset.** It is
`[EXPERIMENTAL]`, and enabling it would create a second, parallel spawn path that
bypasses `SubAgentManager`'s policy checks and the command-lane accounting. The
current `HarnessAgentFactory` already leaves it off by not setting it; §6.6 adds a
regression test so it stays that way.

---

## 5. Architecture decisions

### 5.1 The `load_skill` name collision is a hard blocker — resolve it first

`AgentSkillsProvider` registers a tool named **`load_skill`**.
`Skills/LoadSkillTool.cs:21` also declares `Name => "load_skill"`, and
`SkillRegistry`'s constructor auto-registers it into `ToolRegistry`
(`SkillSystem.cs:118`).

Both surfaces flow into the same `ChatOptions.Tools` list. Two tools with the same
function name is undefined behaviour at the provider level and will break tool
calling. **Nothing else in this plan can land until this is resolved.**

Resolution: `LoadSkillTool` is deleted as part of Phase 2. For the Phase 1 window
where both exist, `SkillRegistry` must not register it while the provider is on.

### 5.2 AgentFox skills and MS skills are not the same concept

This is the central finding of the migration analysis, and it changes what
"migrate to the MS provider" can mean.

| | AgentFox `Skill` | MS `AgentSkill` |
| --- | --- | --- |
| Primary payload | A **bundle of `ITool` implementations** registered into `ToolRegistry` | **Instructions** (`SKILL.md` body) + resources + scripts |
| Activation | `EnableSkillAsync` mutates the live tool surface | `load_skill` injects text into context |
| Tool surface effect | Adds N callable tools | Adds none |

A 1:1 port is therefore not possible, and pretending otherwise would silently drop
every skill tool. The migration splits each existing skill along that seam:

- **Tools** (`git_commit`, `docker_build`, `rest_call`, …) are already plain
  `ITool`s. They move to direct `ToolRegistry` registration, grouped by a
  `ToolGroup` tag so they can still be enabled/disabled together. Nothing about
  their behaviour or gating changes.
- **Guidance** (`GetSystemPrompts()`, the `skill.md` bodies) becomes `SKILL.md`
  files or `AgentClassSkill<T>` types behind `AgentSkillsProvider`.
- **`SkillMetadata.Capabilities` / `.Tags` / `.ComplexityScore`** map onto
  `AgentSkillFrontmatter.Metadata` (an arbitrary string map, per the
  agentskills.io spec).
- **`SkillPermission.AllowedAgentRoles`** maps onto a `FilteringAgentSkillsSource`
  predicate, which receives `AgentSkillsSourceContext` (`Agent` + `Session`) and
  can therefore role-gate per agent — a capability the current registry has but
  never actually enforces per-session.
- **`CheckHealthAsync`** has no MS equivalent. It stays as a standalone
  `ISkillPrerequisiteCheck` registry keyed by skill name, consumed by
  `Doctor/Checks/SkillHealthCheck.cs`.
- **Composio does not migrate.** `ComposioSkillProvider` produces *tools for 100+
  external services*, not knowledge packages. Modelling it as an `AgentSkill`
  would be a category error. It is reclassified as what it already is — a **tool
  provider** — and registers directly into `ToolRegistry`, dropping only its
  vestigial `Skill` wrapper.

### 5.3 Do not delete `SkillIntelligence.cs`

Despite its name, it contains `SystemPromptBuilder` (used by `SkillRegistry`,
`LLM/SystemPromptManager.cs`, and `LLM/SystemPromptQualityCheck.cs`), plus
`SkillFilter` and `AgentRouter`. Only the skill-capability-discovery parts are in
scope. `SystemPromptBuilder` and `AgentRouter` move to `LLM/` and `Agents/`
respectively, unchanged.

### 5.4 Skills attach to `FoxAgent`, not only to Harness

`AgentSkillsProvider` derives from `AIContextProvider`, and `AgentBuilder.Build()`
already composes a `List<AIContextProvider>` at `Agents/Agent.cs:1808`
(`textSearchProvider`, optional `TodoProvider`). The skills provider is appended
there — exactly the pattern the todo planner already uses. **HarnessAgent is not a
prerequisite for any part of this plan.**

Ordering: append **after** `CompactionProvider`, for the same reason `TodoProvider`
is — skills instructions are re-injected per run from provider state rather than
living in the message list, so compaction cannot summarize them away.

### 5.5 One approval authority

`AgentSkillsProvider` requires approval on all three of its tools by default,
routed through the framework's `ToolApprovalRequestContent` flow. AgentFox's
authority is `HitlManager` via `AgentBuilder.WithToolApprovalGate`, which only
wraps `ToolRegistry` tools through `CreateAgentTool`. Provider tools would take a
**different** approval path — precisely the "alternate approval path" that
`HARNESS_AGENT_ROADMAP.md` principle 2 forbids.

Resolution, via `AgentSkillsProviderOptions`:

- `DisableLoadSkillApproval = true` and `DisableReadSkillResourceApproval = true`
  for **local, first-party** skills. Reading trusted local text is not an approval-
  worthy act, and prompting on it trains users to click through.
- `DisableRunSkillScriptApproval = true`, and approval is instead enforced
  **inside our own script runner** by calling `HitlManager.RequestApprovalAsync`
  directly. This keeps exactly one approval authority and one audit trail.
- For **remote** skills (Phases 3–4) the posture inverts — see §6.3.

---

## 6. Phased plan

### Phase 1 — `AgentSkillsProvider` foundation

**Goal:** file-based `SKILL.md` skills working end to end, gated, with the existing
registry untouched.

- `src/Agent/Skills/AgentSkills/SkillsOptions.cs` — new `Skills` config section
  (§8), disabled by default.
- `src/Agent/Skills/AgentSkills/SkillsProviderFactory.cs` — builds the
  `AgentSkillsProvider` from `AgentSkillsProviderBuilder`. Single point of contact
  with the MS skills API, mirroring the `HarnessAgentFactory` containment pattern.
- `src/Agent/Skills/AgentSkills/GatedSkillScriptRunner.cs` — our
  `AgentFileSkillScriptRunner`. Replaces the non-existent `SubprocessScriptRunner`
  and adds what the docs list as required-for-production: workspace confinement via
  `WorkspaceManager`, wall-clock timeout, output cap, interpreter allow-list,
  `HitlManager` approval, and an audit record per execution.
- Wire into `AgentBuilder`: `WithSkillsProvider(...)`, appended to
  `contextProviders` at `Agents/Agent.cs:1808`.
- Guard the collision: skip `LoadSkillTool` registration when the provider is
  enabled (`SkillSystem.cs:118`).
- Convert `Skills/git|docker|deployment/` from `skill.json` + `skill.md` to spec
  `SKILL.md` with YAML frontmatter.

**Exit:** with `Skills:Enabled=false`, byte-identical behaviour. With it true, the
agent can `load_skill` a local `SKILL.md`, `read_skill_resource`, and
`run_skill_script` under HITL, with no duplicate tool names.

### Phase 2 — Migrate `SkillRegistry` onto the provider

**Goal:** one skills mechanism. This is the largest phase; 14 files reference
`SkillRegistry`.

1. Add `ToolGroup` to `ITool`/`BaseTool`; register the ~25 skill tools directly in
   `ToolRegistry` with their group. Preserves every tool 1:1.
2. Port the 8 built-in skills' `GetSystemPrompts()` bodies into `SKILL.md` files
   (or `AgentClassSkill<T>` where content must be computed).
3. Port `SkillPermission` → `FilteringAgentSkillsSource` predicate.
4. Extract `ISkillPrerequisiteCheck` from `Skill.CheckHealthAsync`; repoint
   `Doctor/Checks/SkillHealthCheck.cs`.
5. Move `SystemPromptBuilder` → `LLM/`, `AgentRouter` + `SkillFilter` → `Agents/`
   (§5.3).
6. Reclassify Composio as a tool provider (§5.2); delete `ComposioSkillAdapter`,
   keep `ComposioClient` and `ComposioToolWrapper`.
7. Delete `LoadSkillTool.cs`, the `Skill`/`SkillRegistry`/`ISkillPlugin` types,
   `SkillMetrics.cs`, `SkillContext.cs`, `ComposioSkillsExample.cs`.
8. Update the `skills` REPL command (`Modules/Cli/CliWorker.cs`) and the web
   surface (`Modules/Web/WebModule.cs`) to enumerate from the new source.

**Exit:** `SkillRegistry` is gone; every previously available tool is still
callable; `dotnet build` clean; skills-related tests green.

> **Scope note.** This phase is a breaking internal refactor with no user-visible
> feature gain — its value is removing a parallel mechanism. It is worth doing
> *because* Phases 3–5 all attach to the MS provider, but it is also the phase
> most reasonable to defer if delivery pressure appears. Phases 3 and 4 depend
> only on Phase 1, not on Phase 2.

### Phase 3 — Git-backed remote registry

**Goal:** install skills from skills.sh / agentskills.io / awesome-copilot.

All three directories resolve to Git repositories — skills.sh's CLI
(`vercel-labs/skills`) accepts `owner/repo`, full GitHub/GitLab URLs, `git@` URLs,
tree-subpaths, and local paths; awesome-copilot uses `gh skills install
github/awesome-copilot <name>`. One Git-backed implementation covers the set.

- `src/Agent/Skills/AgentSkills/GitSkillsSource.cs : AgentSkillsSource` —
  materializes a pinned checkout into `skills/_remote/<host>/<owner>/<repo>@<sha>/`
  and delegates discovery to an inner `AgentFileSkillsSource`.
- `src/Agent/Skills/AgentSkills/InstallSkillTool.cs` — `install_skill` /
  `list_remote_skills` / `remove_skill`, HITL-gated, writing a lockfile
  (`skills/skills.lock.json`) recording source URL, resolved commit SHA, content
  hash, and install time.
- Discovery walks `skills/<name>/SKILL.md` one level deep plus one extra level for
  catalog layouts — matching the convention those directories already use.
- Validate every fetched skill with `AgentSkillFrontmatter.ValidateName` /
  `ValidateDescription` / `ValidateCompatibility` before admitting it.

Security posture for remote skills (§6.3 applies in full):

- **Pin by resolved commit SHA**, never a floating branch. Re-resolution is an
  explicit, approved action.
- **`run_skill_script` is refused for remote-origin skills by default**
  (`Skills:Remote:AllowScripts=false`). This mirrors the framework's own stance
  that archive-type MCP skills never execute scripts.
- Caps on file count, per-file size, and total unpacked size.
- Domain allow-list for source hosts.
- `SKILL.md` bodies are untrusted, attacker-controlled instruction text. They are
  wrapped in an untrusted-content delimiter and the skills instruction prompt
  (`AgentSkillsProviderOptions.SkillsInstructionPrompt`) states that skill content
  is reference material, never an instruction to escalate privilege or bypass a
  gate.

### Phase 4 — MCP skills source

**Goal:** the MS-native remote path, for centrally managed / Foundry skills.

- Add `Microsoft.Agents.AI.Mcp` `1.15.0-alpha.260722.1` to
  `Directory.Packages.props`, under the same deliberate-bump policy that governs
  `Microsoft.Agents.AI.Harness` — extended to note that **alpha is a lower bar than
  preview** and this reference must be re-reviewed at every family bump.
- **Spike first:** confirm the `McpClient` type that `UseMcpSkills` expects is
  compatible with what AgentFox's `MCP/MCPClient.cs` holds (we pin
  `ModelContextProtocol.Core` 1.4.1). If not, this phase needs an adapter or a
  second client instance. Do not start the phase until this is answered.
- Extend the `MCP.Servers` config with a per-server `ProvidesSkills` flag; add
  matching sources via `.UseMcpSkills(client, options)`.
- Set `AgentMcpSkillsSourceOptions` conservatively: `ArchiveSkillsDirectory` under
  the workspace, `ArchiveMaxFileCount`, `ArchiveMaxSizeBytes`,
  `ArchiveMaxUncompressedSizeBytes` all explicitly set rather than defaulted.
- Wrap in `CachingAgentSkillsSource` with a `RefreshInterval` — MCP skill sets
  change over process lifetime, unlike local files.

### Phase 5 — CodeAct via Hyperlight

**Goal:** replace `Runtime/CodeExecution.cs` with a genuinely isolated sandbox.

Today `CodeSandbox` writes a temp `.csproj` and runs `dotnet run`, or writes a
`.py` and runs `python`, **in-process-adjacent with no isolation** — full host
filesystem, full network, host credentials. The `execute_code` tool is a
significant standing risk; Hyperlight is a real remediation, not a nice-to-have.

- **Prerequisite spike** (blocking): obtain the Python guest module from
  `hyperlight-dev/hyperlight-sandbox`, confirm WHP is available on target Windows
  hosts, and confirm sandbox creation succeeds on win-x64. If the guest module
  cannot be redistributed acceptably, fall back to `CreateForJavaScript()`.
- Add `Microsoft.Agents.AI.Hyperlight` `1.15.0-preview.260722.1` centrally.
- `src/Agent/Runtime/CodeAct/CodeActProviderFactory.cs` — sole contact point with
  the Hyperlight API.
- **The critical security requirement:** tools registered on
  `HyperlightCodeActProviderOptions.Tools` are reachable from inside the sandbox
  via `call_tool(...)`, but **`call_tool` executes them in the host process** with
  full host filesystem, network, and credentials. Every such tool must therefore
  be constructed to route through `AgentBuilder.ExecuteThroughGatewayAsync`, so the
  plan gate, HITL, lifecycle hooks, and experience learning all still apply. This
  is the same contract `CreateGatewayTools()` already enforces for the Harness
  bridge, and it is non-negotiable — a raw `AIFunction` on that list is a complete
  bypass of AgentFox policy.
- Compounding that: **approval applies to the whole `execute_code` block, not to
  each `call_tool` inside it.** So only read-only, deterministic tools go on the
  provider list. Anything side-effecting stays a direct agent tool with its own
  per-invocation gate.
- Set `ApprovalMode = CodeActApprovalMode.AlwaysRequire` for the initial rollout.
- Constrain the sandbox explicitly: `HostInputDirectory` scoped to the
  `WorkspaceManager` workspace, `FileMounts` empty by default, `AllowedDomains`
  empty by default (deny-all outbound).
- Gate behind `CodeAct:Enabled=false`; `Runtime/CodeExecution.cs` remains the
  fallback until CodeAct is proven, then `execute_code` is **removed**, not left
  as a bypass.
- Honour `IDisposable` and the one-provider-per-agent constraint in `FoxAgent`
  teardown.

### Phase 6 — Background agents: confirm and lock in

No implementation. Two small tasks:

- Add a regression test asserting `HarnessAgentOptions.BackgroundAgents` is left
  unset by `HarnessAgentFactory`, with a comment explaining that `SubAgentManager`
  is the single spawn authority (§4).
- Document the existing background-agent capability in `README.md` /
  `src/CLAUDE.md`, which currently under-describe it.

---

## 7. Risks

| # | Risk | Mitigation |
| --- | --- | --- |
| R1 | `load_skill` name collision breaks tool calling | §5.1 — resolve in Phase 1 before anything else; add a startup assertion for duplicate tool names |
| R2 | Phase 2 is a large breaking refactor across 14 files with no feature gain | Phases 3–4 depend only on Phase 1; Phase 2 can slip without blocking them |
| R3 | `UseMcpSkills` expects an `McpClient` type incompatible with our `ModelContextProtocol.Core` 1.4.1 pin | Blocking spike at the top of Phase 4 |
| R4 | `Microsoft.Agents.AI.Mcp` is **alpha** — weaker stability guarantee than the preview Harness line we already took | Own phase, own bump gate, isolated behind our own source abstraction |
| R5 | Hyperlight Python guest module is not on nuget.org and must be sourced from GitHub releases | Blocking spike at the top of Phase 5; `CreateForJavaScript()` fallback |
| R6 | Hyperlight ships native bits for **win-x64 and linux-x64 only**, and requires WHP/KVM | Feature-flag off by default; detect and degrade to a clear error, never a silent unsandboxed fallback |
| R7 | Remote `SKILL.md` content is attacker-controlled instruction text (prompt injection) | SHA pinning, no remote script execution by default, untrusted-content framing, frontmatter validation (§6.3) |
| R8 | Provider-owned CodeAct tools bypass the AgentFox gateway | Gateway-routing requirement in Phase 5 is a hard review gate, with a test proving a sandbox `call_tool` still hits the plan gate |
| R9 | `AgentSkillsProvider` may add state-bag keys that interact with session persistence | `FoxAgent.FilterToTodoState` already allowlists only `TodoProvider`, so extra keys are dropped safely — but verify, and confirm `SessionManager.SidecarSuffixes` needs no new entry |

---

## 8. Configuration

```jsonc
{
  "Skills": {
    "Enabled": false,
    "LocalPaths": [ "skills" ],
    "SearchDepth": 2,
    "DisableCaching": false,
    "Scripts": {
      "Enabled": false,
      "AllowedInterpreters": [ "python", "pwsh" ],
      "TimeoutSeconds": 60,
      "MaxOutputBytes": 262144,
      "RequireApproval": true
    },
    "Remote": {
      "Enabled": false,
      "AllowedHosts": [ "github.com", "gitlab.com" ],
      "AllowScripts": false,
      "MaxFileCount": 50,
      "MaxTotalBytes": 2097152,
      "InstallDirectory": "skills/_remote"
    },
    "Mcp": { "Enabled": false, "RefreshIntervalMinutes": 15 }
  },
  "CodeAct": {
    "Enabled": false,
    "Backend": "Wasm",
    "GuestModulePath": null,
    "ApprovalMode": "AlwaysRequire",
    "AllowedDomains": [],
    "FileMounts": []
  }
}
```

---

## 9. Testing

Added to `tests/AgentFox.ChannelTests/`, following the existing MSTest pattern.

- `SkillsProviderTests` — provider builds; disabled-by-default is a true no-op;
  **no duplicate tool name** across `ToolRegistry` and the provider.
- `SkillScriptRunnerTests` — script outside the workspace refused; timeout
  enforced; output truncated; approval denial blocks execution; audit record written.
- `RemoteSkillsSourceTests` — floating branch refused; SHA pin honoured; size and
  file-count caps enforced; invalid frontmatter rejected; `run_skill_script`
  refused for a remote-origin skill when `AllowScripts=false`.
- `ToolGroupMigrationTests` (Phase 2) — every tool previously reachable via
  `SkillRegistry.EnableSkillAsync` is still resolvable from `ToolRegistry`.
- `CodeActGatewayTests` (Phase 5) — a tool invoked via `call_tool` inside the
  sandbox still passes through `ExecuteThroughGatewayAsync` and is blocked by a
  denying plan gate.
- `HarnessBackgroundAgentsTests` (Phase 6) — `BackgroundAgents` is left unset.

Build/run notes carried over from `HARNESS_AGENT_ROADMAP.md`: the solution is
`src/AgentFox.sln`; `dotnet test` fails on the .NET 10 SDK, so run the built
`tests/.../bin/<cfg>/net10.0/AgentFox.ChannelTests.exe`; prefer Release because
Debug `bin` is often locked by a running AgentFox instance.

---

## 10. Open questions

1. **Phase 2 sequencing.** Phases 3–4 need only Phase 1. Do we take the Phase 2
   refactor before shipping remote skills, or ship remote skills first and migrate
   after?
2. **Composio's future.** Reclassifying it as a tool provider is the honest
   modelling, but it also raises whether ~1,300 lines of Composio code still earns
   its place now that MCP covers much of the same ground.
3. **Skill trust tiers.** Is a two-tier model (first-party local = scripts allowed;
   remote = no scripts) sufficient, or do we need a per-skill trust grant?
4. **CodeAct vs. `execute_code`.** Confirm the intent is to *remove*
   `Runtime/CodeExecution.cs` once CodeAct lands, rather than keep it as a fallback
   — keeping it would preserve the unsandboxed bypass this phase exists to close.

---

## 11. References

- [Agent Skills — Microsoft Learn (C#)](https://learn.microsoft.com/en-us/agent-framework/agents/skills?pivots=programming-language-csharp)
- [Agent Skills for .NET is now released](https://devblogs.microsoft.com/agent-framework/agent-skills-for-net-is-now-released/)
- [Give your agents domain expertise with Agent Skills](https://devblogs.microsoft.com/agent-framework/give-your-agents-domain-expertise-with-agent-skills-in-microsoft-agent-framework/)
- [Agent Harness — scaling the claw](https://devblogs.microsoft.com/agent-framework/agent-harness-scaling-the-claw-or-harness-capabilities/)
- [Hyperlight CodeAct — Microsoft Learn](https://learn.microsoft.com/en-us/agent-framework/integrations/hyperlight?pivots=programming-language-csharp)
  (note: its "not yet published to nuget.org" warning is stale — see §2.3)
- [Agent Skills specification — agentskills.io](https://agentskills.io/specification)
- [github/awesome-copilot — skills](https://github.com/github/awesome-copilot/blob/main/docs/README.skills.md)
- [skills.sh](https://www.skills.sh/) · [vercel-labs/skills CLI](https://github.com/vercel-labs/skills)
- `HARNESS_AGENT_ROADMAP.md` — governing principles and preview-bump policy
