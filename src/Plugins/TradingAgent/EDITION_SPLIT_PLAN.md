# Edition Split Implementation Plan

Status: proposed refactor plan
Prepared: 2026-08-21
Implements: [PREMIUM_AUTO_TRADING_PLAN.md](PREMIUM_AUTO_TRADING_PLAN.md) section 7, "Keeping premium code private"
Scope: this public repository only. No premium logic, no strategy code, no behavior change.

The goal of this refactor is narrow and testable: end with a **`TradingAgent.Core` library plus a
thin community entry plugin**, such that a *separate private repository* can build a premium entry
plugin that is "everything the community plugin does, plus premium features", while the community
plugin keeps working exactly as it does today — same DI graph, same endpoints, same tools, same
specialist, same dashboard, same ledger.

Two projects, not three. An earlier draft of this plan also carved out a `TradingAgent.Abstractions`
assembly; section 2.3 records why that was dropped from this phase and when to revisit it.

Every step below must leave the solution building and `AgentFox.ChannelTests` green. Steps 1, 2, 2b,
3 and 5 change no runtime decision at all — they move code and introduce composition entry points.
Only step 3b (a new overlay data channel on an existing endpoint, empty in the community edition) and
step 4 (a startup guard) add behavior, and both are additive.

---

## 1. What section 7 gets right, and the four things it assumes this repo already has

Section 7's conclusion is correct and is confirmed by the loader source. What it does not say is that
three of its four premises are not yet true here. These are the real cost of the refactor.

### 1.1 Confirmed: the load-context hazard is real, and worse than type identity

[PluginLoadContext.cs](../../Agent/Modules/Loaders/PluginLoadContext.cs) shares only
`AgentFox.Plugins`, `Microsoft.Extensions.*`, `Newtonsoft.Json`, and `Polly` with the default
context. Section 7.1 stops at "the types will not cast". The more immediate failure is duplication:
if a community entry and a premium entry are both present, each gets its own `PluginLoadContext`
([Program.cs:1016](../../Agent/Program.cs#L1016)) and therefore its own copy of the core assembly, so
the host would run **two** `AhkFeedWorker`s, two `WatchlistMonitorWorker`s, two
`BrokerReconciliationWorker`s, two SQLite writers against the same ledger file, and two Chromium
profiles against the same broker account. That is a duplicate-order defect, not a casting error.
The mutual-exclusion guard in step 4 is therefore a safety control, not tidiness.

### 1.2 Not yet true: channel providers cannot live in the core

[Program.cs:512](../../Agent/Program.cs#L512) registers a plugin channel provider only when
`enabledModuleAssemblies.Contains(t.Assembly)` — that set holds `module.GetType().Assembly`
([Program.cs:490](../../Agent/Program.cs#L490)). And discovery only scans a DLL that has a sibling
`.deps.json` ([Program.cs:1007](../../Agent/Program.cs#L1007)), which a *dependency* assembly like
`TradingAgent.Core.dll` never gets.

Consequence: `IChannelProvider` implementations **must remain in the entry assembly**.
[WhatsAppBridgeChannelProvider.cs](Channel/WhatsAppBridgeChannelProvider.cs) stays in the community
entry project; [WhatsAppBridgeChannel.cs](Channel/WhatsAppBridgeChannel.cs) (the implementation)
moves to Core. The premium entry declares its own thin provider over the same Core channel type.
This is easy to get wrong and fails **silently** — the channel simply never appears.

### 1.3 Not yet true: the UI is embedded against a specific assembly

[TradingAgentModule.cs:105](TradingAgentModule.cs#L105) builds
`new ManifestEmbeddedFileProvider(typeof(TradingAgentModule).Assembly, "wwwroot")`. The dashboard is
embedded in the entry DLL. Leave it there and the premium edition ships no dashboard at all unless it
rebuilds the whole Svelte app. Step 5 moves the embedded `wwwroot` into Core, so both editions serve
**the same, single dashboard bundle** and the premium plugin contributes no UI files by default.

That is the requirement, stated plainly: premium features — projections, predicted points, next
target, confidence bands — appear **on the existing chart in the existing dashboard**, not on a
second page. The mechanism is data, not markup: the public `ChartPane` already draws every primitive
those features need, and it draws them from the `/trading/candles` response. Step 3b defines the
overlay contract that carries premium values into that same renderer. Nothing about "keeping premium
code private" requires a separate page, and a separate page would be the wrong product.

### 1.4 Not yet true: there is no NuGet packaging of any kind

Section 7.2 says "the premium repository consumes versioned public NuGet packages". Today there is no
`nuget.config`, no `dotnet pack` step anywhere in [release.yml](../../../.github/workflows/release.yml),
and `AgentFox.Plugins` is consumed only as a `ProjectReference`. Since Core must reference
`AgentFox.Plugins` (it *is* the plugin contract surface), publishing `TradingAgent.Core` as a package
requires publishing `AgentFox.Plugins` as a package too. That is step 6 and it is the only step with
real new infrastructure in it. Steps 1-5 are worth doing regardless of whether packaging ever lands,
because a private repo can consume the two public projects from a checked-out public core in the
interim.

### 1.5 What is cheaper than section 7 implies

- **Namespaces do not need to change.** All 105 source files already sit under `TradingAgent.*`, and
  a namespace is not an assembly. Split the assemblies and keep every namespace exactly as it is:
  zero `using` churn across the 2,696-line module and zero churn across the 38 test files that
  reference trading types. *Do not* rename to `TradingAgent.Core.*`.
- **Internals are a non-issue.** There are only 8 `internal` declarations in the plugin, all in
  broker/feed/analytics/persistence/tools code that lands together in Core. [AssemblyInfo.cs](AssemblyInfo.cs)
  moves to Core with its `InternalsVisibleTo("AgentFox.ChannelTests")`.
- **The endpoints are portable as-is.** All 45 `Map*` calls use lambda parameter injection, and
  `_services` is touched only inside `StartAsync` and `OnAgentReadyAsync` — never inside an endpoint.
  The endpoint block moves without rewiring.

---

## 2. Reuse model: what premium builds on

The product is: **premium = every community feature, plus auto-trading, plus UI enhancements.** The
reuse question is therefore not "how do we share a little code" but "what exactly does the premium
plugin reference and call".

### 2.1 The candidate approaches, and why this plan picks the middle one

**A. Premium wraps the community entry plugin.** Premium references `TradingAgent.dll` and holds a
`TradingAgentModule` instance, delegating to it:

    public sealed class PremiumTradingModule : IAgentAwareModule
    {
        private readonly TradingAgentModule _community = new();
        public void RegisterServices(IServiceCollection s, IConfiguration c)
        {
            _community.RegisterServices(s, c);       // everything community does
            s.AddSingleton<IAutoTrader, PremiumAutoTrader>();
        }
        public void MapEndpoints(IEndpointRouteBuilder e)
        {
            _community.MapEndpoints(e);
            e.MapGet("/trading/projection", ...);
        }
    }

This is **mechanically viable** and it is the cheapest thing that could work, so it deserves a fair
hearing rather than a dismissal. The loader cooperates: a referenced assembly's `.deps.json` is not
copied into the referencing project's output, so a premium publish contains
`Premium.Plugin.dll` + `Premium.Plugin.deps.json` + `TradingAgent.dll` *without*
`TradingAgent.deps.json`, and [Program.cs:1007](../../Agent/Program.cs#L1007) therefore skips
`TradingAgent.dll` for discovery and resolves it on demand inside the premium load context. One
context, one set of singletons, shared type identity. Verified against the current build output:
`plugins/` contains `TradingAgent.deps.json` but no `AgentFox.Plugins.deps.json`, even though
`AgentFox.Plugins.dll` sits right beside it.

Four things go wrong anyway:

1. **The community project self-deploys.** [TradingAgent.csproj:11](TradingAgent.csproj#L11) sets
   `OutputPath` to the host's `plugins/` folder. Referenced as a project, it *still* writes
   `TradingAgent.dll` **and** `TradingAgent.deps.json` there on every dev build — so a dev build has
   two entry plugins, `TradingAgentModule` gets discovered independently of the wrapper, and
   `RegisterServices` runs twice: duplicate feed/monitor/reconciliation workers, two writers on one
   ledger, two broker sessions (1.1). Fixing it means removing `OutputPath` — at which point the
   community project is a library, which is this plan's `TradingAgent.Core` under a different name.
2. **`IAppModule` is the wrong granularity.** `RegisterServices` is one `void`; you get all of it or
   none of it. "Everything except the community watchlist worker, because premium replaces it" is
   not expressible. And [TradingAgentModule.cs:82](TradingAgentModule.cs#L82) is `sealed`, so there
   is no override — only delegation, with no hook anywhere inside the 2,696 lines.
3. **Endpoints cannot be overridden by re-mapping** — see 2.2. The word "override" in the sketch is
   the part that does not survive contact with ASP.NET routing.
4. **No boundary at all.** The community DLL exposes `AhkBroker`, `AhkPortalClient`,
   `PlaceOrderTool`, and Puppeteer publicly, so premium strategy code can reach the broker directly,
   which section 5.1 forbids as the plan's central safety rule.

**B. Premium references a `TradingAgent.Core` library and calls a composition API.** Identical reuse
— premium still "holds" all community behavior and calls into it — but the thing it calls was built
to be called: `AddCore(services, config, options)`, `MapCoreEndpoints(endpoints)`,
`RegisterCoreTools(context, services, options)`. Points 1 and 2 disappear (a library does not
self-deploy and cannot be discovered; options give granularity where it is needed). The cost is the
file moves in step 1.

**C. Premium forks the repository.** Rejected in section 7.4 for drift. Not revisited.

**This plan takes B**, and the reason is narrow: approach A's own fix *is* approach B. Once you strip
`OutputPath` so the community project can be safely referenced, you have a library — and the only
question left is whether the module type lives in it or in a 150-line shim beside it. Putting the
module in the shim costs one small project and buys the thing A cannot have: a reuse surface with
parameters on it.

Note what is *not* a reason to prefer B: granularity is barely needed for the stated product. "All
community features plus premium ones" means premium excludes nothing, so `RegisterServices`
all-or-nothing would have been acceptable. B is chosen for the deployment-safety and boundary
reasons, not because the product needs fine-grained opt-outs today.

### 2.2 "Overridden endpoints" is not available — behavior extension is

Mapping `GET /trading/candles` a second time does not shadow the first. Two endpoints with the same
route template, same method, and equal precedence make the request ambiguous, and ASP.NET Core
routing throws `AmbiguousMatchException` at request time — a 500 on a route that worked before
premium was installed, not a clean override.

So premium has exactly three legitimate moves, and this plan supports all three:

- **Add** new routes (`/trading/premium/...`). Unrestricted, no seam needed.
- **Change what an existing route returns**, through DI — a provider the core endpoint already
  consults. Step 3b is this pattern: `/trading/candles` keeps one implementation and one route, and
  premium fills an `overlays` block that is empty in the community edition.
- **Replace** a specific core route, by making that one mapping opt-out in `TradingCompositionOptions`
  so the core does not map it and premium maps its own. Decided route by route, on evidence, never
  as a blanket mechanism — every opt-out is a place the two editions can silently diverge.

### 2.3 `TradingAgent.Abstractions` is deferred, not cancelled

An earlier draft split contracts into a third assembly so that, in the private repo, the strategy and
AI projects could reference *only* contracts and thus be unable to see `IBrokerAdapter`, `AhkBroker`,
or Puppeteer even by accident — compile-enforcing section 5.1.

That enforcement is worth having, but it enforces a rule against code that does not exist yet: the
premium strategy projects, and the section 5.2 seams they would consume, are Phase 0+ of the premium
roadmap. Building the assembly now means guessing its contents, splitting three files that mix a
contract with an implementation, and carrying a third package through step 6 — to protect nothing.

Revisit it when the section 5.2 seams are actually written. At that point the correct contents are
observable rather than guessed: whatever the strategy projects import is what belongs in
Abstractions. Carving it out of Core later is a pure move of already-isolated types, and it is
strictly easier then than now. Until then Core holds the contracts, and section 5.1's rule is
enforced by review rather than by the compiler — which must be stated in the premium repo's
CODEOWNERS review checklist so it is not simply forgotten.

### 2.4 Target project graph

Public repository (this one):

    src/AgentFox.Plugins/                     unchanged; becomes a packable contract assembly in step 6

    src/Plugins/TradingAgent.Core/            library — everything the plugin has today
      AhlAnalytics, Analysis, Broker, Channel/WhatsAppBridgeChannel, Config, Feed, Manager,
      Market, Models, Observability, Persistence, Reconciliation, Research, Risk, Safety,
      Tools, Trading, Watchlist, TradingTopics
      TradingAgentRuntime  (AddCore / Start / GetCorePages / RegisterCoreTools /
                            RegisterSpecialist / MapCoreEndpoints)
      Chart/IChartOverlayProvider + overlay DTOs  (step 3b)
      wwwroot (embedded)
      NO OutputPath override — must never produce a .deps.json in plugins/

    src/Plugins/TradingAgent/                 community entry plugin, ~150 lines
      TradingAgentModule.cs                   delegates to TradingAgentRuntime
      Channel/WhatsAppBridgeChannelProvider.cs (must stay here — see 1.2)
      ui/                                     builds into ../TradingAgent.Core/wwwroot

Private repository:

    src/TradingAgent.Premium.Strategies/      auto-trading, scoring, projections
    src/TradingAgent.Premium.ServiceClient/   optional hosted-decision client
    src/TradingAgent.Premium.Plugin/          references Core; the only deployed entry module
      Channel/WhatsAppBridgeChannelProvider.cs (its own, per 1.2)
      ui/                                     optional superset bundle (step 3b)
    tests/

---

## 3. Steps

Each step is a separate commit with its own green gate.

### Step 1 — create `TradingAgent.Core` and move the whole plugin body into it

New `TradingAgent.Core.csproj` carrying every `PackageReference` from the current
[TradingAgent.csproj](TradingAgent.csproj) except the UI-manifest property (that follows in step 5),
plus a `ProjectReference` to `AgentFox.Plugins`.

**No `OutputPath` override, and never one later.** This single property is what keeps Core invisible
to plugin discovery: a referenced library's `.deps.json` is not copied into the referencing project's
output, so Core lands beside the entry DLL as a plain dependency and
[Program.cs:1007](../../Agent/Program.cs#L1007) skips it. Give Core an `OutputPath` into `plugins/`
and it becomes a second entry plugin with its own load context — the failure in 1.1. Verifiable
today: `plugins/` holds `AgentFox.Plugins.dll` with no `AgentFox.Plugins.deps.json`.

`git mv` everything except the entry shim into Core: `AhlAnalytics/`, `Analysis/`, `Broker/`,
`Config/`, `Feed/`, `Manager/`, `Market/`, `Models/`, `Observability/`, `Persistence/`,
`Reconciliation/`, `Research/`, `Risk/`, `Safety/`, `Tools/`, `Trading/`, `Watchlist/`,
`TradingTopics.cs`, `AssemblyInfo.cs`, and `Channel/WhatsAppBridgeChannel.cs`. Leave in the entry
project only `TradingAgentModule.cs`, `Channel/WhatsAppBridgeChannelProvider.cs`, `ui/`, the docs,
and the csproj.

Because Abstractions is deferred (2.3), **no file needs splitting in this step** — including the
three that mix a contract with an implementation
([Broker/IBrokerAdapter.cs](Broker/IBrokerAdapter.cs) with `AhkBrowserBrokerAdapter`,
[Broker/IBrokerAccountReader.cs](Broker/IBrokerAccountReader.cs) with `AhkBrokerAccountReader`,
[Reconciliation/IBrokerStateReader.cs](Reconciliation/IBrokerStateReader.cs) with
`TradingReconciliationState`). They move whole. Nor does
[TechnicalSnapshot.cs:141](Analysis/TechnicalSnapshot.cs#L141) need its
`TechnicalOptions.From(TradingScanOptions)` factory rewritten, since `Config` and `Analysis` stay in
the same assembly. This step is a pure move: `git mv` plus one new csproj.

Entry csproj keeps only what the shim compiles against: `AgentFox.Plugins`,
`Microsoft.Extensions.*`, `Microsoft.Extensions.FileProviders.Embedded`, and the new
`ProjectReference` to Core. `CopyLocalLockFileAssemblies=true` stays, so Core and the whole
dependency closure still land next to the entry DLL.

Gate: solution builds; full suite green. The 38 trading test files should need **zero** edits —
if any needs a `using` change, a namespace was renamed and should be reverted. Then confirm the
build output: `plugins/TradingAgent.dll` + `plugins/TradingAgent.deps.json` +
`plugins/TradingAgent.Core.dll` and **no** `plugins/TradingAgent.Core.deps.json`.

### Step 2 — extract `TradingAgentRuntime` and reduce `TradingAgentModule` to a shim

In Core, add `TradingAgentRuntime` as the public composition surface, moving bodies verbatim:

| Moves from | To |
| --- | --- |
| `RegisterServices` body ([:131](TradingAgentModule.cs#L131)) | `TradingAgentRuntime.AddCore(IServiceCollection, IConfiguration)` |
| `MapEndpoints` body ([:267](TradingAgentModule.cs#L267)) | `TradingCoreEndpoints.MapCoreEndpoints(IEndpointRouteBuilder)` |
| `StartAsync` + `RegisterBrokerCredentialChangeListener` + `ConnectionFingerprint` | `TradingAgentRuntime.Start(IServiceProvider)` |
| `GetPages` body ([:101](TradingAgentModule.cs#L101)) | `TradingAgentRuntime.GetCorePages()` |
| `OnAgentReadyAsync` tool list + audit hooks ([:2169](TradingAgentModule.cs#L2169)) | `TradingAgentRuntime.RegisterCoreTools(IPluginContext, IServiceProvider, TradingCompositionOptions)` |
| `RegisterAgent` descriptor + `ContributeToSystemPrompt` + `BuildSpecialistToolNames` + `DescribeAllowedSymbols` | `TradingAgentRuntime.RegisterSpecialist(...)` |
| `ParseProposalOrders`, `AssessSymbolAsync`, `SerializeAlertForSse` | Core internals |
| the 15 request records at [:2602-2696](TradingAgentModule.cs#L2602) | Core (the endpoints model-bind them) |

`TradingAgentModule` then becomes roughly:

    public sealed class TradingAgentModule : IAgentAwareModule, IPluginUiContributor
    {
        private IServiceProvider? _services;
        public string Name => "trading-agent";
        public IEnumerable<PluginUiPage> GetPages() => TradingAgentRuntime.GetCorePages();
        public void RegisterServices(IServiceCollection s, IConfiguration c) => TradingAgentRuntime.AddCore(s, c);
        public void MapEndpoints(IEndpointRouteBuilder e) => TradingCoreEndpoints.MapCoreEndpoints(e);
        public Task StartAsync(IServiceProvider s) { _services = s; TradingAgentRuntime.Start(s); return Task.CompletedTask; }
        public Task OnAgentReadyAsync(IPluginContext ctx)
        {
            TradingAgentRuntime.RegisterCoreTools(ctx, _services!, TradingCompositionOptions.Community);
            TradingAgentRuntime.RegisterSpecialist(ctx, _services!, TradingCompositionOptions.Community);
            return Task.CompletedTask;
        }
    }

Two observable side effects to accept deliberately and record in the revision log:

- **Log category change.** 15 endpoint handlers take `ILogger<TradingAgentModule>`; in Core that
  becomes `ILogger<TradingCoreEndpoints>`, so the category moves from
  `TradingAgent.TradingAgentModule` to `TradingAgent.TradingCoreEndpoints`. No appsettings file
  filters on the old category today (verified), so nothing breaks — but any saved log query does.
- **`typeof(TradingAgentModule).Assembly` no longer owns `wwwroot`** — handled in step 5.

Gate: solution builds; full suite green; then a live smoke run — the dashboard loads, `/trading/status`
returns, one paper order crosses `TradingManager`, and the specialist answers one PSX question.

### Step 2b — split the endpoint file by area

`MapCoreEndpoints` is one ~1,860-line method. Split it into `partial class TradingCoreEndpoints`
files by `MapGroup` area (status/policy, orders/approvals, watchlist, charts/candles, alerts/SSE,
assessment, config/broker). Pure mechanical move, no logic edits. Two payoffs: the file becomes
reviewable, and premium endpoint additions stop conflicting with core edits in the same 2,000-line
region. Its own commit, so the diff is verifiably a move (decision 2).

### Step 3 — add the composition seams premium needs

`TradingCompositionOptions` (Core, public record) is the whole premium extension surface for this
phase. Keep it small and explicit; section 5.2's advice against a stringly-typed capability registry
applies here:

    public sealed record TradingCompositionOptions
    {
        public static TradingCompositionOptions Community { get; } = new();
        public string EditionName { get; init; } = "community";
        public IReadOnlyList<ITool> AdditionalTools { get; init; } = [];
        public IReadOnlyList<string> AdditionalSpecialistToolNames { get; init; } = [];
        public IReadOnlyList<string> AdditionalPrimaryReadToolNames { get; init; } = [];
        public string? SpecialistPromptAppendix { get; init; }
    }

Rules that keep this honest:

- Premium tools are registered through the same `RegisterAgentTool`/`RegisterTool` loop, so they land
  in the same audit `ownToolNames` set and the same `OnToolPreExecute`/`PostExecute`/`Error` hooks.
  Do not let the premium plugin call `context.RegisterAgentTool` itself and bypass the audit filter.
- The specialist prompt is *appended to*, never replaced. The core prompt carries the
  allowed-symbols and tradable-flag rules; a replacement would silently drop them.
- By default the premium plugin contributes **no** `PluginUiPage` and no `wwwroot`: the dashboard is
  the one Core serves via `GetCorePages()`, and premium reaches the screen through the step 3b
  overlay data contract. A premium-only *second page* is never the answer (1.3). If premium needs
  structural UI changes rather than chart data, it builds a superset bundle and *replaces* the
  `trading` page — see step 3b, "structural UI enhancements".
- No `ContributeCoreUi` flag is needed for that case, and none was added: `GetPages` is a member of
  the entry module, so a premium module simply returns its own page instead of calling
  `GetCorePages()`. One less field, and the choice sits where it is made. The rule it encodes still
  holds — exactly one edition may contribute slug `trading`.
- Additional pre-trade risk rules go through DI (`IPreTradeRiskRule` when section 5.2 lands), not
  through this options record.
- Premium may **add** routes freely but may not re-map a core route (2.2). If a specific core route
  must be replaced, add an explicit opt-out to this record for that one mapping, and record why.

Gate: build + suite. Community behavior must be byte-identical since `Community` is all-empty.

### Step 3b — the chart overlay contract: premium features on the *same* dashboard

This is how projections, predicted points, next target, and confidence bands land on the existing
chart without a line of premium markup and without a second page.

**Why this is cheap: the renderer already exists.** [ChartPane.svelte](ui/src/ChartPane.svelte) is
already fully data-driven, and it already has all three primitives premium needs:

| Existing code | Draws | Fed today by |
| --- | --- | --- |
| `drawLevels` ([:343](ui/src/ChartPane.svelte#L343)) | horizontal price lines, width/style/label encoded | `levels.supports/resistances` |
| `drawPlanMarkers` ([:410](ui/src/ChartPane.svelte#L410)) | entry / stop / **target** markers on a bar | `plan.entry/stop/target` |
| `addSeries(LineSeries)` + `setData` ([:255](ui/src/ChartPane.svelte#L255), [:318](ui/src/ChartPane.svelte#L318)) | overlay lines | per-candle `sma20`, `sma50`, `rsi14` |

So "next target on the chart" is not new UI architecture — the deterministic core already renders a
target. What is missing is a *generic, edition-neutral* channel so a premium provider can supply
values into those same three primitives.

**The contract.** Add to Core under `Chart/` (presentation DTOs; nothing here touches a broker,
so these move to Abstractions unchanged whenever 2.3 is revisited):

    public sealed record ChartOverlaySet(
        IReadOnlyList<ChartOverlayLevel>  Levels,   // -> createPriceLine
        IReadOnlyList<ChartOverlaySeries> Series,   // -> addSeries(LineSeries) + setData
        IReadOnlyList<ChartOverlayMarker> Markers,  // -> createSeriesMarkers
        IReadOnlyList<ChartOverlayBand>   Bands);   // -> two LineSeries, optionally filled

    public sealed record ChartOverlayLevel (string Id, string Label, decimal Price, string Kind, int Weight, bool Confirmed);
    public sealed record ChartOverlaySeries(string Id, string Label, string Kind, bool Dashed, IReadOnlyList<ChartOverlayPoint> Points);
    public sealed record ChartOverlayPoint (long Time, decimal Value);
    public sealed record ChartOverlayMarker(string Id, long Time, decimal? Value, string Text, string Position, string Kind);
    public sealed record ChartOverlayBand  (string Id, string Label, string Kind, IReadOnlyList<ChartOverlayBandPoint> Points);
    public sealed record ChartOverlayBandPoint(long Time, decimal Lower, decimal Upper);

`Kind` is a **semantic token**, never a color: `projection`, `prediction`, `target`, `entry`, `stop`,
`support`, `resistance`, `neutral`. The client maps it to a theme token exactly as `drawLevels` does
today via `token('--primary', …)`. If premium sent raw colors it would own the public theme and break
light/dark.

**The provider seam** (Core, `Chart/`):

    public interface IChartOverlayProvider
    {
        string Id { get; }
        Task<ChartOverlaySet?> GetOverlaysAsync(ChartOverlayRequest request, CancellationToken ct);
    }

    public sealed record ChartOverlayRequest(
        string Symbol, string Interval,
        long FirstBarTime, long LastBarTime, int BarCount,
        IReadOnlyList<long> NextSessionTimes);

Core's `/trading/candles` handler resolves `IEnumerable<IChartOverlayProvider>`, merges the results,
and adds one `overlays` block to the existing response — `{}` (empty, never null) in the community
edition, so the client has exactly one code path. Community behavior is unchanged: no provider is
registered, so the block is empty and nothing renders.

Five design rules that matter more than the DTO shape:

1. **Future timestamps come from the server, never the client.** A projection needs times *past* the
   last bar, and on a daily series those must be real PSX sessions — not weekends, not market
   holidays. `NextSessionTimes` is computed by Core from `PsxMarketCalendar`/`OrderWindow` and handed
   to the provider. A provider that invents `lastBar + 86400` will draw a target on a Sunday.
2. **The chart is a read path and must never be breakable by a model.** Wrap each provider in a
   short timeout and a try/catch: on timeout or throw, log a warning, drop that provider's overlays,
   and render the chart. Same failure direction as section 7.4 — degraded premium never takes down
   core function.
3. **Extend the visible-range headroom.** [ChartPane.svelte:333](ui/src/ChartPane.svelte#L333) sets
   `to: d.candles.length + 4`. A projection of *n* bars needs `to: length + max(4, n + 2)`, or the
   projected tail renders off-screen and reads as "the feature is broken".
4. **Overlays are presentation only — never an execution input.** Position sizing, the intent
   builder, and the risk engine read the server-side model directly. Nothing may size or trigger a
   trade from a value that made a round trip through the chart response.
5. **Anything in the overlay is public.** This is the sharpest constraint and it is *stronger* than
   the DLL-decompilation caveat in section 7.4: a DLL must be decompiled, whereas a JSON response is
   already readable in the browser's network tab. Emit the **conclusion** — a line, a target, a band
   — and never the features, weights, thresholds, scores, or model identity that produced it. A
   premium projection is defensible as a product because reproducing it requires the model, not
   because the drawn line is hidden.

**Optional but recommended:** migrate the existing `drawLevels` and `drawPlanMarkers` onto the same
overlay renderer, with Core emitting its deterministic levels and plan *as* overlays. One rendering
path instead of two means every future chart improvement applies to premium overlays automatically,
and it proves the contract against known-good data before any premium value flows through it. It is
churn on a working chart, so it is a separate commit and it is optional.

**Structural UI enhancements — when overlays are not enough.** Overlays cover anything that is
*values on the existing chart*. They cannot add a panel, a dialog, a new control, or a new tab. For
those, the right move follows the same reuse principle as the backend: the premium `ui/` project
**imports the public Svelte source and composes a superset bundle.**

    // private repo: src/TradingAgent.Premium.Plugin/ui/src/main.ts
    import TradingDashboard from '@agentfox/trading-ui/TradingDashboard.svelte';   // public source
    import ProjectionPanel  from './ProjectionPanel.svelte';                       // premium only

Premium builds that into its own `wwwroot`, embeds it, sets `ContributeCoreUi = false`, and
contributes the `trading` page itself. Properties of this route:

- Full reuse of the public UI — the same components, not a fork — and arbitrary premium UI on top.
- No public JS extension API to design, version, and keep stable.
- The dependency is a **compile-time** one, so a breaking change to a public component fails the
  premium build loudly instead of degrading a page at runtime. That is the good failure direction,
  but it does mean the premium bundle must be rebuilt whenever the public UI changes — budget for it
  in the release process rather than discovering it.
- The public `ui/` project must be consumable by an external build for this to work: either a sibling
  checkout during development, or an npm package published from this repo alongside the NuGet
  packages in step 6. Note that the public UI is currently a private, unpublished npm project, so
  this is real (small) work, not a given.
- Everything in that bundle is public regardless of which repo it was built in, so rule 5 above
  applies unchanged: premium UI may present conclusions, never the model behind them.

Start with overlays; add the superset bundle only when a premium feature genuinely needs new
structure. The two compose — a superset bundle still renders overlays through the inherited
`ChartPane` — so choosing overlays now costs nothing later.

A third option, a `window.tradingDashboard` extension API where the public page loads a
premium-served JS bundle at runtime, is **not** recommended: it means designing and versioning a
public front-end contract to achieve what a compile-time import already achieves, with weaker
failure modes.

Gate: a unit test that `/trading/candles` returns an empty `overlays` block with no providers
registered and merges two fake providers when they are; a test that a throwing provider and a
slow provider both leave the response otherwise intact; a test that `NextSessionTimes` skips a known
PSX holiday. Then visually: register a fake provider that draws a straight projection five sessions
out and confirm it renders on-screen, on real session dates, in both light and dark themes.

### Step 4 — the duplicate-edition guard

This must work **across load contexts**, so a `static bool` in Core cannot do it: two entry plugins
mean two copies of Core and two copies of every static. What *is* shared is the
`IServiceCollection` — [Program.cs:489](../../Agent/Program.cs#L489) passes the same instance to
every module. So compare by type **name**, not type identity:

    // Core, inside TradingAgentRuntime.AddCore, before anything else
    const string Marker = "TradingAgent.TradingCoreMarker";
    if (services.Any(d => d.ServiceType.FullName == Marker))
        throw new InvalidOperationException(
            "Two TradingAgent edition plugins are installed (community and premium). They are " +
            "mutually exclusive: each loads its own copy of the core, which would run duplicate " +
            "feed/monitor/reconciliation workers, two writers against the same ledger, and two " +
            "broker browser sessions. Remove one from the plugins folder.");
    services.AddSingleton<TradingCoreMarker>();

Fail fast and loud at startup is correct here — a half-working double install places duplicate
orders. Add a unit test that runs `AddCore` twice on one `ServiceCollection` and asserts the throw,
and assert the message names both editions (the operator needs to know which file to delete).

Optional hardening for the separate-process case, which the marker cannot see: have
`SqliteTradingRepository` take an exclusive advisory lock on a sidecar file next to the ledger at
startup, so a second AgentFox process against the same data directory also fails fast.

Gate: the new test, plus a manual check — drop a second copy of the entry DLL folder into `plugins/`
and confirm startup fails with that message rather than starting twice.

### Step 5 — move UI embedding into Core

- Move `GenerateEmbeddedFilesManifest` and the `wwwroot/**/*` `EmbeddedResource` item group from
  [TradingAgent.csproj](TradingAgent.csproj) to `TradingAgent.Core.csproj`.
- Point the Vite build at Core: `ui/vite.config.ts` `outDir` → `../../TradingAgent.Core/wwwroot`.
- `GetCorePages()` uses `typeof(TradingAgentRuntime).Assembly`. Keep the existing
  "no manifest / no index.html → yield break" behavior verbatim; a backend-only build must still
  produce a working plugin with no Trading page.
- Update the two build paths that name the UI output:
  [pack-local.ps1:91](../../../pack-local.ps1#L91) and
  [release.yml:113-120](../../../.github/workflows/release.yml#L113). The npm step still has to run
  **before** the publish, and the comment explaining why must move with it.
- Add both new projects to [AgentFox.sln](../../AgentFox.sln).

Gate: `pack-local.ps1` produces `plugins/TradingAgent/` containing `TradingAgent.dll` +
`TradingAgent.deps.json` + `TradingAgent.Core.dll` (and no `TradingAgent.Core.deps.json`), and the
existing pack verification (`no .deps.json → throw`) still passes. Then confirm
`/ext/trading` renders from the packed build — this is the step most likely to silently ship no UI.

### Step 6 — packaging for the private repository

Only now does the private repo become buildable from published artifacts.

- Make two projects packable: `AgentFox.Plugins` and `TradingAgent.Core`
  (`IsPackable=true`, `PackageId`, `Description`, `RepositoryUrl`, deterministic builds). Everything
  else stays `IsPackable=false`.
- Version them off the existing CI auto-increment scheme rather than inventing a second one, and
  watch for the known SDK double-suffix gotcha in the version properties.
- Publish to GitHub Packages from this repo's release workflow via OIDC/`GITHUB_TOKEN`. Do **not**
  grant this public repo read access to any private package — per section 7.3, forks of a public
  repo can reach private packages that the repo can reach.
- In the private repo: pin exact versions (`[1.4.2]`, never a floating range), keep the feed
  credential in Actions secrets, and add a packaging test that asserts the premium publish output
  contains exactly one `.deps.json` and that `TradingAgent.Core.dll` sits beside it. That single
  assertion is what proves the premium plugin loads as one context.
- Interim, before packaging exists: the private repo can consume the three projects as
  `ProjectReference` against a sibling checkout of this repo, pinned by commit. Fine for
  development, not for release — a release must be reproducible from a version number.

### Step 7 — private-repo skeleton (in the private repo, listed for completeness)

Four projects per section 2, the mutual-exclusion guard exercised in its tests, and CI configured
per section 7.3 (branch protection, CODEOWNERS, secret scanning, restricted Actions permissions).
Nothing from this list belongs in the public repository — including the premium `appsettings`
fragment, the strategy prompts, and the backtest datasets.

---

## 4. Verification

`dotnet test` does not work on the .NET 10 SDK here. Build and run the MSTest executable:

    dotnet build src/AgentFox.sln
    dotnet build tests\AgentFox.ChannelTests\AgentFox.ChannelTests.csproj
    & "tests\AgentFox.ChannelTests\bin\Debug\net10.0\AgentFox.ChannelTests.exe"

`PsxListingStatusTests.NormalizeStockSymbol_RemovesErroneousCelSuffix` is a known pre-existing
failure and is not caused by this refactor.

Per-step gates are listed above. The refactor-specific checks worth adding permanently:

1. `AddCore` twice on one `ServiceCollection` throws, and the message names both editions.
2. `TradingAgent.Core` produces no `.deps.json` in the plugins output — assert over the build output,
   because an `OutputPath` added later would silently turn Core into a second entry plugin (1.1).
3. The publish output of the entry plugin contains exactly one `.deps.json`.
4. `GetCorePages()` returns a page whose `index.html` exists, and returns empty (does not throw) for
   a build with no `wwwroot`.

Live smoke after step 2 and again after step 5, against the remote instance at `10.40.0.20:8080`:
dashboard loads, watchlist and chart panels populate, `/trading/status` shows the same
`liveExecutionReady` computation as before, one paper order crosses `TradingManager` and lands in the
ledger, the specialist answers one PSX question with `scan_watchlist` in the tool trace, and the
WhatsApp bridge channel still appears in the channels UI (the 1.2 failure mode is silent).

---

## 5. Do not

- Do not rename namespaces. `TradingAgent.*` stays; that is what makes this refactor cheap.
- Do not have the premium plugin reference the community **entry plugin** — reference
  `TradingAgent.Core` (2.1). An entry plugin's purpose is to be found by an assembly scanner;
  building on one invites two entries in `plugins/` and the 1.1 duplication failure.
- Do not try to override a core route by mapping it again. Identical template + method = ambiguous
  match = a 500 at request time (2.2). Extend behavior through DI, or add an explicit per-route
  opt-out.
- Do not move `WhatsAppBridgeChannelProvider` (or any `IChannelProvider`) into Core — see 1.2.
- Do not give `TradingAgent.Core` an `OutputPath` into `plugins/`, or let it produce a `.deps.json`
  there; that makes the loader treat a dependency as an entry plugin and give it a stray load context.
- Do not resurrect `TradingAgent.Abstractions` before the section 5.2 seams are written (2.3). When
  it is resurrected, `IBrokerAdapter`, `IBrokerStateReader`, `ITradingRepository`, and anything
  Puppeteer- or SQLite-shaped stay behind in Core — the point of that assembly is that strategy code
  cannot see them.
- Do not let the premium plugin register tools or endpoints outside the seams in step 3; that is how
  the audit trail and the specialist allowlist get bypassed.
- Do not solve edition composition with reflection over private fields, a static service locator, or
  duplicate HTTP calls to the management API (section 7.1).
- Do not add a private submodule to this repository (section 7.3).
- Do not treat a shipped DLL as a secrecy boundary. If a strategy is genuinely valuable IP, it
  belongs behind the section 7.4 hosted-decision service — with the latency budget and the
  expired-candidate rate published, per the 2026-08-19 revision.

---

## 6. Decisions — settled 2026-08-21

1. **The premium entry module reuses the name `trading-agent`.** Every existing
   `Modules` / `DisabledModules` value and every saved plugin-config overlay keeps working unchanged,
   and the two editions are mutually exclusive anyway. Because the name no longer distinguishes the
   editions, `TradingCompositionOptions.EditionName` must appear in the "Ready." startup log line and
   in the `/trading/status` response, or an operator cannot tell from the logs which edition is
   running.
2. **Step 2b is in scope**, immediately after step 2, as its own move-only commit.
3. **Steps 1-5 land first; step 6 (packaging) follows as a separate change.** Until step 6 exists,
   the private repo consumes the two public projects as `ProjectReference` against a sibling
   checkout pinned by commit — development only, never a release.
4. **Two public projects, not three** — `TradingAgent.Core` + the community entry shim.
   `TradingAgent.Abstractions` is deferred until the section 5.2 seams exist (2.3).
5. **Premium references `TradingAgent.Core`, not the community entry plugin** (2.1, approach B), and
   extends behavior through DI rather than by re-mapping routes (2.2).

### Order of work

    1 → 2 → 2b → 3 → 3b → 4 → 5       then, separately,  6 → 7

Steps 3b and 4 are the two that add new behavior; everything else is a move or a delegation. Step 5
is the one most likely to fail silently (no UI shipped, or the channel provider vanishing), so its
gate is a packed-build check rather than a unit test.
