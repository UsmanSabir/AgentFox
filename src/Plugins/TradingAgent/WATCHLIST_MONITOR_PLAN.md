# Watchlist, Charts, Monitoring & Actions — Implementation Plan

Plan for the next TradingAgent increment: an editable watchlist, per-symbol charts with
support/resistance overlays, continuous trend/level monitoring with alerts, on-demand LLM
confidence, and one-click actions — **without adding a single trading dependency to the AgentFox
host (backend or frontend)**.

Written against the code as of `dev` @ `12614b9`.

---

## 0. Where we stand today

**Backend isolation is already good.** `TradingAgentModule` is a self-contained `IAgentAwareModule`:
it has its own csproj/NuGet closure, loads into its own `PluginLoadContext`, keeps its own SQLite
ledger (`trading/trading.db`), registers four `IHostedService` workers, and maps its own
`/api/trading/*` endpoints via `IAppModule.MapEndpoints`. Nothing in `src/Agent` mentions trading.

**Frontend isolation does not exist.** Four concrete couplings:

| Coupling | File |
| --- | --- |
| The whole trading page lives in the host SPA | [+page.svelte](../../frontend/src/routes/trading/+page.svelte) |
| 6 trading interfaces + `api.trading` namespace | [api.ts:360-475, 662-675](../../frontend/src/lib/api.ts#L360) |
| Hardcoded nav entry | [Sidebar.svelte:32](../../frontend/src/lib/components/Sidebar.svelte#L32) |
| Any chart library would land in the host `package.json` | [package.json](../../frontend/package.json) |

**What we can build on (all already implemented):**

- `TechnicalAnalyzer` → `TechnicalSnapshot`: pivots, clustered `PriceLevel`s with touch counts,
  SMA20/50, RSI14, ATR14, range position, `PriceZone`, `TradeSetup`, entry/stop/target + R:R,
  consecutive up/down days, new range high/low. **This is the event-detection engine already.**
- `MultiTimeframeAnalyzer` → daily+weekly confluence, `EntryLevelConfirmedWeekly`, `WeeklyBreakdown`,
  `TimeframeAlignment`.
- `CandleHistoryProvider` — archive-first daily history, portal top-up, live forming bar.
- `PsxDataClient` — **one market-wide request per trading DAY** (all symbols), 60s-cached market
  watch, intraday ticks + `AggregateTicks` + `SupportedIntervals` (`1D/60m/15m/5m`).
- `daily_bars` / `daily_bar_coverage` / `intraday_bars` tables; resumable throttle-aware
  `CandleBackfillRunner`.
- `TradingManager` + `TradingRiskEngine` + `ApprovalIntentRegistry` + durable idempotency + audit
  events — the only sanctioned execution path.
- `PendingTakeProfitStore` + `TakeProfitRetryWorker` — a working pattern for "resting exit order".
- `ManagementRoles`: Viewer / Analyst / Trader / RiskManager / Administrator.

---

## 1. The isolation challenge

### 1.1 Backend — keep the existing discipline, add nothing to the host

Every item below stays inside `src/Plugins/TradingAgent`:

- **Schema**: extend the one `CREATE TABLE IF NOT EXISTS` block in
  [SqliteTradingRepository.cs:576](Persistence/SqliteTradingRepository.cs#L576). New tables only —
  no host storage.
- **Config**: new sections on `TradingAgentOptions` + new fields in
  `TradingPluginConfigDefinitionProvider` (the host already renders that schema generically on the
  Plugins page).
- **Workers**: `services.AddHostedService<WatchlistMonitorWorker>()` in `RegisterServices` — proven
  four times over.
- **Endpoints**: new routes on the existing `/trading` group.
- **Alert delivery** — three isolated options, in order of preference:
  1. Plugin-owned SSE: `GET /trading/alerts/stream`, consumed by the plugin's own UI. Zero host
     contact.
  2. Plugin-owned WhatsApp channel (`WhatsAppBridgeChannel`) for push off-screen.
  3. AgentFox chat notification via `IAgentService` — already an SDK contract and registered in host
     DI ([Program.cs:413](../../Agent/Program.cs#L413)), so resolving it is not new coupling. Use for
     "tell me in chat" only, behind a flag.

  Do **not** reach for the host's `PendingNotificationStore` — it lives in `AgentFox.Agents`, not the
  plugin SDK, and depending on it would create exactly the coupling we're avoiding.

### 1.2 Frontend — one small, generic host extension point (build once, reused by every plugin)

The trading UI becomes a **plugin-hosted micro-frontend**. The host gains a generic plugin-UI
mechanism; it never learns what a candle is.

**New in `AgentFox.Plugins` (SDK):**

```csharp
public interface IPluginUiContributor
{
    IEnumerable<PluginUiPage> GetPages();
}

public sealed class PluginUiPage
{
    public required string Slug  { get; init; }   // "trading"  → /ext/trading
    public required string Title { get; init; }   // "Trading"
    public string Icon        { get; init; } = "";      // lucide icon name, resolved host-side
    public string EntryPath   { get; init; } = "index.html";
    public string RequiredRole{ get; init; } = "Viewer";// hides the nav entry for lesser roles
    /// Assets for this page. Typically a ManifestEmbeddedFileProvider over the plugin assembly.
    public required IFileProvider Assets { get; init; }
}
```

**New in the host (≈80 lines, one time):**

1. `GET /api/plugin-ui` → the contributed page list (no `Assets`, obviously).
2. Static serving per page at **`/plugin-assets/{slug}/…`**, unauthenticated — same posture as
   `wwwroot` today. Only JS/CSS/HTML; every data call still hits authorized `/api/trading/*`.
   *(Learned during Phase 0: assets must NOT be served under `/ext/{slug}`. The static-file
   middleware runs before routing, so it answered `/ext/trading` with the plugin's raw `index.html`
   and the user lost the AgentFox sidebar and header entirely. `PluginUiPaths` now names both
   prefixes so the split can't be re-collapsed by accident.)*
3. `Sidebar.svelte` inserts entries fetched from `/api/plugin-ui` between the built-in pages and
   Settings, which stays last.
4. One generic route `src/routes/ext/[slug]/+page.svelte`:

```svelte
<iframe src={`/ext/${slug}/index.html`} title={title} />
```

**New in the plugin:** `src/Plugins/TradingAgent/ui/` — its own npm project, its own
`package.json`, its own chart library. Built to `TradingAgent/wwwroot/`, embedded via
`<EmbeddedResource Include="wwwroot\**\*" />` (mirroring
[AgentFox.csproj:58](../../Agent/AgentFox.csproj#L58)), served through
`ManifestEmbeddedFileProvider` from the plugin assembly.

**Auth inside the iframe:** the iframe is same-origin, so it shares the tab's `sessionStorage` and
reads `agentfox.managementApiKey` exactly as `api.ts` does — no new auth path. Belt-and-braces: the
host also `postMessage`s `{ apiKey, theme }` on load, which is how the plugin picks up the light/dark
theme too (the plugin ships its own copy of the CSS custom properties).

**Chart library — `lightweight-charts`** (TradingView's open-source library, ~45 kB, MIT-ish
Apache-2.0). Purpose-built for candles + horizontal price lines + markers + a second pane, which is
precisely the S/R-overlay job. It lands in the *plugin's* `package.json`, never the host's.

**Migration (Phase 0):** move `routes/trading/+page.svelte` into `ui/`, delete the trading
interfaces and `api.trading` from `api.ts`, drop the hardcoded Sidebar entry. After Phase 0 the host
frontend has **zero** trading references, and the current page still works — proving the mechanism
before any new feature is built on it.

**Escape hatch, if the iframe ever chafes** (deep links, shared toasts): keep the same
`/ext/{slug}` asset path and manifest endpoint, but have the plugin export
`mount(element, ctx) → dispose()` from an ESM bundle that the host `import()`s into a **shadow root**.
Same isolation, no iframe. Both options share Phase 0's plumbing, so choosing the iframe now costs
nothing later. Recommend the iframe first — the trading page is a full-page dashboard, not an inline
widget.

**Release wiring:** the plugin UI build must run wherever `src/frontend` is built today
(`install.ps1` / `RELEASING.md` / CI). One extra `npm ci && npm run build` in `ui/`.

---

## 2. Feature 1 — Watchlist (independent, editable, resettable)

### 2.1 The constraint that shapes this feature

`TradingRiskEngine` reads `AllowedSymbols` straight from `IOptions<TradingAgentOptions>`
([TradingRiskEngine.cs:33](Risk/TradingRiskEngine.cs#L33)) — appsettings, **not** the runtime
overlay. And `CandleHistoryProvider` archives history for `AllowedSymbols` only
([CandleHistoryProvider.cs:55](Research/CandleHistoryProvider.cs#L55)).

So a watchlist symbol outside `AllowedSymbols` can be charted, scanned and alerted on, but **any
order for it is rejected**, and it has no deep archive, so **no weekly confirmation**. That must be
explicit in the UI, not discovered at order time.

**Resolution — split the two universes and name them:**

```csharp
// New: TradingAgent/Watchlist/MonitoredUniverse.cs
public sealed class MonitoredUniverse
{
    IReadOnlyList<string> ForMonitoring();  // watchlist ∪ AllowedSymbols  → scan, chart, alerts
    IReadOnlyList<string> ForArchive();     // same — deep history for everything we monitor
    IReadOnlyList<string> ForExecution();   // AllowedSymbols ONLY — unchanged, risk engine's view
}
```

Wire `ForArchive()` into `CandleHistoryProvider` and `CandleBackfillRunner` (so added symbols get
their two years of history and therefore weekly levels), and `ForMonitoring()` into
`ScanWatchlistTool`. `ForExecution()` stays exactly as it is — **the risk engine is not touched**.

Widening the archive costs nothing extra in requests: a session request already returns every symbol
in the market; only local rows increase.

Later, optionally: make `AllowedSymbols` runtime-editable through the plugin-config overlay under an
`Administrator` role, audited. Deliberately out of scope here — expanding the tradable universe from
a web button deserves its own decision.

### 2.2 Storage

```sql
CREATE TABLE IF NOT EXISTS watchlist (
  symbol        TEXT PRIMARY KEY,
  added_utc     TEXT NOT NULL,
  source        TEXT NOT NULL,      -- 'seed' | 'user'
  sort_order    INTEGER NOT NULL DEFAULT 0,
  alerts_enabled INTEGER NOT NULL DEFAULT 1,
  notes         TEXT
);
CREATE TABLE IF NOT EXISTS watchlist_meta (   -- single row
  seeded_utc    TEXT,
  seed_hash     TEXT              -- hash of AllowedSymbols at seed time; detects config drift
);
```

Seeded from `AllowedSymbols` on first start (empty table + no `seeded_utc`). Never re-seeded
automatically — the point is independence. `seed_hash` lets the UI say "the configured allowed list
changed since seeding" and offer a reset.

### 2.3 Endpoints (`/api/trading/watchlist`)

| Verb | Route | Role | Notes |
| --- | --- | --- | --- |
| GET | `/watchlist` | Viewer | symbols + `tradable`, `hasWeeklyHistory`, `archivedBars`, latest snapshot summary (zone/setup/change%), open alert count |
| POST | `/watchlist` | Analyst | `{ symbol }`. Validated against live market-watch keys so typos fail fast; 409 on duplicate |
| DELETE | `/watchlist/{symbol}` | Analyst | keeps archived bars and alert history (audit) |
| POST | `/watchlist/reset` | Analyst | clear + reseed from `AllowedSymbols`, re-stamp `seed_hash` |
| PATCH | `/watchlist/{symbol}` | Analyst | `alerts_enabled`, `notes`, `sort_order` |

### 2.4 UI

Left rail list: symbol, last price + day %, zone/setup chip, alert dot, monitor-only badge when
`!tradable`, mute toggle. Header: add-symbol input with market-watch autocomplete, "Reset to allowed
list" (confirm dialog naming what changes). Selecting a row drives the chart pane.

---

## 3. Feature 2 — Chart

### 3.1 Endpoint

```
GET /api/trading/candles?symbol=OGDC&interval=1D&bars=250
```

Returns everything needed to draw the decision, computed by existing code:

```jsonc
{
  "symbol": "OGDC", "interval": "1D", "usesLiveBar": true,
  "candles":  [{ "t": "2026-08-11", "o":.., "h":.., "l":.., "c":.., "v":.. }],
  "overlays": {
    "sma20": [...], "sma50": [...], "rsi14": [...],
    "supports":    [{ "price":.., "touches": 4, "origin": "pivot", "weeklyConfirmed": true }],
    "resistances": [...],
    "suggested":   { "entry":.., "stop":.., "target":.., "rewardRisk": 2.4 }
  },
  "snapshot":  { /* TechnicalSnapshot */ },
  "weekly":    { /* MultiTimeframeView: alignment, confirmed levels, weeklyBreakdown */ },
  "warnings":  ["…"], "retrievedAtUtc": "…"
}
```

`1D` → `CandleHistoryProvider.GetDailyAsync` (archive-first). Intraday → `GetIntradayBarsAsync`
(archive) + current session rebuilt from `GetIntradayTicksAsync` + `AggregateTicks`. Reuse
`PsxDataClient.ResolveInterval` for validation. Response-cache intraday for `MarketWatchCacheSeconds`.

*Phase 2 as built, plus two things worth recording:*

- The loading/analysis was **extracted into `CandleAnalysisService`** and `analyze_candles` refactored
  onto it (its output shape unchanged), rather than the endpoint growing a second implementation of
  level discovery. One source of truth for support and resistance.
- `weekly.entryLevelConfirmed` turned out to describe the **full-history** nearest support, not the
  entry shown for the requested window — so the chart was about to print "no weekly confirmation"
  beside a level the level list marked confirmed. The response now carries
  `plan.entryWeeklyConfirmed` for the displayed plan, and both fields are documented.

### 3.2 UI

`lightweight-charts`: candlestick series + volume histogram; horizontal price lines for each S/R
(line width ∝ `touches`, dashed when weekly-unconfirmed, solid when confirmed); entry/stop/target
markers; SMA20/50 overlays; RSI pane with the configured oversold/overbought bands; interval
switcher (1D / 60m / 15m / 5m); a "live bar forming" indicator driven by `usesLiveBar`; a levels
legend listing price, touches, distance %, weekly-confirmed. Warnings rendered verbatim — they carry
real caveats (portal fallback, no weekly structure).

---

## 4. Feature 3 — Monitoring & alerts

### 4.1 `WatchlistMonitorWorker` (new `BackgroundService`)

Cadence, driven by `IMarketCalendar` / `PsxMarketClock`:

- market open → every `Monitor.IntervalSeconds` (default 120)
- after close → one settle pass (final daily bars, archive top-up, end-of-day transitions)
- market closed otherwise → idle

Per pass:

1. `MonitoredUniverse.ForMonitoring()`
2. Daily history from the archive (local read) + **one** market-watch snapshot for the whole
   universe → forming daily bar
3. `TechnicalAnalyzer.Analyze` + `MultiTimeframeAnalyzer.Analyze` per symbol
4. Archive completed intraday bars (`ArchiveIntradayBars` already exists) so intraday history accrues
5. Diff against persisted per-symbol state → emit transitions

**Cost:** one market-wide HTTP request per pass regardless of symbol count, plus local reads. That
is what makes 100+ symbols at 2-minute cadence viable. **Never add a per-symbol fetch to this loop** —
share `PsxDataClient` and its caches, and respect the throttle detection `CandleBackfillRunner`
already implements.

### 4.2 Event catalogue (all derivable from existing snapshot fields)

| Kind | Condition | Severity |
| --- | --- | --- |
| `SupportBounce` | prior zone `AtSupport`/`LowerRange`, now `ConsecutiveUpDays ≥ 1` off the level, RSI turning up from ≤ `RsiOversold`, `VolumeRatio ≥ Monitor.VolumeConfirmRatio` | High (the bullish-from-support case) |
| `ResistanceRejection` | zone `AtResistance` + reversal bar / `ConsecutiveDownDays ≥ 1` | High (at-resistance-and-turning-down) |
| `SupportBreak` | close < support × (1 − `BreakBufferPercent`), volume-confirmed | High |
| `ResistanceBreakout` | close > resistance × (1 + `BreakBufferPercent`), volume-confirmed | High |
| `SetupChanged` | `TradeSetup` transition (e.g. → `AvoidBreakdown`) | Medium |
| `TrendFlip` | SMA20/SMA50 cross | Medium |
| `WeeklyBreakdown` | `MultiTimeframeView.WeeklyBreakdown` turns true | High |
| `StopBreach` / `TargetReached` | held position crosses its recorded stop / target | Critical / High |
| `RsiExtreme` | crosses `RsiOversold` / `RsiOverbought` | Low |

Position-aware kinds join `get_portfolio` holdings — cache holdings per pass; the AHK browser call is
expensive, so refresh it on a slower timer (`Monitor.HoldingsRefreshMinutes`, default 15) and mark
alerts with the holdings age.

### 4.3 Noise control (the make-or-break detail)

*Phase 3 correction, found by the tests:* the plan assumed one confirmation rule for everything. It
does not work. State is rewritten at the end of every pass, so a **change-vs-previous** signal (setup
changed, SMA cross, RSI band entry, weekly breakdown) exists for exactly one pass — gating it behind
`ConfirmPasses` would not delay it, it would make it **unfireable**. Sustained conditions (bouncing off
support, making fresh lows on volume) do persist and are streak-confirmed as planned. Edges fire
immediately and rely on the durable cooldown instead. Break detection likewise reads its level from the
current snapshot — the analyzer already reclassifies a broken support as overhead resistance — rather
than from a remembered level that drifts on the very next pass.

- **Transitions, not conditions.** Persist `watchlist_state(symbol, zone, setup, level_price,
  sma_relation, rsi_band, updated_utc)`; fire only on change.
- **Confirmation:** the condition must hold for `Monitor.ConfirmPasses` consecutive passes
  (default 2) before an alert is emitted. Kills flicker when price sits exactly on a level.
- **Buffer:** `BreakBufferPercent` (default 0.5) so a wick through a level isn't a break.
- **Cooldown:** at most one alert per `(symbol, kind, rounded level)` per
  `Monitor.CooldownMinutes` (default: rest of session) — same principle as `DuplicateSignalFilter`.
- **Mute** per symbol via `alerts_enabled`.

### 4.4 Storage & delivery

```sql
CREATE TABLE IF NOT EXISTS watchlist_alerts (
  alert_id     TEXT PRIMARY KEY,
  symbol       TEXT NOT NULL,
  kind         TEXT NOT NULL,
  severity     TEXT NOT NULL,
  level_price  REAL, price REAL, interval TEXT,
  raised_utc   TEXT NOT NULL,
  session_date TEXT NOT NULL,
  evidence_json TEXT NOT NULL,     -- snapshot + multi-timeframe view + reasons at raise time
  state        TEXT NOT NULL,      -- new | acknowledged | dismissed | acted | expired
  assessment_json TEXT,            -- §5, filled on demand
  proposal_id  TEXT, execution_id TEXT   -- §6 linkage
);
CREATE TABLE IF NOT EXISTS watchlist_state (...);
```

Append-only; state transitions update in place. Evidence is snapshotted at raise time so an alert
stays explicable after the market moves on.

| Verb | Route | Role |
| --- | --- | --- |
| GET | `/alerts?symbol=&state=&since=&limit=` | Viewer |
| GET | `/alerts/stream` (SSE) | Viewer |
| POST | `/alerts/{id}/ack` \| `/dismiss` | Analyst |
| GET | `/monitor/status` | Viewer — last pass, symbols covered, throttle state, holdings age |

SSE follows the pattern the host already uses for `/chat/events` (`fetch`-based reader, not
`EventSource`, so the API-key header can be sent). Optional fan-out to WhatsApp and (flagged) chat.

### 4.5 UI

Watchlist rows highlight by severity and glow on a fresh alert; an alerts panel groups by symbol with
the evidence reasons, level, distance, and time; filters by kind/severity/state. The chart pane draws
a marker at the alert's bar.

---

## 5. Feature 4 — LLM confidence, on demand

```
POST /api/trading/alerts/{id}/assess     → Analyst
POST /api/trading/assess                 → Analyst   { symbol, interval }  (chart-pane button)
```

Deterministic-first, unchanged invariant: **every number comes from `TechnicalAnalyzer`; the model
only judges.** Input: snapshot + multi-timeframe view + news evidence (`ResearchNewsEnabled` path) +
portfolio context. Output, structured and persisted onto the alert:

```jsonc
{ "confidence": "HIGH|MEDIUM|LOW", "recommendation": "BUY|SELL|HOLD|AVOID",
  "reasons": ["…"], "risks": ["…"], "invalidationLevel": 123.4,
  "modelKey": "CheapModel", "assessedUtc": "…" }
```

Runs on `ParserModelKey` (cheap model), cached per `(symbol, level, session)` so repeat clicks are
free. **Never** auto-run per alert — cost and rate limits. Optional
`Monitor.AutoAssessMinSeverity = High` for the few that matter.

Refactor note: `ResearchStockTool` already does this assessment. Extract a
`StockAssessmentService` used by both the tool and the endpoint rather than duplicating the prompt —
one place where the confidence rubric lives.

---

## 6. Feature 5 — Actions (buy / sell / stop-loss)

**Non-negotiable:** the UI gets **no new execution path**. Every action goes through
`TradingManager` → `TradingRiskEngine` → `AhkBrowserBrokerAdapter`, so kill switch, execution mode,
market calendar, reconciliation health, `AllowedSymbols`, batch/value caps, idempotency and audit
events all apply untouched.

### 6.1 Proposals — the complaint is correct, and here's the fix

Verified in code: `trade_proposals` is **write-only**. `CreateProposalAsync` inserts
([SqliteTradingRepository.cs:88](Persistence/SqliteTradingRepository.cs#L88)), `GetProposalsAsync`
lists for the UI, `GetStatusAsync` counts `status IN ('proposed','awaiting_approval')` — and
**nothing ever transitions a row**. The `approvals` table is created
([:613](Persistence/SqliteTradingRepository.cs#L613)) and never written to. So every proposal sits at
`proposed` forever and the "pending proposals" metric only ever climbs. It is exactly the dumb
growing log you described.

The use case it was *reaching for* is real, though, and it's the one thing the UI can't do today:
**a WhatsApp signal that arrives while you're away.** Bridge → specialist → `parse_signal` →
`research_stock` → proposal. Without a lifecycle there's no way to act on that later; with one, the
proposals tab becomes a **signal inbox**: "3 pending from last night — execute, or discard."

So: keep the table, give it a lifecycle, and make it self-cleaning. It stops being a log and becomes
a queue.

```
proposed ──(execute)──► executing ──► executed        (linked to execution_id)
    │
    ├──(reject)───────► rejected     (reason recorded)
    └──(session end / TTL)──► expired
```

- `POST /proposals/{id}/execute` → **Trader** — hands the proposal's orders to `TradingManager` with
  idempotency key `hash(proposalId)`; writes `execution_id` back onto the row.
- `POST /proposals/{id}/reject` → Analyst — `{ reason }`, terminal.
- A **sweeper in `WatchlistMonitorWorker`** expires anything still `proposed` past
  `Proposals.TtlHours` (default 24) or whose stated entry has moved more than
  `Proposals.InvalidateOnDriftPercent` (default 3) from the live price — a stale price is not a
  tradable plan. Expiry is a state change with a reason, not a delete: the audit trail survives.
- `Proposals.RetentionDays` (default 90) prunes terminal rows so the table has a ceiling.
- The UI shows **only non-terminal** proposals by default, with a history toggle. An empty inbox is
  the normal state.

Alerts, by contrast, do **not** route through proposals. An alert already carries its own evidence, so
the alert card gets Buy / Sell / Set-stop buttons that hit `/orders` directly. Proposals exist for
signals that arrived while nobody was watching; alerts are acted on live.

### 6.2 Order endpoints

| Verb | Route | Role | Behaviour |
| --- | --- | --- | --- |
| POST | `/orders` | **Trader** | `{ symbol, side, type, price, qty, alertId?, proposalId? }`. Idempotency key = hash of `(source, symbol, side, price, session)`. Returns either a completed execution or an `ApprovalIntent`, per §6.3. |
| POST | `/orders/{intentId}/confirm` | **Trader** / RiskManager | Redeems the intent inside `ApprovalIntentTtlSeconds`. |
| POST | `/orders/{intentId}/cancel` | Trader | Drops the intent. |
| POST | `/positions/{symbol}/stop` | **Trader** | Arms a stop-loss (§6.4). |
| DELETE | `/positions/{symbol}/stop` | Trader | Disarms it. |
| GET | `/exits` | Viewer | Armed stops + queued take-profits and their state. |

The UI must show, before the confirm button, exactly what the risk engine will check: mode, kill
switch, market open, reconciliation freshness, `tradable`, order value vs `MaxOrderValuePkr`. A
rejection should never be the first time the operator learns a gate exists.

### 6.3 Approval bypass — pre-approve, auto-approve, and "armed" windows

`ApprovalRequired` today means every order needs a fresh confirmation. That's right for a cold start
and wrong for an operator sitting in front of the screen watching a level break. Three bypass modes,
all inside the existing `ApprovalIntentRegistry` — the intent is still created and audited, it just
gets redeemed automatically when a rule says so:

```jsonc
"Approval": {
  "Mode": "Always",              // Always | Auto | Armed
  "Auto": {                       // Mode=Auto: redeem immediately when ALL caps hold
    "MaxOrderValuePkr": 25000,
    "MaxOrdersPerSession": 5,
    "Sides": ["SELL"],            // exits-only is the safe starting point
    "Symbols": [],                // empty = any tradable symbol
    "MinAlertSeverity": "High",   // only from a High/Critical alert, never ad-hoc
    "RequireMarketOpen": true
  },
  "Armed": { "DefaultMinutes": 30, "MaxMinutes": 120 }
}
```

- **`Always`** — current behaviour, the default. Nothing changes for existing installs.
- **`Auto`** — a *pre-approval*: orders matching every cap are redeemed without a prompt; anything
  outside them still prompts. Note the default `Sides: ["SELL"]` — auto-approving exits (stop-loss,
  take-profit) is a materially smaller risk than auto-approving entries, so that's the shipped
  default and entries are opt-in.
- **`Armed`** — a sudo-style window: `POST /approval/arm { minutes }` (**RiskManager**) suspends
  prompting for that long, surfaced as a countdown banner with a one-click disarm, and auto-disarms
  on kill-switch activation, on process restart, and at market close.

Kill switch, `AllowedSymbols`, calendar, reconciliation health and value caps are **never** bypassable
— those are the risk engine's, not the approval layer's. Every auto-redeemed intent writes an audit
event recording *which rule* redeemed it, so a bypassed order is as traceable as a confirmed one.

### 6.4 Stop-loss — a trigger price plus a limit order

Correcting the earlier framing: a stop-loss is a **trigger** (`sell if the price drops below N`) that
submits a **limit order** when breached. Two layers, and we want both:

**(a) Native, if the portal has it.** The adapter only ever selects `"Limit"` or `"Market"` in
`#buyordertype` / `#sellordertype` ([AhkBroker.cs:1584](Broker/AhkBroker.cs#L1584)) — we've never
enumerated what else that dropdown offers, and the dialog already carries *two* price fields
(`#buyprice` **and** `#buylimitprice`), which is the shape a trigger+limit order takes. First task is
therefore **discovery, not code**: a `probe_order_form` diagnostic that opens both dialogs, dumps the
full option list of the type selects plus every input id, and logs it. If a stop/SL-limit type
exists, drive it — a stop resting at the broker survives our process being down, which no
locally-monitored stop can.

**(b) Monitored trigger, always.** Independent of (a), because it also covers symbols and brokers
without native stops:

```csharp
// New table: armed_exits(symbol, kind, trigger_price, limit_price, qty, armed_utc,
//                        expires_utc, state, source_alert_id, execution_id)
```

`WatchlistMonitorWorker` evaluates armed exits every pass. On `last <= trigger` (sell-stop) it
submits a **LIMIT SELL** at `limit_price` — defaulted to `trigger × (1 − Exits.SlippagePercent)`
(default 1.0) so it actually fills in a falling market instead of resting above the bid — through
`TradingManager`, subject to §6.3. Symmetrically for a buy-stop above `trigger`.

Generalize `PendingTakeProfitStore` / `TakeProfitRetryWorker` into `PendingExitStore` with
`Kind = TakeProfit | StopLoss`: the retry / market-open / attempt-cap machinery is already exactly
what a triggered exit needs on a transient broker failure.

**Say the limits plainly in the UI**, next to the control:
- a monitored stop only fires while AgentFox is running and the market is open;
- it is a limit order, so a gap straight through `limit_price` may not fill;
- a native broker stop (when available) has neither limitation — prefer it, and show which kind is armed.

---

## 7. Feature 6 — Data sources

- **PSX DPS (`dps.psx.com.pk`) stays the single source of truth.** Free, authoritative, and one
  request per session covers every symbol — the architecture depends on that shape.
- **AHK broker** — holdings, balance, order placement. Never quotes: PSX gives us those market-wide
  in one request, and the browser gives us one symbol at a time.

### 7.1 Capturing AHK's own network calls (worth doing)

The portal loads exposure, collaterals and symbol resolution over AJAX (the adapter already waits on
`Networkidle2` and jQuery `change` handlers). Those are real HTTP endpoints, and driving them directly
would replace a multi-second browser round-trip with a plain `HttpClient` call — which matters a lot
for §4.2's position-aware alerts, where `get_portfolio` currently launches a whole Chromium.

Plan:

1. **Capture** — opt-in `Ahk.CaptureNetwork` (default off). During a normal authenticated session,
   subscribe to PuppeteerSharp's `Page.Request` / `Page.Response` and write method, URL, headers,
   request body and a truncated response body to `logs/trading/ahk-network-{timestamp}.jsonl`.
   Run everything through the SDK's `SecretGuard` first — the login POST and every order carries the
   password and the trading PIN, and that file must never contain them.
2. **Promote reads first** — once an endpoint is understood, add an `AhkRestClient` that reuses the
   cookies from the Puppeteer session (`Page.GetCookiesAsync` → `CookieContainer`) and implement
   `IBrokerStateReader` / portfolio reads against it. Keep the browser as the fallback: if the REST
   call 401s or its shape changes, fall through to the DOM path, log it, and let the reconciliation
   worker notice.
3. **Leave order submission on the browser path** until the REST contract is proven stable across a
   portal deploy. A misunderstood parameter on a read is a wrong number; on an order it's a wrong
   trade. Promoting writes is a separate, deliberate decision with its own smoke test.

Caveats to expect: CSRF/anti-forgery tokens minted per page load, session cookies that expire
independently of the browser profile, and the portal changing shape without notice — which is exactly
why the fallback and the reconciliation check stay.
- **TradingView / Investing.com** — no public PSX API; widget embedding and scraping are against
  their terms. Don't build the monitor on them. If a chart *look* is the goal, `lightweight-charts`
  (§1.2) is TradingView's own library, used with our own data.
- **Redundancy, done additively:** introduce `ICandleSource` with `PsxCandleSource` as the sole
  implementation and a `Source` field on `PsxCandle`, so a licensed second feed can be added later
  without touching the analyzers. Abstraction now, second source only if PSX proves insufficient.

---

## 8. Config — ready to run, customizable if needed

**Rule for this increment: every new option ships with a working default, and adding these features
requires *zero* additions to `appsettings.json`.** A fresh install must monitor, chart and alert out
of the box; the JSON below documents the defaults that are already baked into the C# option classes,
not a checklist for the user to fill in. Only two things stay opt-in, both because they place real
orders or expand real risk: `Approval.Mode` (defaults to `Always`) and `Ahk.CaptureNetwork`
(defaults to off).

```jsonc
// All defaults — present in code, absent from appsettings unless you want to override.
"Monitor": {
  "Enabled": true, "IntervalSeconds": 120, "ConfirmPasses": 2,
  "BreakBufferPercent": 0.5, "VolumeConfirmRatio": 1.3,
  "CooldownMinutes": 0,              // 0 = rest of session
  "HoldingsRefreshMinutes": 15,
  "AutoAssessMinSeverity": "",       // "" = never auto-assess (cost control)
  "MaxAlertsPerPass": 25             // circuit breaker
},
"Watchlist": {
  "SeedFromAllowedSymbols": true, "MaxSymbols": 150,
  "ArchiveWatchlistSymbols": true    // widen ForArchive() to the watchlist
},
"Proposals": {
  "TtlHours": 24, "InvalidateOnDriftPercent": 3, "RetentionDays": 90
},
"Exits": {
  "SlippagePercent": 1.0,            // stop limit = trigger × (1 − this)
  "PreferNativeStopOrder": true      // used only if the portal turns out to have one
},
"Approval": { "Mode": "Always", /* … §6.3 … */ }
```

Runtime-editable without a restart (via `TradingPluginConfigDefinitionProvider`, same store as the
kill switch): `Monitor.Enabled`, `Monitor.IntervalSeconds`, `Monitor.AutoAssessMinSeverity`,
`Approval.Mode`. Everything else is a start-up setting.

Config health is surfaced, not assumed: `GET /monitor/status` reports the effective values it is
running with, so "why didn't it alert" is answerable from the UI rather than by reading JSON on disk.

---

## 9. Phasing

| Phase | Deliverable | Proves |
| --- | --- | --- |
| **0** ✅ | `IPluginUiContributor` + `/api/plugin-ui` + `/plugin-assets/{slug}` static + dynamic Sidebar + the trading page moved into `TradingAgent/ui/`; trading stripped from `api.ts`, Sidebar, and the plugins page | **Done** — isolation works, with no new features in flight |
| **1** ✅ | `MonitoredUniverse`, watchlist table + endpoints + UI list (add/remove/reset/mute); archive universe widened; settled-session archiving fix | **Done** — independent watchlist, weekly levels for added symbols |
| **2** ✅ | `CandleAnalysisService` extracted (shared with `analyze_candles`), `IndicatorSeries`, `/trading/candles`, `lightweight-charts` pane with S/R overlays, RSI bands and interval switcher | **Done** — chart-driven decisions, drawn from the same levels the agent quotes |
| **3** ✅ | `AlertDetector` (pure), `WatchlistMonitorWorker`, `watchlist_state` / `watchlist_alerts`, `/alerts` + SSE + `/monitor/status`, alerts panel and row badges | **Done** — continuous monitoring with controlled noise |
| **4** | `StockAssessmentService` + `/assess` endpoints, confidence on the alert card | On-demand LLM confidence |
| **5a** | Proposal lifecycle (execute/reject/expire + sweeper + retention) — turns the log into a signal inbox | WhatsApp signals become actionable |
| **5b** | `probe_order_form` discovery → `/orders` + approval modes (`Auto`/`Armed`) + `PendingExitStore` + `armed_exits` trigger evaluation | Actions and stops, all through the risk engine |
| **6** | `Ahk.CaptureNetwork` → `AhkRestClient` for portfolio reads (browser fallback retained) | Fast holdings, no Chromium in the alert path |
| **7** | Optional: `ICandleSource`, WhatsApp alert fan-out, chat notifications, runtime-editable `AllowedSymbols` | Redundancy and reach |

Phase 0 is worth doing on its own even if the rest slips: it removes the coupling that exists today
and gives every future plugin (PageAgent included) a UI story.

---

## 10. Risks & gotchas

1. **Portal throttling.** The monitor must share `PsxDataClient` and its caches and honour the
   existing throttle detection. One market-wide request per pass; a per-symbol fetch in that loop is
   the one change that would break this design.
2. **Weekly levels need ~2 years.** A newly added watchlist symbol has none until the backfill
   reaches it. Surface `hasWeeklyHistory` in the list and offer a targeted backfill; until then,
   alerts for that symbol must say "no weekly confirmation".
   *(Phase 1: done — the list badges it, and the archive universe now includes the watchlist so the
   next scheduled pass fills it.)*

2b. **Unsettled sessions were being archived** (found while implementing Phase 1, now fixed).
   `daily_bar_coverage` is written even for an empty result, and the pass ran to `today` — so a pass
   during market hours either stored a partial bar that the coverage marker then prevented from ever
   being corrected, or recorded the day as non-trading, which is a permanent hole. The backfill now
   stops at the last settled session (`Scan.ArchiveSettleAfterPkt`, default 17:30 PKT) and clears
   coverage past it, which self-repairs dates recorded prematurely by earlier builds.
3. **Watchlist ≠ tradable.** Badge monitor-only symbols; disable action buttons with the reason
   inline. `AllowedSymbols` stays appsettings-only for now, by choice.
4. **Alert flicker** is the most likely reason this feature gets muted. `ConfirmPasses` +
   `BreakBufferPercent` + cooldown are not optional polish.
5. **Iframe details:** theme and API key via `postMessage` (plus `sessionStorage` fallback), and the
   iframe needs an explicit `height:100%` flex parent — auto-resize is not worth the complexity.
6. **Plugin UI build must be wired into release** (`install.ps1`, `RELEASING.md`, CI) or a published
   build silently ships a trading page with no assets. Fail the build if `ui/dist` is missing when
   `wwwroot` embedding is expected.
7. **Holdings freshness.** `get_portfolio` drives a real browser; position-aware alerts carry a
   holdings age and must never assert a stop breach on stale holdings without saying so.
8. **Testing** (`tests/AgentFox.ChannelTests` already covers trading): transition detection is pure
   given two snapshots — table-test every event kind, plus hysteresis/cooldown, watchlist repo
   CRUD + reset, and `MonitoredUniverse` (especially that `ForExecution()` never widens).
