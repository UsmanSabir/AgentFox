# Research Reference Tracking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Capture the web-source URLs the TradingAgent consults during research, surface them as a "Sources" list under the assistant's chat reply, and persist them so they reappear when a conversation is reopened.

**Architecture:** An ambient, per-turn reference collector (`ResearchReferenceScope`, `AsyncLocal`-backed) lives in the shared `AgentFox.Plugins` assembly so the host and plugins share one static. `FoxAgent.ProcessAsync` opens a scope around each turn; tools (starting with `research_stock`) register URLs into it; the host drains the scope into `AgentResult.References`, which flows out through `AgentReply` → `ChatResponse`/SSE `done` → the frontend, and is persisted to a per-conversation `.refs.jsonl` sidecar.

**Tech Stack:** .NET 10 (C#), MSTest (`tests/AgentFox.ChannelTests`), SvelteKit + TypeScript frontend (`src/frontend`), `marked`/DOMPurify for chat markdown.

## Global Constraints

- **Plugin assembly version pinning:** `AgentFox.Plugins` and `TradingAgent` must not introduce package references to `Microsoft.Extensions.*` at versions higher than the host resolves — roll-forward only binds up. New types added here use only BCL + existing references. (from `TradingAgent.csproj` comments)
- **`ResearchReference` and `ResearchReferenceScope` MUST live in `src/AgentFox.Plugins/`** — the assembly `PluginLoadContext` delegates to the host's default load context — so host and plugin resolve the same type and share the `AsyncLocal` static. Do not duplicate these types in the `Agent` project or the `TradingAgent` plugin.
- **Fail-soft:** URL capture must never throw out of a tool or a turn. `Add` silently skips malformed/non-http(s) URLs; a null `ResearchReferenceScope.Current` is a no-op.
- **Frontend link safety:** every rendered source link uses `target="_blank" rel="noopener noreferrer"`.
- **Build/verify commands** (run from repo root `d:/RnD/CSharpClaw`):
  - Backend build: `dotnet build src/AgentFox.sln`
  - Backend tests: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~<TestClass>"`
  - Frontend typecheck: `cd src/frontend && npm run check`
  - Frontend build: `cd src/frontend && npm run build`

---

### Task 1: Shared reference collector (`ResearchReference` + `ResearchReferenceScope`)

**Files:**
- Create: `src/AgentFox.Plugins/Research/ResearchReference.cs`
- Create: `src/AgentFox.Plugins/Research/ResearchReferenceScope.cs`
- Test: `tests/AgentFox.ChannelTests/ResearchReferenceScopeTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ResearchReference(string Url, string? Title = null, string? Source = null)` in namespace `AgentFox.Plugins.Research`.
  - `public sealed class ResearchReferenceScope : IDisposable` in namespace `AgentFox.Plugins.Research` with:
    - `public static ResearchReferenceScope? Current { get; }`
    - `public static IDisposable Begin()`
    - `public void Add(string? url, string? title = null, string? source = null)`
    - `public void AddRange(IEnumerable<ResearchReference> references)`
    - `public IReadOnlyList<ResearchReference> Snapshot()`
- Consumes: nothing (leaf task).

- [ ] **Step 1: Write the failing test**

Create `tests/AgentFox.ChannelTests/ResearchReferenceScopeTests.cs`:

```csharp
using AgentFox.Plugins.Research;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ResearchReferenceScopeTests
{
    [TestMethod]
    public void Current_IsNull_OutsideAnyScope()
    {
        Assert.IsNull(ResearchReferenceScope.Current);
    }

    [TestMethod]
    public void Add_DedupesByNormalizedUrl_FirstWins()
    {
        using (ResearchReferenceScope.Begin())
        {
            ResearchReferenceScope.Current!.Add("https://Example.com/a/", "First", "SrcA");
            ResearchReferenceScope.Current!.Add("https://example.com/a", "Second", "SrcB");

            var snap = ResearchReferenceScope.Current!.Snapshot();
            Assert.AreEqual(1, snap.Count);
            Assert.AreEqual("First", snap[0].Title);
            Assert.AreEqual("SrcA", snap[0].Source);
        }
    }

    [TestMethod]
    public void Add_SkipsMalformedAndNonHttpUrls()
    {
        using (ResearchReferenceScope.Begin())
        {
            ResearchReferenceScope.Current!.Add(null);
            ResearchReferenceScope.Current!.Add("   ");
            ResearchReferenceScope.Current!.Add("not a url");
            ResearchReferenceScope.Current!.Add("ftp://example.com/x");
            ResearchReferenceScope.Current!.Add("javascript:alert(1)");
            ResearchReferenceScope.Current!.Add("https://ok.example.com/y");

            var snap = ResearchReferenceScope.Current!.Snapshot();
            Assert.AreEqual(1, snap.Count);
            Assert.AreEqual("https://ok.example.com/y", snap[0].Url);
        }
    }

    [TestMethod]
    public void Begin_Nested_RestoresPreviousScopeOnDispose()
    {
        using (ResearchReferenceScope.Begin())
        {
            var outer = ResearchReferenceScope.Current!;
            outer.Add("https://example.com/outer");

            using (ResearchReferenceScope.Begin())
            {
                Assert.AreNotSame(outer, ResearchReferenceScope.Current);
                ResearchReferenceScope.Current!.Add("https://example.com/inner");
                Assert.AreEqual(1, ResearchReferenceScope.Current!.Snapshot().Count);
            }

            Assert.AreSame(outer, ResearchReferenceScope.Current);
            Assert.AreEqual(1, ResearchReferenceScope.Current!.Snapshot().Count);
        }
        Assert.IsNull(ResearchReferenceScope.Current);
    }

    [TestMethod]
    public async Task Current_PropagatesAcrossAwait()
    {
        using (ResearchReferenceScope.Begin())
        {
            await Task.Yield();
            await NestedAddAsync();
            Assert.AreEqual(1, ResearchReferenceScope.Current!.Snapshot().Count);
        }

        static async Task NestedAddAsync()
        {
            await Task.Delay(1);
            ResearchReferenceScope.Current!.Add("https://example.com/deep");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~ResearchReferenceScopeTests"`
Expected: FAIL — build error, `ResearchReferenceScope`/`ResearchReference` do not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/AgentFox.Plugins/Research/ResearchReference.cs`:

```csharp
namespace AgentFox.Plugins.Research;

/// <summary>
/// A single web source consulted during research: the URL plus optional human-readable
/// title and source label. Surfaced to the UI as a "Sources" citation under a chat reply.
/// </summary>
public sealed record ResearchReference(string Url, string? Title = null, string? Source = null);
```

Create `src/AgentFox.Plugins/Research/ResearchReferenceScope.cs`:

```csharp
namespace AgentFox.Plugins.Research;

/// <summary>
/// Ambient, per-turn collector of <see cref="ResearchReference"/>s. The host opens a scope
/// around an agent turn via <see cref="Begin"/>; any tool executing within that turn's async
/// flow registers URLs through <see cref="Current"/>; the host drains them with
/// <see cref="Snapshot"/> when the turn completes.
///
/// Backed by <see cref="AsyncLocal{T}"/> so the value flows across await boundaries. Because
/// this type lives in AgentFox.Plugins (delegated to the host's default load context), the host
/// and every plugin share one static — a plugin tool writing to <see cref="Current"/> is seen by
/// the host turn that opened the scope.
///
/// v1 limitation: only tools running within the opening turn's async flow contribute. Tools that
/// run on a different lane (background workers, sub-agents with their own <see cref="Begin"/>)
/// collect into their own scope, not the parent's.
/// </summary>
public sealed class ResearchReferenceScope : IDisposable
{
    private static readonly AsyncLocal<ResearchReferenceScope?> _current = new();

    private readonly ResearchReferenceScope? _previous;
    private readonly object _gate = new();
    private readonly List<ResearchReference> _items = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _disposed;

    private ResearchReferenceScope(ResearchReferenceScope? previous) => _previous = previous;

    /// <summary>The scope for the current async flow, or null if none is open.</summary>
    public static ResearchReferenceScope? Current => _current.Value;

    /// <summary>Opens a fresh scope, restoring the previous one when the returned handle is disposed.</summary>
    public static IDisposable Begin()
    {
        var scope = new ResearchReferenceScope(_current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <summary>Registers a source. No-op for null/whitespace or non-http(s) URLs. Dedupes by normalized URL.</summary>
    public void Add(string? url, string? title = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        var key = Normalize(uri);
        lock (_gate)
        {
            if (!_seen.Add(key)) return; // first occurrence wins
            _items.Add(new ResearchReference(url.Trim(), Trim(title), Trim(source)));
        }
    }

    public void AddRange(IEnumerable<ResearchReference> references)
    {
        if (references is null) return;
        foreach (var r in references) Add(r.Url, r.Title, r.Source);
    }

    /// <summary>A stable copy of the references collected so far, in first-seen order.</summary>
    public IReadOnlyList<ResearchReference> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _current.Value = _previous;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Normalize for dedup: lowercase scheme+host, drop a single trailing slash on the path,
    // keep query. Deliberately conservative so distinct articles are never collapsed.
    private static string Normalize(Uri uri)
    {
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : uri.AbsolutePath;
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{path}{uri.Query}";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~ResearchReferenceScopeTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/AgentFox.Plugins/Research/ResearchReference.cs src/AgentFox.Plugins/Research/ResearchReferenceScope.cs tests/AgentFox.ChannelTests/ResearchReferenceScopeTests.cs
git commit -m "feat: ambient per-turn research reference collector"
```

---

### Task 2: Capture research URLs in TradingAgent

**Files:**
- Modify: `src/Plugins/TradingAgent/Research/PsxDataClient.cs` (record `NewsHeadline` line 32; `GatherAsync` lines 82-107; `StockResearchData` lines 49-57; `GetNewsAsync` lines 293-318)
- Modify: `src/Plugins/TradingAgent/Tools/ResearchStockTool.cs` (`ExecuteInternalAsync` lines 99-134)
- Test: `tests/AgentFox.ChannelTests/PsxNewsFeedTests.cs`

**Interfaces:**
- Consumes: `ResearchReference`, `ResearchReferenceScope` (Task 1).
- Produces:
  - `NewsHeadline(string Title, string? Source, DateTime? PublishedUtc, string? Url)` — new `Url` field appended (positional, last).
  - `public static IReadOnlyList<NewsHeadline> ParseNewsFeed(string xml, int max)` on `PsxDataClient` — pure, testable feed parser.
  - `StockResearchData` gains `public IReadOnlyList<string> SourceUrls { get; init; } = [];` — the PSX portal endpoint URLs used for this gather.

- [ ] **Step 1: Write the failing test**

Create `tests/AgentFox.ChannelTests/PsxNewsFeedTests.cs`:

```csharp
using TradingAgent.Research;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class PsxNewsFeedTests
{
    private const string SampleRss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel>
          <item>
            <title>OGDC hits new high</title>
            <link>https://news.example.com/ogdc-high</link>
            <pubDate>Mon, 14 Jul 2025 09:00:00 GMT</pubDate>
            <source url="https://biz.example.com">Business Times</source>
          </item>
          <item>
            <title>Market roundup</title>
            <link>https://news.example.com/roundup</link>
            <pubDate>Tue, 15 Jul 2025 09:00:00 GMT</pubDate>
          </item>
        </channel></rss>
        """;

    [TestMethod]
    public void ParseNewsFeed_ExtractsLinkIntoUrl()
    {
        var headlines = PsxDataClient.ParseNewsFeed(SampleRss, 10);

        Assert.AreEqual(2, headlines.Count);
        Assert.AreEqual("OGDC hits new high", headlines[0].Title);
        Assert.AreEqual("https://news.example.com/ogdc-high", headlines[0].Url);
        Assert.AreEqual("Business Times", headlines[0].Source);
    }

    [TestMethod]
    public void ParseNewsFeed_RespectsMaxAndSkipsEmptyTitles()
    {
        var headlines = PsxDataClient.ParseNewsFeed(SampleRss, 1);
        Assert.AreEqual(1, headlines.Count);
    }

    [TestMethod]
    public void ParseNewsFeed_MalformedXml_ReturnsEmpty()
    {
        var headlines = PsxDataClient.ParseNewsFeed("<not-xml", 10);
        Assert.AreEqual(0, headlines.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~PsxNewsFeedTests"`
Expected: FAIL — `PsxDataClient.ParseNewsFeed` does not exist; `NewsHeadline` has no `Url`.

- [ ] **Step 3a: Add `Url` to `NewsHeadline`**

In `src/Plugins/TradingAgent/Research/PsxDataClient.cs` line 32, replace:

```csharp
public sealed record NewsHeadline(string Title, string? Source, DateTime? PublishedUtc);
```

with:

```csharp
public sealed record NewsHeadline(string Title, string? Source, DateTime? PublishedUtc, string? Url = null);
```

- [ ] **Step 3b: Add `SourceUrls` to `StockResearchData`**

In the same file, in the `StockResearchData` record (lines 49-57), add after the `RetrievedAtUtc` property:

```csharp
    /// <summary>The web endpoints consulted for this gather (PSX portal series + company page), for citation.</summary>
    public IReadOnlyList<string> SourceUrls { get; init; } = [];
```

- [ ] **Step 3c: Extract a pure `ParseNewsFeed` and use it from `GetNewsAsync`**

Replace `GetNewsAsync` (lines 293-318) with:

```csharp
    private async Task<IReadOnlyList<NewsHeadline>> GetNewsAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = NewsFeedUrl(query);
            var xml = await _http.GetStringAsync(url, ct);
            return ParseNewsFeed(xml, Math.Max(1, _options.Value.ResearchHeadlineCount));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PsxData] News fetch failed for query '{Query}'.", query);
            return [];
        }
    }

    /// <summary>Builds the keyless Google News RSS search URL for a query.</summary>
    public static string NewsFeedUrl(string query) =>
        "https://news.google.com/rss/search?q=" + Uri.EscapeDataString(query) + "&hl=en-PK&gl=PK&ceid=PK:en";

    /// <summary>
    /// Parses a Google News RSS document into headlines. Pure/deterministic so it can be unit-tested
    /// without network access. Each item's &lt;link&gt; is captured as <see cref="NewsHeadline.Url"/>.
    /// Returns an empty list for unparseable XML.
    /// </summary>
    public static IReadOnlyList<NewsHeadline> ParseNewsFeed(string xml, int max)
    {
        try
        {
            var feed = XDocument.Parse(xml);
            return feed.Descendants("item")
                .Take(Math.Max(1, max))
                .Select(item => new NewsHeadline(
                    item.Element("title")?.Value.Trim() ?? "",
                    item.Elements().FirstOrDefault(e => e.Name.LocalName == "source")?.Value.Trim(),
                    DateTime.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                        ? dt : null,
                    item.Element("link")?.Value.Trim()))
                .Where(h => h.Title.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
```

- [ ] **Step 3d: Populate `SourceUrls` in `GatherAsync`**

In `GatherAsync` (lines 82-107), build the portal URLs and attach them to the result. Replace the `return new StockResearchData { … }` block (lines 98-106) with:

```csharp
        var baseUrl = _options.Value.PsxDataBaseUrl.TrimEnd('/');
        var sourceUrls = new List<string>
        {
            $"{baseUrl}/timeseries/eod/{symbol}",
            $"{baseUrl}/timeseries/int/{symbol}",
            $"{baseUrl}/company/{symbol}",
            $"{baseUrl}/timeseries/eod/{Kse100Symbol}"
        };
        if (_options.Value.ResearchNewsEnabled)
        {
            sourceUrls.Add(NewsFeedUrl($"\"{symbol}\" PSX Pakistan stock"));
            sourceUrls.Add(NewsFeedUrl("Pakistan Stock Exchange KSE-100"));
        }

        return new StockResearchData
        {
            Quote          = await quoteTask,
            IndexQuote     = await indexTask,
            ListingStatus  = await listingTask,
            CompanyNews    = await newsTask,
            MarketNews     = await marketTask,
            RetrievedAtUtc = DateTime.UtcNow,
            SourceUrls     = sourceUrls
        };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~PsxNewsFeedTests"`
Expected: PASS (3 tests). Also confirm `PsxListingStatusTests` still passes:
Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~PsxListingStatusTests"`
Expected: PASS.

- [ ] **Step 5: Register references from `ResearchStockTool`**

In `src/Plugins/TradingAgent/Tools/ResearchStockTool.cs`, add the using at the top (after line 6):

```csharp
using AgentFox.Plugins.Research;
```

Then in `ExecuteInternalAsync`, immediately after `var data = await _dataClient.GatherAsync(symbol);` (line 108), insert:

```csharp
        // Register the web sources consulted so the chat UI can cite them. Fail-soft: no-op when no
        // scope is open (e.g. the tool is invoked outside an agent turn).
        var scope = ResearchReferenceScope.Current;
        if (scope is not null)
        {
            foreach (var headline in data.CompanyNews.Concat(data.MarketNews))
                scope.Add(headline.Url, headline.Title, headline.Source ?? "News");
            foreach (var portalUrl in data.SourceUrls)
                scope.Add(portalUrl, $"PSX data: {symbol}", "PSX Data Portal");
        }
```

- [ ] **Step 6: Build to verify the plugin compiles**

Run: `dotnet build src/AgentFox.sln`
Expected: Build succeeded (0 errors).

- [ ] **Step 7: Commit**

```bash
git add src/Plugins/TradingAgent/Research/PsxDataClient.cs src/Plugins/TradingAgent/Tools/ResearchStockTool.cs tests/AgentFox.ChannelTests/PsxNewsFeedTests.cs
git commit -m "feat: capture research source URLs in TradingAgent"
```

---

### Task 3: Drain the scope into `AgentResult` per turn

**Files:**
- Modify: `src/Agent/Models/AgentModels.cs` (`AgentResult` lines 141-150)
- Modify: `src/Agent/Agents/Agent.cs` (`ProcessAsync` lines 188-333)

**Interfaces:**
- Consumes: `ResearchReferenceScope` (Task 1).
- Produces: `AgentResult.References` — `public List<ResearchReference> References { get; set; } = new();`

- [ ] **Step 1: Add `References` to `AgentResult`**

In `src/Agent/Models/AgentModels.cs`, add the using at the top of the file (with the other usings):

```csharp
using AgentFox.Plugins.Research;
```

Then in the `AgentResult` class (lines 141-150), add after the `Duration` property:

```csharp
    public List<ResearchReference> References { get; set; } = new();
```

- [ ] **Step 2: Open a scope around the turn and snapshot it**

In `src/Agent/Agents/Agent.cs`, add the using at the top of the file (with the other usings):

```csharp
using AgentFox.Plugins.Research;
```

In `ProcessAsync`, open the scope at the start of the `try` block. Replace line 211 (`var agent = _chatAgent;`) with:

```csharp
            using var referenceScope = ResearchReferenceScope.Begin();
            var agent = _chatAgent;
```

Then at line 312, replace:

```csharp
            var result = new AgentResult { Success = true, Output = responseText };
```

with:

```csharp
            var references = ResearchReferenceScope.Current?.Snapshot().ToList() ?? new List<ResearchReference>();
            var result = new AgentResult { Success = true, Output = responseText, References = references };
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/AgentFox.sln`
Expected: Build succeeded (0 errors).

- [ ] **Step 4: Commit**

```bash
git add src/Agent/Models/AgentModels.cs src/Agent/Agents/Agent.cs
git commit -m "feat: drain research references into AgentResult per turn"
```

---

### Task 4: Transport references to the web API

**Files:**
- Create: `src/AgentFox.Plugins/Models/AgentReply.cs`
- Modify: `src/AgentFox.Plugins/Interfaces/IAgentService.cs` (lines 17, 28-32)
- Modify: `src/AgentFox.Plugins/Models/ChatRequest.cs` (`ChatResponse` lines 17-30)
- Modify: `src/Agent/Agents/FoxAgentHolder.cs` (`FoxAgentService` lines 50-70)
- Modify: `src/Agent/Modules/Web/WebModule.cs` (`/chat` lines 86-92; `/chat/stream` lines 134-153)

**Interfaces:**
- Consumes: `AgentResult.References` (Task 3), `ResearchReference` (Task 1).
- Produces:
  - `public sealed class AgentReply { public string Output { get; set; } = string.Empty; public List<ResearchReference> References { get; set; } = new(); }` in `AgentFox.Plugins.Models`.
  - `IAgentService.RunAsync` now returns `Task<AgentReply>`; `IAgentService.StreamAsync` now returns `Task<AgentReply>`.
  - `ChatResponse.References` — `public List<ResearchReference> References { get; set; } = new();`

> Note: `IAgentService` is implemented only by `FoxAgentService` and called only by `WebModule` (verified). Changing the return types is safe within this repo.

- [ ] **Step 1: Create the `AgentReply` DTO**

Create `src/AgentFox.Plugins/Models/AgentReply.cs`:

```csharp
using AgentFox.Plugins.Research;

namespace AgentFox.Plugins.Models;

/// <summary>The result of an agent turn as seen by the web/API layer: the reply text plus any
/// research source references collected during the turn.</summary>
public sealed class AgentReply
{
    public string Output { get; set; } = string.Empty;
    public List<ResearchReference> References { get; set; } = new();
}
```

- [ ] **Step 2: Update the `IAgentService` contract**

In `src/AgentFox.Plugins/Interfaces/IAgentService.cs`, add at the top:

```csharp
using AgentFox.Plugins.Models;
```

Change the `RunAsync` signature (line 17) to:

```csharp
    Task<AgentReply> RunAsync(string input, string? conversationId = null, CancellationToken ct = default);
```

Change the `StreamAsync` signature (lines 28-32) to:

```csharp
    Task<AgentReply> StreamAsync(
        string input,
        string? conversationId,
        Func<string, Task> onToken,
        CancellationToken ct = default);
```

- [ ] **Step 3: Add `References` to `ChatResponse`**

In `src/AgentFox.Plugins/Models/ChatRequest.cs`, add at the top:

```csharp
using AgentFox.Plugins.Research;
```

In the `ChatResponse` class (lines 17-30), add after the `Error` property:

```csharp
    /// <summary>Web sources consulted during the turn, for display as citations.</summary>
    public List<ResearchReference> References { get; set; } = new();
```

- [ ] **Step 4: Update `FoxAgentService` to return `AgentReply`**

In `src/Agent/Agents/FoxAgentHolder.cs`, add the using at the top:

```csharp
using AgentFox.Plugins.Models;
```

Replace `RunAsync` and `StreamAsync` (lines 50-70) with:

```csharp
    public async Task<AgentReply> RunAsync(
        string input,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        var agent = await _holder.WaitAsync(ct);
        var result = await agent.ProcessAsync(input, conversationId, cancellationToken: ct);
        return new AgentReply { Output = result.Output ?? string.Empty, References = result.References };
    }

    public async Task<AgentReply> StreamAsync(
        string input,
        string? conversationId,
        Func<string, Task> onToken,
        CancellationToken ct = default)
    {
        var agent = await _holder.WaitAsync(ct);
        var streaming = new StreamingCallbacks { OnToken = onToken };
        var result = await agent.ProcessAsync(input, conversationId, streaming, ct);
        return new AgentReply { Output = result.Output ?? string.Empty, References = result.References };
    }
```

- [ ] **Step 5: Update the `/chat` endpoint**

In `src/Agent/Modules/Web/WebModule.cs`, replace the `/chat` success path (lines 86-92) with:

```csharp
                var reply = await agentService.RunAsync(req.Message, conversationId, ct);
                return Results.Ok(new ChatResponse
                {
                    Response = reply.Output,
                    ConversationId = conversationId,
                    Success = true,
                    References = reply.References
                });
```

- [ ] **Step 6: Update the `/chat/stream` done payload**

In the same file, replace the `StreamAsync` call and done payload (lines 134-153) with:

```csharp
                var reply = await agentService.StreamAsync(
                    req.Message,
                    conversationId,
                    async token =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var data = JsonSerializer.Serialize(new { token });
                        await httpContext.Response.WriteAsync($"data: {data}\n\n", ct);
                        await httpContext.Response.Body.FlushAsync(ct);
                    },
                    ct);

                // Terminal event — always includes the conversation ID so the client
                // can store it and send it with the next message.
                var donePayload = JsonSerializer.Serialize(new
                {
                    done = true,
                    conversationId,
                    references = reply.References
                });
                await httpContext.Response.WriteAsync($"event: done\ndata: {donePayload}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
```

- [ ] **Step 7: Build to verify it compiles**

Run: `dotnet build src/AgentFox.sln`
Expected: Build succeeded (0 errors). If any other implementor/caller of `IAgentService` surfaces as an error, update it to the new return type (none expected).

- [ ] **Step 8: Commit**

```bash
git add src/AgentFox.Plugins/Models/AgentReply.cs src/AgentFox.Plugins/Interfaces/IAgentService.cs src/AgentFox.Plugins/Models/ChatRequest.cs src/Agent/Agents/FoxAgentHolder.cs src/Agent/Modules/Web/WebModule.cs
git commit -m "feat: transport research references through chat API"
```

---

### Task 5: Persist references to a per-conversation sidecar

**Files:**
- Modify: `src/Agent/Memory/MarkdownSessionStore.cs` (`GetConversationMessages` lines 216-233; `DeleteSession` lines 235-243; `ConversationMessageSnapshot` line 496; add new methods)
- Modify: `src/Agent/Agents/Agent.cs` (`ProcessAsync`, after `SaveSession` at line 303)
- Test: `tests/AgentFox.ChannelTests/ReferencesSidecarTests.cs`

**Interfaces:**
- Consumes: `ResearchReference` (Task 1).
- Produces:
  - `ConversationMessageSnapshot` gains init-only `IReadOnlyList<ResearchReference> References { get; init; } = []`.
  - `public void PersistAssistantReferences(string conversationId, IReadOnlyList<ResearchReference> references)` on `MarkdownSessionStore`.

- [ ] **Step 1: Write the failing test**

Create `tests/AgentFox.ChannelTests/ReferencesSidecarTests.cs`:

```csharp
using AgentFox.Memory;
using AgentFox.Plugins.Research;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentFox.ChannelTests;

[TestClass]
public sealed class ReferencesSidecarTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reftests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Writes a minimal two-turn conversation (user/assistant x2) directly through the store's
    // append path so GetConversationMessages has assistant snapshots to attach references to.
    private static void SeedTwoTurns(MarkdownSessionStore store, string convId)
    {
        var provider = store.HistoryProvider;
        // Turn 1
        store.AppendForTest(convId, new ChatMessage(ChatRole.User, "research OGDC"));
        store.AppendForTest(convId, new ChatMessage(ChatRole.Assistant, "OGDC looks fine."));
        // Turn 2
        store.AppendForTest(convId, new ChatMessage(ChatRole.User, "and LUCK?"));
        store.AppendForTest(convId, new ChatMessage(ChatRole.Assistant, "LUCK looks fine too."));
    }

    [TestMethod]
    public void PersistAndRead_AttachesReferencesToCorrectAssistantSnapshot()
    {
        var dir = NewTempDir();
        var store = new MarkdownSessionStore(dir);
        const string conv = "main";
        SeedTwoTurns(store, conv);

        // Only the SECOND assistant reply (index 1) has references.
        store.PersistAssistantReferences(conv, new List<ResearchReference>
        {
            new("https://news.example.com/luck", "LUCK rallies", "Business Times")
        });

        var msgs = store.GetConversationMessages(conv);
        var assistants = msgs.Where(m => m.Role == "assistant").ToList();

        Assert.AreEqual(2, assistants.Count);
        Assert.AreEqual(0, assistants[0].References.Count);
        Assert.AreEqual(1, assistants[1].References.Count);
        Assert.AreEqual("https://news.example.com/luck", assistants[1].References[0].Url);
    }

    [TestMethod]
    public void PersistAssistantReferences_EmptyList_WritesNothing()
    {
        var dir = NewTempDir();
        var store = new MarkdownSessionStore(dir);
        const string conv = "main";
        SeedTwoTurns(store, conv);

        store.PersistAssistantReferences(conv, new List<ResearchReference>());

        var msgs = store.GetConversationMessages(conv);
        Assert.IsTrue(msgs.Where(m => m.Role == "assistant").All(a => a.References.Count == 0));
    }
}
```

> This test needs a small test-only append helper on the store (the real append path goes
> through the AI framework's history provider, which is awkward to drive in a unit test).
> Step 3 adds `AppendForTest`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~ReferencesSidecarTests"`
Expected: FAIL — `AppendForTest`, `PersistAssistantReferences`, and `ConversationMessageSnapshot.References` do not exist.

- [ ] **Step 3: Add `References` to the snapshot and the store methods**

In `src/Agent/Memory/MarkdownSessionStore.cs`, add the using at the top:

```csharp
using AgentFox.Plugins.Research;
```

Change `ConversationMessageSnapshot` (line 496) from a positional-only record to one with an init-only `References`:

```csharp
public sealed record ConversationMessageSnapshot(string Role, string Content)
{
    public IReadOnlyList<ResearchReference> References { get; init; } = [];
}
```

Add a JSON options field and a sidecar-line record near the other private statics (after `_jsonOpts`, line 136):

```csharp
    private sealed record ReferenceLine(int I, List<ResearchReference> Items);
```

Add the sidecar path helper next to `PendingFilePath` (after line 288):

```csharp
    /// <summary>References sidecar path (<c>{session}.md.refs.jsonl</c>) for a conversation.</summary>
    private string ReferencesFilePath(string conversationId) => FilePath(conversationId) + ".refs.jsonl";
```

Add a test-only append helper and the persist method (place them after `SaveSession`, around line 181):

```csharp
    /// <summary>
    /// Test-only: append a fully-formed message directly to the in-memory list and flush it to
    /// disk, bypassing the AI framework's history provider (which needs a live AgentSession).
    /// Mirrors the same delta-tracking logic as SaveSession. Not used in production code paths.
    /// </summary>
    internal void AppendForTest(string conversationId, ChatMessage message)
    {
        var list = _messages.GetOrAdd(conversationId, _ => []);
        list.Add(message);

        int written = _writtenCounts.GetOrAdd(conversationId, 0);
        var delta = list.Skip(written).ToList();
        bool isNewFile = written == 0 && !File.Exists(FilePath(conversationId));
        AppendToFile(conversationId, delta, isNewFile);
        _writtenCounts[conversationId] = list.Count;
    }

    /// <summary>
    /// Records the research references collected during the most recent assistant turn.
    /// No-op when <paramref name="references"/> is empty. The references are keyed by the
    /// assistant reply's position among user/assistant non-empty-text messages, so they can be
    /// re-attached to the correct snapshot on reload even when other turns have no references.
    /// </summary>
    public void PersistAssistantReferences(string conversationId, IReadOnlyList<ResearchReference> references)
    {
        if (references is null || references.Count == 0) return;
        SessionManager.EnsureSafeSessionId(conversationId);

        if (!_messages.TryGetValue(conversationId, out var messages)) return;
        int assistantIndex = ProjectSnapshots(messages).Count(s => s.Role == "assistant") - 1;
        if (assistantIndex < 0) return;

        var line = JsonSerializer.Serialize(new ReferenceLine(assistantIndex, references.ToList()), _jsonOpts);
        File.AppendAllText(ReferencesFilePath(conversationId), line + "\n", Encoding.UTF8);
    }
```

- [ ] **Step 4: Add the projection helper and wire references into `GetConversationMessages`**

Replace `GetConversationMessages` (lines 216-233) with:

```csharp
    /// <summary>Returns user-visible text messages for a persisted conversation, with any
    /// research references attached to the corresponding assistant messages.</summary>
    public IReadOnlyList<ConversationMessageSnapshot> GetConversationMessages(string conversationId)
    {
        SessionManager.EnsureSafeSessionId(conversationId);
        if (!_messages.TryGetValue(conversationId, out var messages))
        {
            var path = FilePath(conversationId);
            if (!File.Exists(path)) return [];
            messages = ParseFile(path);
        }

        var snapshots = ProjectSnapshots(messages);
        var refs = LoadReferences(conversationId);
        if (refs.Count == 0) return snapshots;

        var result = new List<ConversationMessageSnapshot>(snapshots.Count);
        int assistantIndex = 0;
        foreach (var s in snapshots)
        {
            if (s.Role == "assistant" && refs.TryGetValue(assistantIndex++, out var items))
                result.Add(s with { References = items });
            else
                result.Add(s);
        }
        return result;
    }

    // Projects the raw message list to user/assistant non-empty-text snapshots. Shared by the
    // read path and PersistAssistantReferences so the assistant-index definition never drifts.
    private static List<ConversationMessageSnapshot> ProjectSnapshots(List<ChatMessage> messages) =>
        messages
            .Where(message => message.Role == ChatRole.User || message.Role == ChatRole.Assistant)
            .Select(message => new ConversationMessageSnapshot(
                message.Role == ChatRole.User ? "user" : "assistant",
                message.Text?.Trim() ?? string.Empty))
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .ToList();

    // Reads the sidecar into a map of assistantIndex → references. Empty when absent/unreadable.
    private Dictionary<int, List<ResearchReference>> LoadReferences(string conversationId)
    {
        var map = new Dictionary<int, List<ResearchReference>>();
        var path = ReferencesFilePath(conversationId);
        if (!File.Exists(path)) return map;

        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var line = JsonSerializer.Deserialize<ReferenceLine>(raw, _jsonOpts);
                if (line?.Items is { Count: > 0 }) map[line.I] = line.Items;
            }
            catch { /* malformed line — skip */ }
        }
        return map;
    }
```

- [ ] **Step 5: Delete the sidecar in `DeleteSession`**

In `DeleteSession` (lines 235-243), after the block that deletes the `.md` file, add:

```csharp
        var refsPath = ReferencesFilePath(conversationId);
        if (File.Exists(refsPath))
            File.Delete(refsPath);
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~ReferencesSidecarTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Call `PersistAssistantReferences` from `ProcessAsync`**

In `src/Agent/Agents/Agent.cs`, in `ProcessAsync`, right after `ConversationStore.SaveSession(conversationId, session);` (line 303), and after the `references` local is computed — reorder so the snapshot is taken before persistence. Replace lines 302-315 (from the `SaveSession` comment through `return result;`) with:

```csharp
            // Persist updated session metadata (e.g. lastActiveAt) after each turn.
            ConversationStore.SaveSession(conversationId, session);

            var references = ResearchReferenceScope.Current?.Snapshot().ToList() ?? new List<ResearchReference>();
            if (references.Count > 0)
                (ConversationStore as MarkdownSessionStore)?.PersistAssistantReferences(conversationId, references);

            // Turn completed successfully — remove the pending marker.
            (ConversationStore as MarkdownSessionStore)?.ClearPendingUserMessage(conversationId);

            // Keep the session alive in the session manager
            SessionManager?.TouchSession(conversationId);
            _logger?.LogInformation("Agent '{AgentName}' completed task in conversation {ConversationId}", Name, conversationId);

            var result = new AgentResult { Success = true, Output = responseText, References = references };
```

> This replaces the `references`/`result` code that Task 3 Step 2 placed at the old line 312 —
> by this point in the plan that code has moved a few lines earlier (right after `SaveSession`)
> and gained the `PersistAssistantReferences` call. There should be exactly one `references`
> declaration and one `result` assignment left in this method after this step.

- [ ] **Step 8: Build and run the full backend test class**

Run: `dotnet build src/AgentFox.sln`
Expected: Build succeeded (0 errors).
Run: `dotnet test tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj --filter "FullyQualifiedName~ReferencesSidecarTests|FullyQualifiedName~ResearchReferenceScopeTests|FullyQualifiedName~PsxNewsFeedTests"`
Expected: PASS (all).

- [ ] **Step 9: Commit**

```bash
git add src/Agent/Memory/MarkdownSessionStore.cs src/Agent/Agents/Agent.cs tests/AgentFox.ChannelTests/ReferencesSidecarTests.cs
git commit -m "feat: persist research references to per-conversation sidecar"
```

---

### Task 6: Frontend types and store

**Files:**
- Modify: `src/frontend/src/lib/api.ts` (`ChatResponse` lines 12-17; `ConversationMessage` lines 115-118; `StreamEvent` lines 484-487; `streamChat` done parse lines 526-527; add `ReferenceItem`)
- Modify: `src/frontend/src/lib/stores.ts` (`ChatMessage` lines 15-23; add `attachReferences` helper near `finalizeMessage` line 64)

**Interfaces:**
- Produces (TypeScript):
  - `export interface ReferenceItem { url: string; title?: string; source?: string }`
  - `ChatResponse.references?: ReferenceItem[]`, `ConversationMessage.references?: ReferenceItem[]`
  - `StreamEvent` `done` variant gains `references?: ReferenceItem[]`
  - `ChatMessage.references?: ReferenceItem[]`
  - `export function attachReferences(id: string, references?: ReferenceItem[]): void`

> Note: backend serializes C# property names as camelCase by default (ASP.NET Core `System.Text.Json`
> web defaults), so `Url`/`Title`/`Source` arrive as `url`/`title`/`source`. Match that casing.

- [ ] **Step 1: Add `ReferenceItem` and extend response types in `api.ts`**

In `src/frontend/src/lib/api.ts`, add after the `ChatRequest` interface (line 10):

```typescript
export interface ReferenceItem {
  url: string;
  title?: string;
  source?: string;
}
```

Change `ChatResponse` (lines 12-17) to add:

```typescript
export interface ChatResponse {
  response: string;
  conversationId?: string;
  success: boolean;
  error?: string;
  references?: ReferenceItem[];
}
```

Change `ConversationMessage` (lines 115-118) to add:

```typescript
export interface ConversationMessage {
  role: 'user' | 'assistant';
  content: string;
  references?: ReferenceItem[];
}
```

- [ ] **Step 2: Extend the `StreamEvent` done variant and parse references**

Change `StreamEvent` (lines 484-487) to:

```typescript
export type StreamEvent =
  | { type: 'token';  token: string }
  | { type: 'done';   done: true; conversationId?: string; references?: ReferenceItem[] }
  | { type: 'error';  error: string };
```

Change the `done` yield in `streamChat` (line 527) to:

```typescript
              yield { type: 'done', done: true, conversationId: payload.conversationId, references: payload.references };
```

- [ ] **Step 3: Extend `ChatMessage` and add the `attachReferences` store helper**

In `src/frontend/src/lib/stores.ts`, change `ChatMessage` (lines 15-23) to add:

```typescript
export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  streaming?: boolean;
  error?: string;
  timestamp: Date;
  isBackgroundResult?: boolean;
  references?: ReferenceItem[];
}
```

Add the `ReferenceItem` import at the top (line 2 imports from `./api`):

```typescript
import type { AgentStatus, AgentInfo, ToolInfo, SkillInfo, ReferenceItem } from './api';
```

Add after `finalizeMessage` (line 68):

```typescript
export function attachReferences(id: string, references?: ReferenceItem[]) {
  if (!references || references.length === 0) return;
  chatMessages.update(msgs =>
    msgs.map(m => m.id === id ? { ...m, references } : m)
  );
}
```

- [ ] **Step 4: Typecheck**

Run: `cd src/frontend && npm run check`
Expected: 0 errors, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/frontend/src/lib/api.ts src/frontend/src/lib/stores.ts
git commit -m "feat: frontend types for research references"
```

---

### Task 7: Render the "Sources" section in chat

**Files:**
- Modify: `src/frontend/src/routes/chat/+page.svelte` (import line ~4; done branch lines 108-110; specialist branch lines 120-122; `openSession` map lines 168-173; render block after line 392; add CSS)

**Interfaces:**
- Consumes: `attachReferences` (Task 6), `ChatMessage.references`, `ConversationMessage.references`, `StreamEvent.done.references`.

- [ ] **Step 1: Import the store helper**

In `src/frontend/src/routes/chat/+page.svelte`, find the import that brings in `finalizeMessage`/`appendToken`/`addAssistantMessage` from `$lib/stores` and add `attachReferences` to it. (If store functions are imported individually, add `attachReferences` alongside `finalizeMessage`.)

- [ ] **Step 2: Populate references on the streaming `done` event**

In the streaming loop, change the `done` branch (lines 108-110) to:

```typescript
          } else if (event.type === 'done') {
            if (event.conversationId) activeConversationId.set(event.conversationId);
            attachReferences(assistantId, event.references);
            finalizeMessage(assistantId);
            break;
```

- [ ] **Step 3: Populate references on the synchronous/specialist path**

Change the specialist success branch (lines 120-122) to:

```typescript
        if (response.success) {
          appendToken(assistantId, response.response);
          attachReferences(assistantId, response.references);
          finalizeMessage(assistantId);
```

- [ ] **Step 4: Populate references on session reload**

In `openSession`, change the `chatMessages.set(...)` map (lines 168-173) to carry references:

```typescript
      chatMessages.set(history.messages.map(item => ({
        id: crypto.randomUUID(),
        role: item.role,
        content: item.content,
        references: item.references,
        timestamp: new Date(session.lastActive)
      })));
```

- [ ] **Step 5: Render the Sources block**

In the message template, insert the Sources block between the assistant content `{/if}` (line 392) and the copy-button `{#if …}` (line 394):

```svelte
              {#if msg.role === 'assistant' && !msg.streaming && !msg.error && msg.references && msg.references.length > 0}
                <div class="sources">
                  <span class="sources-label">Sources</span>
                  <ul class="sources-list">
                    {#each msg.references as ref}
                      <li>
                        <a href={ref.url} target="_blank" rel="noopener noreferrer" title={ref.url}>
                          {ref.title || ref.url}
                        </a>
                        {#if ref.source}<span class="sources-src">· {ref.source}</span>{/if}
                      </li>
                    {/each}
                  </ul>
                </div>
              {/if}
```

- [ ] **Step 6: Add CSS**

In the `<style>` block (near the `.copy-btn` rules around line 885), add:

```css
  .sources {
    margin-top: 8px;
    padding-top: 8px;
    border-top: 1px solid var(--border, rgba(255, 255, 255, 0.08));
    font-size: 12px;
  }
  .sources-label {
    display: block;
    color: var(--text-2, #9aa);
    text-transform: uppercase;
    letter-spacing: 0.04em;
    font-size: 10px;
    margin-bottom: 4px;
  }
  .sources-list { list-style: none; margin: 0; padding: 0; }
  .sources-list li { margin: 2px 0; overflow-wrap: anywhere; }
  .sources-list a { color: var(--accent, #6ab0ff); text-decoration: none; }
  .sources-list a:hover { text-decoration: underline; }
  .sources-src { color: var(--text-2, #9aa); }
```

> Use whatever CSS custom properties the file already relies on; the fallbacks above keep it safe
> if a variable is absent. Check the existing `.copy-btn`/`.message-content` rules for the exact
> variable names in use and prefer those.

- [ ] **Step 7: Typecheck and build**

Run: `cd src/frontend && npm run check`
Expected: 0 errors.
Run: `cd src/frontend && npm run build`
Expected: build succeeds; output written to the configured `wwwroot` (per prior chat-UI work).

- [ ] **Step 8: Commit**

```bash
git add src/frontend/src/routes/chat/+page.svelte
git commit -m "feat: render research Sources under chat replies"
```

---

## Manual Verification (after all tasks)

- [ ] Build everything: `dotnet build src/AgentFox.sln` → 0 errors.
- [ ] Run the app, open the web chat, and ask it to research a PSX symbol (e.g. "research OGDC") so `research_stock` runs.
- [ ] Confirm a "Sources" list appears under the assistant reply with clickable news + PSX portal links opening in a new tab.
- [ ] Reload the page / reopen the conversation from the Sessions panel and confirm the Sources list reappears.
- [ ] Ask a non-research question and confirm no empty "Sources" section renders.
