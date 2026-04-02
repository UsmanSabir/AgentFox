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
