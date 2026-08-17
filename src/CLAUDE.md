# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build AgentFox.sln

# Run interactive REPL
dotnet run --project Agent/

# Run single task (command-line mode)
dotnet run --project Agent/ -- "your task here"
```

Interactive REPL commands: `help`, `status`, `history`, `memory`, `tools`, `skills`, `exit`

## Architecture Overview

**AgentFox** is a multi-agent framework for .NET 8. Single project (`Agent/`) inside `AgentFox.sln`.

### Core Execution Model

The agent uses a **lane-based command queue** (inspired by OpenClaw) with four priority levels:

```
Main > Subagent > Tool > Background
```

- `CommandQueue` — thread-safe queue with one `ConcurrentQueue<ICommand>` per lane
- `CommandProcessor` — dequeues and dispatches commands via registered lane handlers
- `ICommand` — base interface with `RunId`, `SessionKey`, `Lane`, `Priority`

### FoxAgent (`Agents/Agent.cs`)

The top-level orchestrator. Built via `AgentBuilder` in `Program.cs`:

```csharp
var agent = new AgentBuilder(toolRegistry)
    .WithName("AgentFox")
    .WithSystemPrompt(systemPrompt)
    .WithMemory(memory)
    .WithSkillsRegistry(skillRegistry)
    .WithMCPClient(mcpClient)
    .WithChatClient(chatClient)
    .Build();
```

### Sub-Agent Management (`Agents/SubAgentManager.cs`)

Spawned via `SpawnSubAgentTool` or `SpawnBackgroundSubAgentTool`. The manager enforces `MaxSpawnDepth` and `MaxConcurrentSubAgents` policies, routes results back via `ResultAnnouncementCommand` callbacks.

A background sub-agent's result reaches the user along one path: completion → result callback → `ResultAnnouncementCommand` on the Main lane → a parent-session agent turn → `PendingNotificationStore`, which web clients drain by polling `GET /chat/pending/{conversationId}`. The parent turn is an enrichment only; if it fails or returns nothing, the raw result is queued anyway, so a finished sub-agent is never silently lost.

The announcement queues **two** notifications, tagged via `PendingNotificationKind`: the sub-agent's raw output (`subagent_result`) and the parent turn's reply (`agent_response`), or a `notice` explaining why no reply exists. This mirrors what the console prints — queueing a single collapsed string previously gave the web UI strictly less than the terminal, with the agent's final response silently dropped. The web client renders `agent_response` as an ordinary assistant bubble and the other kinds with the "background result" badge.

That parent turn also **streams live** over `ConversationEventBus` → `GET /chat/events/{conversationId}`, a long-lived SSE stream the web client holds open per conversation. The turn has no HTTP request of its own, so `AgentCommand.Streaming` is wired to publish `background_token` / `background_reasoning` / `background_status` / `background_tool_activity` between `background_turn_started` and `background_turn_done`. The bus is best-effort (bounded per-subscriber buffers, drop-oldest, publishing never blocks the turn); `PendingNotificationStore` stays the durable path. Every event carries a `runKey` matching the notification's `subAgentRunId`, so a client that rendered a turn live discards the polled duplicate. The client uses `fetch` rather than `EventSource` because the `/api` group is authorized as a whole and `EventSource` cannot send the management API key header. `CheckSubAgentStatusTool` (`check_subagent_status`) exposes running and recently-finished runs, since live task records are purged a few seconds after completion.

### Scheduling (`Runtime/Scheduling.cs`)

`CronScheduler` and `HeartbeatManager` both run a `System.Timers.Timer` (60s default) and dispatch each due entry as a full agent turn on the Background lane.

The invariant that matters: a due job is **claimed before it runs**, not after. `ClaimDueJobs` marks the job `IsRunning` and advances `NextExecution` inside the jobs lock, then each job is dispatched independently (`_ = ExecuteJobAsync(job)`) and releases its claim in a `finally`. Advancing the schedule after the await instead is what produced the production runaway: a task takes minutes while the timer keeps ticking, so every intervening tick still saw the job as due and started another complete, independent run of it — one job fired 81 times in 81 minutes, each copy doing its own research and its own `notify_user` delivery. `IsRunning` is transient and never persisted; a wedged job therefore goes quiet rather than re-firing, which is the safe failure.

`cron.md` is the persisted schedule, with `Last Run` / `Next Run` columns. A persisted **future** occurrence is honoured on load so a restart cannot re-run an occurrence that already fired; a past one is skipped rather than fired on startup. Task cells are escaped (`\n`, `\|`, `\\`) because model-authored tasks routinely contain newlines and pipes — written raw they broke the table and the reader truncated the task at the first newline. Job and beat names are compared case-insensitively, and `ManageCronTool` rejects a name that differs from an existing one only by case or punctuation, pointing the caller at `update` instead. Without an `update` operation the model's response to "already exists" was to retry under a new name, which is how two jobs ended up delivering the same report.

`notify_user` carries two guards for the same class of problem: it refuses sends from a sub-agent session (results belong to the parent, which decides what the user sees — `Tools:SubAgentNotify` opts back in), and it suppresses a near-identical message re-sent within the same session inside `Tools:DuplicateNotifyWindowSeconds`. Similarity is word-shingle Jaccard, so suppression is proportional to message length: a long report with a few refreshed figures is caught, two short status lines differing in a number are not.

### Memory System (`Memory/`)

Three-tier design:
- `ShortTermMemory` — in-memory FIFO ring buffer (default 100 entries)
- `LongTermMemory` — JSON file-based persistence with importance-weighted search
- `HybridMemory` — wraps both; auto-consolidates entries above an importance threshold

`MemoryType` enum covers: `Conversation`, `ToolExecution`, `SubAgentResult`, `Observation`, `Fact`, `UserPreference`.

### Tools System (`Tools/`)

`ToolRegistry` is the central registry. `ITool` requires `Name`, `Description`, `Parameters`, and `ExecuteAsync(Dictionary<string, object?>)`. Built-in tools include file I/O, shell execution, web search/fetch, memory CRUD, calculator, and sub-agent spawning. Extend by registering any `ITool` implementation.

### Skills System (`Skills/`)

Skills are composable capability bundles extending `Skill` (abstract). Each returns tools and system prompt fragments. `SkillRegistry` handles lifecycle, dependencies, and permissions. Composio integration (`ComposioSkillProvider`) gives access to 100+ external services (GitHub, Slack, Jira, etc.) via `COMPOSIO_API_KEY`.

### Channel Integration (`Channels/`)

Extends `Channel` (abstract) for messaging platforms (Discord, WhatsApp, Telegram). `ChannelMessageGateway` bridges incoming messages into the command lane system with concurrency limits (default: 10 concurrent) and timeout management (default: 5 minutes).

Outbound notifications are **topic-routed**. A publisher addresses a subject (`trading.order.accepted`, `hitl.approval`) and each channel subscribes with filters using NATS semantics: `*` matches exactly one segment, `>` matches one or more trailing segments and must be last. So `trading.*` matches `trading.order` but not `trading.order.accepted`; `trading.>` matches both; a bare `>` is the catch-all. The alternative reading — `*` meaning "everything below" — is only decidable while topics are two segments deep, and by the time a third appears every subscription in every config has been written against the old meaning.

`ChannelManager.ResolveRecipients` is the single chokepoint every fan-out passes through, and it carries three deliberate policies. An **unaddressed** notification (null topic) reaches every connected channel — that is what every publisher predating topics sends, and none of them may lose delivery. An unmatched topic is **dropped with a warning**, because the failure mode of subject routing is silence: a filter one character off from the topic looks correct in config forever, and nothing throws or retries. And a topic that `NotificationTopics.IsMandatory` — currently `hitl.>` — **falls back to every channel** rather than matching nothing: those are questions whose reply comes back through `ChannelManager.HandleMessage`, so filtering them to zero recipients does not drop a message, it deadlocks the turn waiting on the answer.

Publishers use constants from `NotificationTopics` (host) and `TradingTopics` (trading plugin), which also register descriptions so the `notify_user` tool and the channels page can show operators what actually exists to subscribe to. `TopicSubscription` defaults to the catch-all whenever a spec is missing *or* entirely unparseable — a typo makes a channel noisier than intended, never silent.

Subscriptions are editable at runtime from three surfaces: `manage_channel action=subscribe` (agent chat), `PUT /channels/{id}/subscriptions` (the channels page's inline editor), and appsettings on restart. All writes funnel through `ChannelConfigStore`, the single locked writer for the `Channels` array — two surfaces each running their own read-modify-write on that file is how an entry gets clobbered. Both paths update the live channel first and persist second, then report `persisted: false` rather than swallowing a failed write: "it worked but reverts on restart" is the one outcome an operator has to act on. The page's 5s poll pauses while its editor is open, or it would overwrite a half-typed filter.

Channel identity is the prerequisite that makes any of it storable. Providers mint their own ids and several are not unique per instance (Telegram hardcodes `"telegram"`), so a second bot used to overwrite the first in the manager's dictionary. Config entries take an optional `Name` that pins the id; `AddChannel` suffixes (`telegram#2`) and warns when one collides anyway. `Subscribe` accepts a comma-separated string or a JSON array — the array form needs explicit handling in `ChannelConfiguration.NormalizeSubscriptions` because `IConfiguration` flattens it into a null-valued parent section that the config-dictionary pass drops on the floor.

### LLM Providers (`LLM/`)

`LLMFactory` abstracts: `OpenAI`, `Anthropic`, `Ollama`, `OpenRouter`, `GoogleGenAI`. Provider is selected by `appsettings.json` `LLM.Provider` key. `SystemPromptBuilder` uses a fluent API (`.WithPersona()`, `.WithTools()`, `.WithConstraints()`, etc.).

### MCP Client (`MCP/MCPClient.cs`)

Connects to Model Context Protocol servers defined in config. Dynamically registers discovered server tools into `ToolRegistry`.

## Configuration

Two appsettings files (environment controlled by `DOTNET_ENVIRONMENT`):
- `appsettings.json` — Production (defaults to Ollama `phi4-mini`)
- `appsettings.Development.json` — Dev overrides (OpenAI `qwen0.8b`)

Key config sections: `LLM`, `Models` (CheapModel/FastModel/ReasoningModel), `Compaction`, `Composio`, `MCP.Servers`, `Workspaces`.

Required environment variables for providers: `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `COMPOSIO_API_KEY`.

## Current Branch State

The `memory_store` branch has uncommitted changes in `Agent/Agents/Agent.cs` and `Agent/Memory/MarkdownStorage.cs` — likely adding markdown-based memory persistence to complement the existing JSON-based `LongTermMemory`.
