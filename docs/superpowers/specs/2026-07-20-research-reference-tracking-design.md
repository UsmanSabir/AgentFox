# Research Reference Tracking for TradingAgent

**Date:** 2026-07-20
**Status:** Approved design, pending implementation plan

## Problem

The TradingAgent plugin's `research_stock` tool fetches data from the web (Google
News RSS for company/market headlines, the PSX data portal for price/index
timeseries) via `HttpClient`. Today the source URLs are **discarded**:

- `NewsHeadline` (`PsxDataClient.cs:31`) has no URL field; the RSS `<item>` `<link>`
  element is parsed away in `GetNewsAsync` (`PsxDataClient.cs:223-232`).
- The PSX portal request URLs are local variables inside `FetchSeriesAsync`
  (`PsxDataClient.cs:149-152`) and never surfaced.

As a result, a user reading the assistant's chat reply cannot see which web sources
were consulted for the research. We want to capture those URLs and display them as a
"Sources" section under the assistant's chat message, and have them persist when an
old conversation is reopened.

## Goals

1. A **general-purpose reference collector** any research fetch can register URLs
   into — not TradingAgent-specific — so future data sources appear automatically
   once they call the register API.
2. Show captured references as a **"Sources" list** beneath the assistant reply in
   the web chat UI.
3. **Persist** references so they reappear when an old conversation is reopened.

## Non-Goals

- Capturing URLs from tools that run out of the turn's async flow (background lane,
  sub-agents on other lanes). Documented v1 limitation.
- Automatic URL scraping via regex over tool output. Collection is by **explicit
  registration**, not text scraping.
- Surfacing references on the plugin-observability page or the trading page (the chat
  message is the chosen display surface).

## Architecture

An **ambient, per-turn reference collector**. Any tool can register URLs with zero
per-tool plumbing on the collection side; the host turn opens a scope, tools write to
the ambient static, and the host drains it when the turn finishes.

The collector types live in `src/AgentFox.Plugins/` — the shared library that
`TradingAgent.csproj` (and the host) both reference, and which `PluginLoadContext`
delegates to the host's default load context. This guarantees the host and every
plugin resolve **one** `ResearchReferenceScope` type and share its `AsyncLocal`
static, so a plugin tool writing to `Current` is seen by the host turn that opened the
scope.

### Data flow

```
research_stock tool  ──Add(url, title, source)──▶  ResearchReferenceScope.Current
FoxAgent.ProcessAsync: using Begin() … run turn … Snapshot() ──▶ AgentResult.References
      ──▶ FoxAgentService ──▶ ChatResponse.References        (sync  POST /chat)
                            └▶ SSE `done` event payload      (POST /chat/stream)
      ──▶ persisted as a [references]{json} line in the conversation .md file
frontend: ChatMessage.references ──▶ rendered "Sources" list under the assistant reply
      ──▶ on reload, re-parsed from the .md and re-attached to the assistant message
```

## Components

### 1. Shared collector — `src/AgentFox.Plugins/Research/`

**`ResearchReference`** — immutable record:

```csharp
public sealed record ResearchReference(string Url, string? Title = null, string? Source = null);
```

**`ResearchReferenceScope`** — ambient per-turn scope:

- Backed by `private static readonly AsyncLocal<ResearchReferenceScope?> _current`.
- `public static ResearchReferenceScope? Current => _current.Value;`
- `public static IDisposable Begin()` — sets `_current.Value` to a fresh scope and
  restores the previous value on `Dispose()` (supports nesting).
- Instance API: `void Add(string url, string? title = null, string? source = null)`,
  `void AddRange(IEnumerable<ResearchReference>)`, `IReadOnlyList<ResearchReference> Snapshot()`.
- Thread-safe (lock around the backing list).
- **Dedup by normalized URL** (trim, lowercase scheme+host, drop trailing slash);
  first occurrence wins for Title/Source.
- **Malformed URLs skipped**: `Add` no-ops if `Uri.TryCreate(url, Absolute)` fails or
  the scheme is not http/https.

### 2. Capture URLs in TradingAgent

- **`Research/PsxDataClient.cs`**
  - Add `string? Url` to the `NewsHeadline` record (line 31).
  - In `GetNewsAsync` (lines 214-232), extract the RSS `<item><link>` text and populate
    `NewsHeadline.Url`.
  - Expose the PSX portal endpoint URLs used by `FetchSeriesAsync` (the
    `timeseries/eod/{symbol}` / `timeseries/int/{symbol}` full URLs, and the KSE-100
    index URL) on `StockResearchData` (record at line 34) so the tool can register them.
- **`Tools/ResearchStockTool.cs`**
  - In `ExecuteInternalAsync` (line 96), after `GatherAsync`, register into
    `ResearchReferenceScope.Current` (null-safe): each news headline's `Url` (Title =
    headline title, Source = headline source) and each PSX portal endpoint (Title =
    e.g. "PSX price data (SYMBOL)", Source = "PSX Data Portal").

### 3. Turn integration — `src/Agent/Agents/Agent.cs`

- In `FoxAgent.ProcessAsync` (lines 188-333), wrap the agent run in
  `using (ResearchReferenceScope.Begin())`.
- After the response text is finalized (both streaming and non-streaming paths),
  call `ResearchReferenceScope.Current!.Snapshot()` and assign to a new field on the
  result.
- **`src/Agent/Models/AgentModels.cs`** (`AgentResult`, lines 141-150): add
  `public List<ResearchReference> References { get; set; } = new();`.

### 4. Transport

- **`src/Agent/Agents/FoxAgentHolder.cs`** (`FoxAgentService`, lines 44-71): surface
  references from the `AgentResult` alongside the output text. `RunAsync`/`StreamAsync`
  currently return only the string output; extend the return path so references reach
  the endpoint (e.g. return the `AgentResult`, or an out/tuple carrying references).
- **`src/AgentFox.Plugins/Models/ChatRequest.cs`** (`ChatResponse`, lines 17-30): add
  `List<ResearchReference> References`.
- **`src/Agent/Modules/Web/WebModule.cs`**
  - Sync `POST /chat` (lines 64-99): populate `ChatResponse.References` (line 84-89).
  - SSE `POST /chat/stream` (lines 107-167): include a `references` array in the
    terminal `done` event payload (lines 145-151).

### 5. Persistence — `src/Agent/Memory/MarkdownSessionStore.cs`

References are persisted in a **per-conversation sidecar file** `{session}.md.refs.jsonl`
(one JSON object per line: `{"i":<assistantIndex>,"items":[{"url","title","source"}]}`),
NOT inline in the `.md`. Rationale: the `.md` is re-parsed into the `ChatMessage`
list that feeds the LLM as history; interleaving a `[references]` line there would
either pollute the reconstructed assistant text or force changes to the shared
parse path (`ParseFile`/`FlushMessage`). A sidecar keeps references completely out
of the LLM-history path and mirrors the existing `.md.pending` sidecar precedent.

- `assistantIndex` = the 0-based position of the assistant reply among the
  user/assistant **non-empty-text** messages the conversation projects (the same set
  `GetConversationMessages` returns). Storing the index (rather than relying on line
  order) keeps alignment correct even for turns that produced no references (no line
  written → a gap, tolerated on read).
- New method `PersistAssistantReferences(conversationId, IReadOnlyList<ResearchReference>)`:
  no-op when the list is empty; otherwise computes `assistantIndex` from the in-memory
  message list and appends one line to the sidecar. Called from `ProcessAsync` right
  after `SaveSession` (`Agent.cs:303`).
- `ConversationMessageSnapshot` (line 496): add init-only `IReadOnlyList<ResearchReference>
  References` (default empty) — keeps the existing positional constructor unchanged.
- `GetConversationMessages` (lines 216-233): after building the role/content snapshots,
  load the sidecar into `Dictionary<int,List<ResearchReference>>` and attach by
  assistant index.
- `DeleteSession` (lines 235-243): also delete the sidecar file.

### 6. Frontend

- **Types**
  - `src/frontend/src/lib/api.ts`: add `references?: ReferenceItem[]` to `ChatResponse`
    (lines 12-17) and `ConversationMessage` (lines 115-118); add `references` to the
    `StreamEvent` `done` variant (line 466). Define `interface ReferenceItem { url:
    string; title?: string; source?: string }`.
  - `src/frontend/src/lib/stores.ts`: add `references?: ReferenceItem[]` to
    `ChatMessage` (lines 15-23); a helper to attach references when finalizing a message
    (alongside `finalizeMessage`, lines 64-68).
- **Render** — `src/frontend/src/routes/chat/+page.svelte`
  - Insert a "Sources" block between the content `<div>` (closes line 312) and the copy
    button (line 314), guarded `msg.role === 'assistant' && !msg.streaming && !msg.error
    && msg.references?.length`.
  - Render each reference as `<a href={ref.url} target="_blank" rel="noopener
    noreferrer">{ref.title ?? ref.url}</a>`, with the source shown as secondary text.
- **Populate**
  - Streaming `done` branch (lines 106-109): attach `event.references`.
  - Sync/specialist path (lines 116-124): attach `response.references`.
  - Session reload mapping (`openSession`, lines 166-171): map persisted references.

## Error Handling & Edge Cases

- No references collected → `References` is empty; the frontend hides the Sources
  section.
- Duplicate URLs collapsed by normalized URL.
- Non-http(s) / malformed URLs skipped at registration.
- `ResearchReferenceScope.Current` may be null (e.g. a tool invoked outside a turn);
  all `Add` call sites are null-safe.
- Scope is per async turn; tools executing out-of-flow do not contribute (v1 limitation,
  documented in code near `Begin()`).

## Testing

- **Unit** — `ResearchReferenceScope`: dedup by normalized URL; malformed/non-http URLs
  skipped; `AsyncLocal` value propagates across `await` boundaries; nested `Begin()`
  restores the previous scope on dispose.
- **Unit** — `PsxDataClient`: RSS `<link>` is extracted into `NewsHeadline.Url` for a
  representative feed sample.
- **Backend** — `MarkdownSessionStore`: references sidecar round-trip (persist references
  for an assistant turn → `GetConversationMessages` returns them attached to the correct
  assistant snapshot; turns without references remain empty).
- **Manual/integration** — run a `research_stock` request through the web chat; confirm
  the Sources list renders under the reply, links open correctly, and the list reappears
  after reopening the conversation.
