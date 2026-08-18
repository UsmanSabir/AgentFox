# TradingAgent Plugin

AgentFox plugin that registers an isolated PSX specialist, persists non-executable proposals, and places authorized orders through a deterministic, SQLite-backed Trading Manager and AHK browser adapter.

The specialist is intentionally read/proposal-only. It never receives browser execution tools. Live compatibility tools and background exits still cross the Trading Manager boundary, which enforces policy, market eligibility, risk, durable idempotency, and audit events.

## How it works

```
WhatsApp Group
  → 3rd-party bridge (WPPConnect / Baileys)
  → signed POST /webhook/whatsapp-bridge
  → gateway enqueues the isolated trading-agent on the Specialist command lane
      parse_signal   — AI extracts symbol, action, price, confidence
      check_market   — deterministic regular sessions + configured exceptions
      log_signal     — records the signal
      create_trade_proposal — persists a non-executable proposal

  "Recommend something" / daily scan
  → scan_watchlist over AllowedSymbols
      archived daily OHLC + weekly rollup → support/resistance on BOTH timeframes
      → buy-at-support / sell-at-resistance, weekly-confirmed levels ranked first
      (daily AND weekly breakdowns excluded)
  → analyze_candles at 15m/60m to time the entry against those levels
  → research_stock on the top candidates → create_trade_proposal

  Authorized compatibility execution / bounded exit worker
  → Trading Manager
      policy + configured universe + risk + calendar + SQLite idempotency
  → AHK broker adapter
  → accepted/failed/unknown result persisted for reconciliation
```

---

## Prerequisites

- .NET 10 runtime
- Chromium or Google Chrome installed (PuppeteerSharp can also download Chromium automatically)
- An active AHK trading account
- A 3rd-party WhatsApp bridge that can POST to an HTTP endpoint (see [Bridge Setup](#bridge-setup))
- AgentFox running in `webhook` module mode

### Verify an AHK test-account login

The integration suite includes an opt-in login-only smoke test. It launches the configured browser,
logs into AHK, verifies `Ahk.LoggedInSelector`, and closes the browser. It does not open an order
dialog and cannot submit an order.

Copy the variable names from `tests/AgentFox.ChannelTests/ahk-login-test.env.example` into your
shell environment and supply the test-account values. Credentials and the trading PIN must never be
committed to `appsettings.json`, the example file, or test source.

PowerShell example:

```powershell
$env:AHK_TEST_LOGIN_ENABLED = "true"
$env:AHK_TEST_USERNAME = "your-test-account"
$env:AHK_TEST_PASSWORD = "your-test-password"
$env:AHK_TEST_TRADING_PIN = "your-test-pin"
$env:AHK_TEST_CHROME_PATH = "C:\Program Files\Google\Chrome\Application\chrome.exe"
dotnet run --project tests/AgentFox.ChannelTests/AgentFox.ChannelTests.csproj -- --filter AhkLogin
```

`AHK_TEST_TRADING_PIN` is accepted so the complete test-account credential set can be supplied, but
the login probe never reads it into an order form. If the portal requires CAPTCHA, OTP, or another
interactive challenge, the smoke test will fail rather than bypassing that control.

---

## Installation

Copy the plugin DLL and its dependencies into the AgentFox `plugins/` folder next to the executable:

```
AgentFox.exe
plugins/
  TradingAgent.dll
  PuppeteerSharp.dll
  (other transitive deps)
```

The plugin loader discovers `TradingAgentModule` and `WhatsAppBridgeChannelProvider` automatically at startup — no changes to AgentFox source code are required.

---

## Web UI

The trading dashboard is part of **this plugin**, not the AgentFox frontend. It lives in
[`ui/`](ui/) as its own npm project, so trading-only dependencies (charting in particular) never
enter the host app's `package.json`, and the host has no trading route, type, or API client.

```bash
cd ui
npm ci
npm run build      # → ../wwwroot, embedded into TradingAgent.dll by the csproj
```

Then build the plugin. At startup the module contributes the page via
`IPluginUiContributor`, and AgentFox:

- serves the embedded assets at `/plugin-assets/trading/`,
- lists the page at `GET /api/plugin-ui`,
- shows a **Trading** entry in the sidebar, which renders the page at `/ext/trading`.

The page shows the watchlist beside a **chart pane**: candlesticks with a direction-tinted volume
overlay, SMA20/50, an RSI sub-pane with the *configured* oversold/overbought bands (not the textbook
30/70), horizontal support/resistance lines whose width encodes touch count and whose style shows
weekly confirmation, entry/stop/target markers, and an interval switcher (1D / 60m / 30m / 15m / 5m).

### Making room

Six labelled price lines, two panes and a volume overlay do not fit legibly in a small box, so the
chart carries three controls for trading detail against readability:

| Control | Effect |
| --- | --- |
| **Expand** | Chart takes the full row (784×400 → 1114×560) and the watchlist **stacks beneath** it rather than being hidden — losing symbol switching would be a poor trade for the extra width. |
| **Levels: all / key / off** | `all` draws the nearest three each side; `key` only the weekly-confirmed ones (structure, not a recent swing); `off` leaves clean price action. |
| **RSI** | Hides the sub-pane and gives its ~90px back to the candles. |

Turning lines off hides *drawing*, never information: the levels legend under the chart always lists
every level with its distance, touch count and weekly confirmation.

Layout changes rebuild the chart rather than mutating it — lightweight-charts has no clean way to add
or drop a pane after construction, and a rebuild over a few hundred bars is imperceptible.

Chart data comes from `GET /api/trading/candles`, which is served by **`CandleAnalysisService`** — the
same code path `analyze_candles` uses. That sharing is the point: the levels drawn on screen are the
same objects the specialist quotes, so the chart cannot tell one story while the agent tells another.
Two details worth knowing:

- `IndicatorSeries` computes the full SMA/RSI lines the chart needs (`TechnicalAnalyzer` computes only
  the latest value). `IndicatorSeriesTests` asserts the last element of each series equals the
  snapshot's scalar, so the line and the number can never drift apart.
- `plan.entryWeeklyConfirmed` describes the entry level **shown**, while
  `weekly.entryLevelConfirmed` describes the nearest support in the *full* archived history. They can
  legitimately differ, because the displayed plan is scoped to the requested window — the chart uses
  the former so it never reports "no weekly confirmation" beside a level that has one.

Notes:

- **Build the UI before the DLL.** `wwwroot` is embedded at compile time. A build without it is
  valid — the plugin simply contributes no page and the backend is unaffected — so a stale or
  missing UI shows up as a missing sidebar entry, not an error.
- **Asset base URL.** `ui/vite.config.ts` sets `base: '/plugin-assets/trading/'` to match
  `PluginUiPaths.AssetPrefix`. That is deliberately *not* `/ext/trading`, which is the host page
  that frames this UI; serving assets there would bypass the AgentFox sidebar and header.
- **Auth.** The frame is same-origin, so `ui/src/api.ts` reads the management API key from the
  shared `sessionStorage`; the host also posts the key and the current theme on load.
- **Standalone dev.** `npm run dev` in `ui/` runs the UI on its own port and proxies `/api` to
  `BACKEND_URL` (default `http://localhost:5000`).

---

## Proposals — the signal inbox

A proposal is what the specialist produced from a signal that arrived **while nobody was watching**
(a WhatsApp tip overnight). It has a lifecycle, which is what makes it a work queue rather than the
write-only log it used to be:

```
proposed ──(execute)──► executing ──► executed        (execution_id recorded)
    │                         └─────► proposed        (refused by a gate — stays actionable)
    ├──(reject)───────► rejected     (reason recorded)
    └──(TTL / drift)──► expired      (reason recorded)
```

| Verb | Route | Role |
| --- | --- | --- |
| GET | `/api/trading/proposals?openOnly=true` — the queue (default) | ManagementViewer |
| POST | `/api/trading/proposals/{id}/execute` | **TradingTrader** |
| POST | `/api/trading/proposals/{id}/reject` — `{ reason }` | TradingAnalyst |

Details that matter:

- **Execute adds no execution path.** It parses the proposal's orders and hands them to
  `TradingManager.ExecuteGroupsAsync`, so execution mode, the risk engine, the market calendar, the
  kill switch, idempotency and audit events all still apply. Each order goes in its own group, since
  they are independent and one failing must not skip the rest.
- **A double click cannot execute twice.** Claiming a proposal is a compare-and-set on its current
  status (`WHERE status = @expected`); the loser gets a 409 rather than a second live order.
- **A refusal returns the proposal to `proposed`**, not to a terminal state — the reason is usually
  transient (market closed, reconciliation stale, approval required), so a failed attempt must not
  burn the proposal.
- **Ageing keeps the queue honest.** The monitor's post-close pass expires anything past
  `Proposals.TtlHours` (24) or whose stated entry has drifted more than
  `Proposals.InvalidateOnDriftPercent` (3 %) from the live price — a stale price is not a tradable
  plan. Expiry is a state change *with a reason*, never a delete; only `Proposals.RetentionDays` (90)
  removes rows, and only terminal ones.
- The UI shows **open proposals only** by default, with a "show resolved" toggle. An empty inbox is
  the normal state, and it says so.

---

## Watchlist vs AllowedSymbols — two different universes

These are deliberately separate, and the distinction is the difference between "what am I watching"
and "what am I allowed to trade":

| | Source | Used for | Editable at runtime |
| --- | --- | --- | --- |
| **Watchlist** | `watchlist` table (seeded once from `AllowedSymbols`) | charting, scanning, monitoring, alerts, archived history | **yes** — Trading page, `/api/trading/watchlist` |
| **AllowedSymbols** | `appsettings.json` | what an order may be placed for (`TradingRiskEngine`) | no — config + restart |

`MonitoredUniverse` is the single place that answers "which symbols":

- `ForExecution()` → `AllowedSymbols` only. **No watchlist edit can widen this** — otherwise the web
  UI would have become an order-permission editor.
- `ForMonitoringAsync()` → watchlist ∪ `AllowedSymbols`. Charts, `scan_watchlist`, alerts.
- `ForArchiveAsync()` → same as monitoring by default, so a watched symbol accumulates the daily
  history its weekly levels need. Costs no extra portal requests (a session fetch already returns
  every symbol in the market), only rows.

Consequences the UI states explicitly rather than leaving to be discovered at order time:

- A watched symbol outside `AllowedSymbols` is badged **monitor-only**; `scan_watchlist` results carry
  `tradable`, and the specialist must not present a non-tradable candidate as actionable.
- A newly added symbol is badged **no weekly** until roughly two years of daily bars are archived —
  until then there is no weekly confirmation to quote.
- The watchlist is seeded from `AllowedSymbols` **once**. If the configured list changes later, the
  watchlist is *not* updated (that would discard your edits); the API reports
  `configuredListChanged: true` and the UI offers **Reset**, which is the only thing that re-seeds.

---

## Monitoring and alerts

`WatchlistMonitorWorker` watches the whole monitoring universe for **transitions** and raises alerts.
It runs every `Monitor.IntervalSeconds` while the market is open, plus one settle pass after the close,
and it can only ever raise alerts — execution stays behind the execution mode, the risk engine, and
the kill switch.

**The cost model is the point.** One pass costs **one market-wide request** (PSX serves candles by
date, covering every symbol) plus local archive reads, so 100 watched symbols cost the same as 5 —
a real pass over 26 symbols measures ~2.6 s cold, ~0.2 s warm. Nothing in that loop may fetch per
symbol; doing so would turn a 2-minute cadence into a rate-limit incident.

### What it detects

| Kind | Fires when | Severity |
| --- | --- | --- |
| `SupportBounce` | setup is buy-at-support **and** price is turning up off the level | High |
| `ResistanceRejection` | setup is sell-at-resistance **and** price is turning down | High |
| `SupportBreak` | fresh range low, still falling, past the buffer, volume-confirmed | High |
| `ResistanceBreakout` | fresh range high, still rising, past the buffer, volume-confirmed | High |
| `SetupChanged` | the deterministic setup classification changed | High into a breakdown, else Medium |
| `TrendFlip` | SMA20 crossed SMA50 | Medium |
| `WeeklyBreakdown` | the weekly chart entered a breakdown | Critical |
| `RsiOversold` / `RsiOverbought` | RSI crossed into a band | Low |

### Why it does not spam

An alert feed that cries wolf gets muted, and a muted monitor is worth nothing. Four guards:

1. **Transitions, not conditions.** "Price is at support" is true for days; "price has turned up off
   support" happens once. Only the second is an alert.
2. **Confirmation streaks.** A *sustained* condition must hold for `Monitor.ConfirmPasses` consecutive
   passes (default 2) before firing.
3. **A break buffer.** A close must clear a level by `Monitor.BreakBufferPercent` (default 0.5 %) and
   be volume-confirmed to count as a break — a wick through a level is noise.
4. **A durable cooldown.** The same symbol + kind + level does not re-alert within
   `Monitor.CooldownMinutes` (0 = the rest of the session). It is a database check, so a restart
   cannot re-announce what was already said.

Two structural details worth knowing:

- **A cold start fires nothing.** Every kind is a transition, and on first sight of a symbol there is
  nothing to have transitioned from — so the first pass records state silently. Otherwise a restart
  would alert on every standing condition at once.
- **Edges fire on the pass they appear; sustained conditions wait for the streak.** A setup change, an
  SMA cross, an RSI band entry and a weekly breakdown are visible for exactly one pass, because the
  state they are compared against is rewritten at the end of every pass. Streak-gating those would not
  delay them — it would silence them permanently. Their flicker protection is the cooldown instead.
  `AlertDetectorTests` pins both behaviours.

Alerts carry their evidence (the analyzer's own reasons), whether the level is weekly-confirmed, and
whether they were raised off a **still-forming bar** — a trigger that can still un-happen before the
close, which the UI labels rather than hiding.

---

## Confidence assessment (LLM, on demand)

The numbers stay deterministic; the model only **judges** them. `StockAssessmentService` owns the one
confidence rubric — `research_stock` and the assessment endpoints share it, so a verdict means the
same thing wherever it appears. The UI uses background jobs because a local model may take minutes:
disconnecting or refreshing the browser does not cancel generation already in progress.

```
POST /api/trading/assessment-jobs              { symbol, interval?, context? }   → 202 + jobId
POST /api/trading/alerts/{id}/assessment-jobs                             → 202 + jobId
GET  /api/trading/assessment-jobs/{jobId}                                 → status/result
```

The older synchronous `/assess` routes remain available for compatibility, but interactive callers
should use jobs. Jobs are serialized to avoid competing local-model generations, identical active
submissions share one job, and terminal statuses remain queryable for 15 minutes.

Evidence is the same read the chart draws (`CandleAnalysisService`: levels, indicators, weekly
structure) plus the portal quote, listing status and news. The reply is structured:
confidence + score, `PROCEED` / `CAUTION` / `AVOID` / `INSUFFICIENT_DATA`, rationale, supporting and
risk factors, an **invalidation level**, and the model that produced it.

Four properties that matter more than the wording of the prompt:

- **Never automatic.** A model call per alert would cost real money and hit rate limits on a busy day,
  and most alerts are read and dismissed in a second. It is a button.
- **Fails conservative.** A model error or unparseable output yields `INSUFFICIENT_DATA` with
  confidence `NONE` — never a default optimism. A **delisted** security short-circuits to `AVOID`
  without spending a call at all.
- **The invalidation level is chosen, not invented.** The prompt requires it to come from the levels
  already in the evidence (a support, a resistance, or the suggested stop), preserving the "never
  invent a price" rule that lets the specialist quote these figures.
- **Cached per symbol + level + session.** Clicking twice on one situation costs one call; a level
  that has moved is a different question and gets a fresh answer. A *failed* assessment is never
  cached, so a retry actually retries. An alert knows its own identity, so a repeat click on one
  short-circuits before any fetching — measured **46 s → 57 ms**.

The verdict reports the **model that actually answered** (read from the chat client's metadata), not
the configured `ParserModelKey`: that key selects the specialist *agent's* model, while tools and
endpoints use the default chat client, and naming a key that was not used would put a false entry in
the audit trail.

### Endpoints

| Verb | Route | Role |
| --- | --- | --- |
| GET | `/api/trading/alerts?symbol=&state=&limit=` | ManagementViewer |
| POST | `/api/trading/alerts/{id}/assessment-jobs` — queue LLM confidence for an alert | TradingAnalyst |
| POST | `/api/trading/assessment-jobs` — queue LLM confidence for a symbol | TradingAnalyst |
| GET | `/api/trading/assessment-jobs/{jobId}` — poll queued/running/result state | TradingAnalyst |
| POST | `/api/trading/alerts/{id}/assess` — LLM confidence for that alert | TradingAnalyst |
| POST | `/api/trading/assess` — LLM confidence for a symbol | TradingAnalyst |
| GET | `/api/trading/alerts/stream` — SSE, live push | ManagementViewer |
| POST | `/api/trading/alerts/{id}/ack` \| `/dismiss` | TradingAnalyst |
| GET | `/api/trading/monitor/status` — last pass, coverage, and the *effective* settings | ManagementViewer |
| POST | `/api/trading/monitor/run` — run a pass now | TradingAnalyst |
| GET | `/api/trading/candles?symbol=&interval=&bars=` — chart data (see below) | ManagementViewer |
| GET | `/api/trading/watchlist` | ManagementViewer |
| POST | `/api/trading/watchlist` — `{ symbol }`, validated against the live market watch | TradingAnalyst |
| DELETE | `/api/trading/watchlist/{symbol}` — keeps archived bars | TradingAnalyst |
| PATCH | `/api/trading/watchlist/{symbol}` — `{ alertsEnabled?, notes? }` | TradingAnalyst |
| POST | `/api/trading/watchlist/reset` — reseed from `AllowedSymbols` | TradingAnalyst |

---

## Configuration

Add the following sections to `appsettings.json`.

### 1. Enable the webhook module

```json
"Modules": "cli,web,webhook"
```

### 2. Plugin settings

```json
"Plugins": {
  "TradingAgent": {
    "AutoExecute":            false,
    "ExecutionMode":          "Disabled",
    "MinConfidence":          "HIGH",
    "ParserModelKey":         "CheapModel",
    "MemoryMode":             "Shared",
    "DuplicateWindowMinutes": 60,
    "DatabasePath":           "trading/trading.db",
    "AllowedSymbols":         ["OGDC", "PPL"],
    "RequireConfiguredSymbols": true,
    "MaxOrdersPerBatch":      10,
    "MaxBatchValuePkr":       250000,
    "RequireReconciliationHealthy": true,
    "ReconciliationIntervalSeconds": 60,
    "ReconciliationMaxAgeSeconds": 180,
    "ResearchWebEnabled":  true,
    "ResearchWebMaxResults": 5,
    "ResearchWebSearchDepth": "advanced",
    "ResearchWebMaxContentCharacters": 4000,
    "SpecialistTimeoutSeconds": 600,
    "MarketHolidays":         [],
    "MarketSessionOverrides": [],
    "Scan": {
      "LookbackDays":               60,
      "SupportProximityPercent":    2.5,
      "ResistanceProximityPercent": 2.5,
      "MinRewardRisk":              1.5,
      "MinAverageVolume":           25000,
      "MaxResults":                 10,
      "BackfillYears":              2,
      "WeeklyLookbackWeeks":        104,
      "ConfluenceTolerancePercent": 2.0,
      "ArchiveIntradayBars":        true,
      "IntradayLookbackBars":       120
    }
  },
  "Ahk": {
    "PortalUrl":        "https://web.ahletrade.com/",
    "Username":         "YOUR_USERNAME",
    "Password":         "YOUR_PASSWORD",
    "TradingPin":       "YOUR_PIN",
    "DefaultQty":       100,
    "MaxOrderValuePkr": 50000,
    "SessionDir":       "session_ahk",
    "LogDir":           "logs/trading"
  }
}
```

| Key | Default | Description |
|---|---|---|
| `AutoExecute` | `false` | Set to `true` to allow the agent to call `place_order`. When `false` the agent logs signals but never trades. |
| `ExecutionMode` | `Disabled` | `Disabled`, `Paper`, `Shadow`, `ApprovalRequired`, or `BoundedAuto`. `AutoExecute` remains an additional hard off-switch. |
| `DatabasePath` | `trading/trading.db` | SQLite operational ledger. WAL mode and durable idempotency are enabled automatically. |
| `AllowedSymbols` | `[]` | Explicit execution universe. Empty fails closed when `RequireConfiguredSymbols=true`. |
| `MarketHolidays` | `[]` | Operator-maintained closed dates in `yyyy-MM-dd` form. |
| `ResearchWebEnabled` | `true` | Expose the provider-backed, read-only `research_web` tool when a provider is configured. |
| `ResearchWebMaxResults` | `5` | Maximum provider results returned to the specialist. |
| `ResearchWebSearchDepth` | `basic` | Provider search depth (`basic` or `advanced`). |
| `ResearchWebMaxContentCharacters` | `4000` | Maximum snippet characters retained per external result. |
| `SpecialistTimeoutSeconds` | `600` | Wall-clock budget for one trading-agent turn (specialist lane timeout). Raise further if AHK browser automation is slow on this machine. |

When the Tavily plugin is installed and `TAVILY_API_KEY` (or `Plugins:Tavily:ApiKey`) is configured,
the isolated specialist receives `research_web`. Results are read-only, treated as untrusted evidence,
and their URLs are attached to the chat response's source references. Harness hosted web search remains
disabled; this tool uses the explicit AgentFox provider bridge instead.
| `MarketSessionOverrides` | `[]` | Date-specific `Closed` or `Sessions` overrides such as `09:30-13:00`. |
| `RequireReconciliationHealthy` | `true` | Blocks live modes when broker fills, positions, and balances cannot be reconciled. The current AHK browser adapter reports unsupported, so live entry execution remains fail-closed. |
| `MinConfidence` | `HIGH` | Minimum signal confidence required before placing an order (`HIGH`, `MEDIUM`, `LOW`). |
| `ParserModelKey` | `CheapModel` | Reserved for future use — will resolve to a named model from the `Models` config section once `IModelClientFactory` is available in AgentFox.Plugins. Currently uses the default `IChatClient`. |
| `MemoryMode` | `Shared` | `Shared` uses AgentFox memory, `Isolated` uses `memory/agents/trading-agent/`, and `Disabled` prevents specialist recall and memory-tool access. Can also be changed at runtime on the Memory page. |
| `DuplicateWindowMinutes` | `60` | Identical messages received within this window are silently discarded. |
| `DefaultQty` | `100` | Shares to trade when the signal message does not specify a quantity. |
| `MaxOrderValuePkr` | `50000` | Hard cap: `qty × price` above this value is rejected before the browser is touched. |
| `SessionDir` | `session_ahk` | Directory for the persistent Chromium profile (keeps the AHK session logged in). |
| `LogDir` | `logs/trading` | Directory for signal JSONL logs and order screenshots. |
| `Scan.LookbackDays` | `60` | Trading sessions of OHLC history per candle scan (5–250). See [Candle scanning](#candle-scanning-buy-at-support-sell-at-resistance). |
| `Scan.SupportProximityPercent` | `2.5` | Within this percent of a support level counts as "at support" (buy zone). |
| `Scan.ResistanceProximityPercent` | `2.5` | Within this percent of a resistance level counts as "at resistance" (sell zone). |
| `Scan.MinRewardRisk` | `1.5` | Minimum `(target−entry)/(entry−stop)` for a buy candidate to be offered. |
| `Scan.MinAverageVolume` | `25000` | Minimum 30-session average volume; thinner symbols are excluded as untradable at the quoted level. |
| `Scan.MaxResults` | `10` | Maximum candidates returned per side. |
| `Scan.MarketWatchCacheSeconds` | `60` | How long a live market-watch snapshot is reused. |
| `Scan.ArchiveSettleAfterPkt` | `17:30` | PKT time after which the current session's candles count as final and may be archived. Earlier archiving stores a partial bar the coverage marker would prevent from ever being corrected. |
| `Watchlist.SeedFromAllowedSymbols` | `true` | Prefill the watchlist from `AllowedSymbols` the first time it is used. Applies once; the watchlist is yours afterwards. |
| `Watchlist.MaxSymbols` | `150` | Upper bound on watched symbols. |
| `Watchlist.ArchiveWatchlistSymbols` | `true` | Archive daily history for watchlist symbols too, so they get weekly levels. Costs database rows, not portal requests. |
| `Watchlist.ValidateAgainstMarketWatch` | `true` | Reject an unknown ticker when it is added, instead of letting a typo become an empty chart. A portal outage warns rather than blocks. |
| `Monitor.Enabled` | `true` | Run the background watchlist monitor. |
| `Monitor.IntervalSeconds` | `120` | Seconds between passes while the market is open (30–3600). One pass = one market-wide request regardless of symbol count. |
| `Monitor.ConfirmPasses` | `2` | Consecutive passes a *sustained* condition must hold before it alerts. 1 fires immediately and flickers on a level. |
| `Monitor.BreakBufferPercent` | `0.5` | How far past a level a close must be to count as a break rather than a wick. |
| `Monitor.VolumeConfirmRatio` | `1.3` | Volume vs the 30-bar average required to confirm a break. 0 accepts any volume. |
| `Monitor.CooldownMinutes` | `0` | Minutes before the same symbol+kind+level may alert again; 0 means the rest of the session. |
| `Monitor.MaxAlertsPerPass` | `25` | Circuit breaker for a market-wide move. Excess is logged, never silently dropped. |
| `Monitor.RunAfterClose` | `true` | One extra pass after the close, on the day's settled bars. |
| `Monitor.RetentionDays` | `90` | Alert history retained; older rows are pruned so the table has a ceiling. |
| `Proposals.TtlHours` | `24` | Hours a proposal stays actionable before it is expired. |
| `Proposals.InvalidateOnDriftPercent` | `3` | Expire once the live price has moved this far from the stated entry. 0 disables drift expiry. |
| `Proposals.RetentionDays` | `90` | Days a *terminal* proposal is kept. Open proposals are never pruned. |
| `Ahk.VerifyOrderInBook` | `true` | Confirm a submitted order exists by reading the order book instead of trusting the result popup. |
| `Ahk.OrderBookVerifyTimeoutMs` | `8000` | How long to wait for a submitted order to appear in the book. |
| `Ahk.StopLimitSlippagePercent` | `1.0` | How far below the trigger a stop-loss SELL's limit is placed. 0 places it at the trigger. |
| `Scan.MarketDayFetchConcurrency` | `4` | Concurrent portal requests while warming a cold candle cache (1–8). |
| `Scan.MaxCachedMarketDays` | `120` | Settled sessions kept in the in-memory candle cache. |
| `Scan.BackfillYears` | `2` | Years of daily OHLC the background worker archives for `AllowedSymbols`. Weekly levels need ~2 years. `0` disables it. |
| `Scan.WeeklyLookbackWeeks` | `104` | Weekly bars requested when computing higher-timeframe structure. |
| `Scan.ConfluenceTolerancePercent` | `2.0` | How close a weekly level must sit to a daily one to confirm it. Wider means more levels read as "confirmed" on weaker evidence. |
| `Scan.ArchiveIntradayBars` | `true` | Persist completed intraday bars to `intraday_bars` in `trading.db`. The only way multi-session intraday history can exist — PSX serves the current session only. |
| `Scan.IntradayLookbackBars` | `120` | Archived intraday bars loaded per analysis, on top of the current session rebuilt from ticks. |
| `Scan.RangeWindow` | `20` | Sessions used for range position and new-low/new-high comparisons. |
| `Scan.PivotWindow` | `3` | Bars either side of a bar for it to count as a swing pivot. |
| `Scan.LevelClusterPercent` | `1.5` | Levels within this percent of each other merge into one (touch count). |
| `Scan.StopAtrMultiple` | `1.0` | ATR multiple below the entry for the suggested protective stop. |
| `Scan.RsiOversold` / `Scan.RsiOverbought` | `35` / `70` | RSI(14) thresholds annotated in the reasons. |
| `Scan.BreakdownDownDays` | `3` | Consecutive down sessions that, with a fresh range low, mark a breakdown instead of a support test. |

### 3. WhatsApp bridge channel

```json
"Channels": [
  {
    "Type":        "whatsapp-bridge",
    "Enabled":     true,
      "CallbackUrl": "",
      "GroupFilter": "PSX Signals",
      "RequireSignature": "true",
      "WebhookSecretEnvironmentVariable": "AGENTFOX_TRADING_WEBHOOK_SECRET",
      "MaxClockSkewSeconds": "120",
      "AllowedSenders": "923001234567"
  }
]
```

| Key | Required | Description |
|---|---|---|
| `CallbackUrl` | No | HTTP endpoint on the bridge for outbound messages (HITL approval prompts). POST body: `{ "text": "..." }`. Leave empty to disable outbound. |
| `GroupFilter` | No | Only process messages from this WhatsApp group name. Leave empty to accept all groups. |
| `RequireSignature` | No | Defaults to `true`. Unsigned webhooks are rejected. |
| `WebhookSecretEnvironmentVariable` | No | Environment variable containing the HMAC secret. Defaults to `AGENTFOX_TRADING_WEBHOOK_SECRET`. |
| `AllowedSenders` | No | Optional comma-separated sender allowlist. |

### 4. HITL (Human-in-the-Loop) approval

The HITL gate is handled by AgentFox's built-in tool approval system — no extra code is needed. When enabled, the agent sends an approval prompt to the channel before executing any order, and waits for `/approve <id>` or `/reject <id>`.

```json
"Hitl": {
  "Enabled": true,
  "RequireApprovalForTools": ["place_order", "place_orders"]
}
```

**Approval prompt example (sent to the channel):**

```
🔐 Approval Required [A3F2C1]

Agent wants to run: place_order
Arguments: action=BUY, symbol=OGDC, quantity=100

/approve A3F2C1 — allow
/reject A3F2C1 [reason] — block
```

To respond, type `/approve A3F2C1` or `/reject A3F2C1` in the same channel. For this to reach you via WhatsApp, `CallbackUrl` must be configured so outbound messages can be forwarded by the bridge.

To disable HITL and let the agent trade automatically:

```json
"Hitl": {
  "Enabled": false,
  "RequireApprovalForTools": []
}
```

> **Warning**: `ApprovalRequired` refuses startup unless HITL is enabled and both compatibility execution tools are watched. `BoundedAuto` is reserved for versioned automated workflows and must not be enabled merely to bypass approval.

---

## Bridge Setup

The plugin expects inbound signals from any HTTP client that can POST JSON to:

```
POST /webhook/whatsapp-bridge
Content-Type: application/json

{
  "id":        "bridge-stable-message-id",
  "from":      "923001234567",
  "group":     "PSX Signals",
  "body":      "BUY OGDC @ 165 target 185 sl 158",
  "timestamp": "1736900000"
}
```

The request must include:

- `X-AgentFox-Timestamp`: current Unix seconds.
- `X-AgentFox-Signature`: `sha256=` followed by the hexadecimal HMAC-SHA256 of `<timestamp>.<raw-body>` using the configured secret.

The stable `id` is mandatory and replayed IDs are rejected.

| Field | Required | Description |
|---|---|---|
| `id` | **Yes** | Stable source message identifier used for replay protection. |
| `body` | **Yes** | The message text. |
| `from` | No | Sender phone number (used as `SenderId` on the channel message). |
| `group` | No | Group name (matched against `GroupFilter` if set). |
| `timestamp` | No | Unix epoch. Defaults to server time if absent. |

### WPPConnect example listener

```javascript
// Node.js — WPPConnect
const crypto = require('crypto');
const axios = require('axios');
const secret = process.env.AGENTFOX_TRADING_WEBHOOK_SECRET;

async function postSigned(payload) {
  const body = JSON.stringify(payload);
  const timestamp = String(Math.floor(Date.now() / 1000));
  const signature = crypto.createHmac('sha256', secret)
    .update(`${timestamp}.${body}`).digest('hex');
  await axios.post('http://your-agentfox-host:8080/webhook/whatsapp-bridge', body, {
    headers: {
      'Content-Type': 'application/json',
      'X-AgentFox-Timestamp': timestamp,
      'X-AgentFox-Signature': `sha256=${signature}`
    }
  });
}

client.onMessage(async (message) => {
  if (!message.isGroupMsg) return;

  await postSigned({
    id:        message.id,
    from:      message.sender.id,
    group:     message.chat.name,
    body:      message.body,
    timestamp: String(Math.floor(message.timestamp))
  });
});
```

### Baileys example listener

```javascript
// Node.js — Baileys
const crypto = require('crypto');
const secret = process.env.AGENTFOX_TRADING_WEBHOOK_SECRET;

async function postSigned(payload) {
  const body = JSON.stringify(payload);
  const timestamp = String(Math.floor(Date.now() / 1000));
  const signature = crypto.createHmac('sha256', secret)
    .update(`${timestamp}.${body}`).digest('hex');
  await fetch('http://your-agentfox-host:8080/webhook/whatsapp-bridge', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-AgentFox-Timestamp': timestamp,
      'X-AgentFox-Signature': `sha256=${signature}`
    },
    body
  });
}

sock.ev.on('messages.upsert', async ({ messages }) => {
  for (const msg of messages) {
    if (!msg.key.remoteJid?.endsWith('@g.us')) continue;  // groups only

    const body = msg.message?.conversation
      || msg.message?.extendedTextMessage?.text
      || '';

    if (!body) continue;

    await postSigned({
      id:        msg.key.id,
      from:      msg.key.participant,
      group:     msg.key.remoteJid,
      body,
      timestamp: String(msg.messageTimestamp)
    });
  }
});
```

---

## Stop-loss orders, and proving an order exists

The portal has a **native Stop Loss** order type, which is preferable to a locally-monitored stop
because it rests at the broker and survives this process being down. Its shape, confirmed by direct
inspection: `#sellprice` is the **trigger**, `#selllimitprice` is the **limit**, and the limit field is
enabled *only* while the Stop Loss type is selected — which the adapter uses as a deterministic
readiness signal rather than guessing at a delay.

Send `OrderType = "STOPLOSS"` with `EntryPrice` as the trigger. `LimitPrice` is optional: left null it
is derived as `trigger × (1 − Ahk.StopLimitSlippagePercent)` (default 1 %), because a stop limit set
exactly *at* the trigger frequently misses the fast move that triggered it. The risk engine enforces
the direction — a SELL stop's limit must be at or below its trigger, a BUY stop's at or above — since
a stop that cannot fill is worse than no stop: it looks like protection.

### Why success is verified against the order book

Measured against the live portal: an off-hours submission returns **HTTP 200 with an empty body** and
shows a green **"success"** alert while placing **nothing** — the order appears in neither the
outstanding nor the activity log, and the happy path returns no order number either. A result popup
therefore cannot distinguish "placed" from "silently discarded".

So `Ahk.VerifyOrderInBook` (default **on**) re-reads the account's own book after every submission:

- **Found** → the order exists whatever the popup said, and the exchange's order number is adopted
  (this is the only place we can obtain one).
- **Absent** → **not** success, even if the popup claimed it. Recorded as not placed.
- **Unreadable** → the popup's verdict stands but a claimed success is downgraded to unconfirmed,
  because "we could not check" and "it is there" are different statements.

Both logs are consulted: a resting order shows in the outstanding book, but one that filled
immediately never rests, and treating its absence there as "never placed" would be exactly backwards.

---

## Armed orders — an order waiting on a level or an event

An armed order is a **trigger plus an order**, evaluated by the monitor pass:

| Trigger | Fires when |
| --- | --- |
| `PriceBelow` | last price reaches or falls below the level — a protective exit |
| `PriceAbove` | last price reaches or rises above it — a breakout entry |
| `Event` | the monitor raises a given `AlertKind` for that symbol (bounce, break, trend flip) |

```
POST   /api/trading/armed-orders    { symbol, action, quantity, triggerKind, triggerPrice |
                                      triggerAlertKind, orderType, price, limitPrice, expiresInDays }
GET    /api/trading/armed-orders?all=false
DELETE /api/trading/armed-orders/{id}                      → disarm
POST   /api/trading/approval/arm    { minutes }             → suspend confirmation (RiskManager)
POST   /api/trading/approval/disarm
```

**Prefer the broker's native stop where one fits.** It rests at the exchange and fires whether or not
this process is running; an armed order only fires while AgentFox is up *and* the market is open. The
API says so in every `GET` response rather than leaving it to be discovered.

Safety properties, each deliberate:

- **Arming a non-tradable symbol is refused** at arm time, not at fire time. An armed order for a
  symbol outside `AllowedSymbols` would sit there looking like protection and be rejected by the risk
  engine at the exact moment it mattered.
- **A trigger is claimed with a compare-and-set before the broker is touched**, so a slow submission
  overlapping the next pass cannot fire it twice.
- **Approval is asked for explicitly.** An armed order fires with nobody watching, so it must be
  pre-authorised by `Approval` policy or it does not send. In the default `Always` mode it stays armed
  and logs that confirmation was required — and the arm response says this up front.
- **A refusal re-arms; a thrown submission does not.** "The market just closed" must not silently
  disarm a protective stop, but a submission that threw is genuinely ambiguous about whether it
  reached the broker, so reconciliation owns that rather than a retry.
- **Expiry outranks the condition**, and one is defaulted (30 days) — an entry trigger left open
  indefinitely can fire months later against a thesis nobody remembers forming.

### Arming one from the UI

Two entry points on the Trading page, both opening the same dialog with different pre-fill:

1. **A price level** — under the chart, the **Resistance** and **Support** lists are buttons (*"click to
   arm"*). Clicking one opens the dialog with the level, the direction that side implies (a support →
   `SELL` stop below it; a resistance → `BUY` above it), a `STOPLOSS` type, and the stop limit already
   derived one percent past the trigger. The header restates the level's touch count and weekly
   confirmation, so size is committed against a level you can see rather than one you remember.
2. **An event** — the ⌖ button on any alert card. This arms on the **kind** of event, not that
   instance: *"Fires the NEXT time Support Bounce is raised for OGDC. This alert is the example, not
   the trigger."* Side is inferred from the event's direction.

Everything stays editable — the pre-fill saves typing, it does not decide the trade. On submit the
dialog **stays open** to show whether the order will fire unattended, because that is the single most
important thing to read and closing would hide it.

The **Armed orders** panel lists what is waiting, with a disarm button per row, the approval state
beside it (an armed order that cannot be approved will not fire, so the two belong together), and an
**Open window** button for a time-boxed confirmation-free period.

### What actually makes an order fire unattended

Three independent layers, and **all** must permit it. An armed order is only the *trigger* — it says
WHEN, not whether a human must confirm:

| Layer | Setting | For unattended firing |
| --- | --- | --- |
| 1. Master switch | `AutoExecute` | `true` |
| 2. Execution mode | `ExecutionMode` | `BoundedAuto` **or** `ApprovalRequired` |
| 3. Approval | `Approval.Mode` | ignored under `BoundedAuto`; under `ApprovalRequired` needs `Auto` (within caps) or an open `Window` |

Plus the risk engine, every time: kill switch clear, symbol in `AllowedSymbols`, market open, order
value within caps, reconciliation healthy.

`BoundedAuto` is itself the operator saying "act within the configured bounds", so approval mode does
not gate it — requiring an intent there would mean a trigger silently never fires on a system
explicitly configured for automatic execution. Ask the API rather than reasoning it out: the arm
response returns `willFireUnattended` with the reason, computed by `ApprovalGate` itself.

> The approval window mode was originally called `Armed`, which collided with "armed order" and
> invited exactly the wrong inference. It is now `Window`.

### How a pre-approval works

`ApprovalGate` does not bypass anything. When policy permits an unattended order it **mints a real
`ApprovalIntent`** and passes it as an `ExecutionAuthorization`, so the order travels the identical
path a clicked approval does — bound to the exact orders, policy version and expiry, with the hash
re-checked immediately before submission. A price that moved between minting and submitting is
rejected there. The only difference from a human approval is the recorded actor
(`approval-auto` / `approval-armed:<who>`).

`Approval.Mode`: `Always` (default) · `Auto` (within the caps in `Approval.Auto`) · `Window` (a
time-boxed window). Arming reports whether it is **actually in force** — granting a window while the
market is closed answers `inForce: false` with the reason, rather than implying protection that is not
active.

---

## AHK Submit Button

The AHK portal submit button ID has not been confirmed from live inspection. The broker currently tries `#buySubmitBtn` / `#sellSubmitBtn` first, then falls back to a JS text-content search.

To confirm the correct selector, log in to the AHK portal, open DevTools (F12), and run:

```javascript
document.querySelectorAll('button, input[type="submit"], input[type="button"]')
  .forEach(b => console.log(b.id, '|', b.className, '|', b.textContent.trim()));
```

Update [`Broker/AhkBroker.cs`](Broker/AhkBroker.cs) with the confirmed ID in `ClickSubmitAsync`:

```csharp
var confirmedId = side == "buy" ? "#YOUR_BUY_BTN_ID" : "#YOUR_SELL_BTN_ID";
```

---

## Signal log format

Every detected signal is appended to a daily JSONL file:

```
logs/trading/signals_20260616.jsonl
```

Each line is a JSON object:

```json
{
  "timestamp_utc":    "2026-06-16T10:35:00Z",
  "action":           "BUY",
  "symbol":           "OGDC",
  "entry_price":      165.00,
  "target":           185.00,
  "stop_loss":        158.00,
  "confidence":       "HIGH",
  "sender":           "923001234567",
  "raw_message":      "BUY OGDC @ 165 target 185 sl 158",
  "executed":         true,
  "execution_reason": "Order placed successfully"
}
```

Screenshots of the AHK portal (before and after submit) are saved alongside:

```
logs/trading/pre_buy_20260616_103500.png
logs/trading/post_buy_20260616_103502.png
```

---

## Candle scanning (buy at support, sell at resistance)

Two tools turn daily OHLC candles into recommendations drawn from the **configured** symbol list
rather than from whatever the model recalls about the market:

| Tool | Use |
|---|---|
| `scan_watchlist` | Rank the whole watchlist: which symbols are at support (buy) and which are pressing resistance (sell). Call this for "what should I buy today", "recommend a stock", or a daily scan. |
| `analyze_candles` | One symbol in depth: levels, indicators, and a suggested entry/stop/target. `interval` selects `1D` (default) or intraday `60m`/`30m`/`15m`/`5m`. |

`research_stock` also carries a `technical` section now, so a tip's stated entry is judged against
real support and resistance rather than only the 52-week range.

### Multi-timeframe levels (weekly + daily + intraday)

Levels drawn from one timeframe are unreliable, so the analysis works on three:

| Timeframe | Role |
|---|---|
| **Weekly** | Structural levels — the ones that actually hold |
| **Daily** | Swing levels and the trade plan (entry / stop / target) |
| **Intraday** | Timing only, never levels |

Weekly candles are **resampled from the daily archive**, not fetched — and they are exact rather than
approximated, because the daily bars carry true highs and lows. (Resampling from the portal's long
close-only JSON series would silently drop every wick and pull levels inward.)

What that buys you:

- **Confluence.** Each daily level is matched against weekly levels within
  `Scan.ConfluenceTolerancePercent`; matches come back under `confirmed_supports` /
  `confirmed_resistances` with both touch counts and the separation. A daily level with no weekly level
  behind it is reported as exactly that — "treat it as a weaker floor and size accordingly".
- **Alignment.** `timeframe_alignment` is `aligned`, `mixed`, `conflicting`, or `unknown`. A daily buy
  under weekly resistance is `conflicting` — named rather than left for the model to notice.
- **The weekly falling-knife filter.** A stock can look like a clean daily support test *because* the
  weekly chart is collapsing through it. `scan_watchlist` moves those to `avoid` instead of offering
  them as buys, and says why.
- **Ranking that prefers structure.** Buy candidates sort by weekly-confirmed entry level first, then
  alignment, then proximity and reward:risk — a level two timeframes recognise outranks one that is
  merely closer.

Observed on the live watchlist: HBL came back `aligned` (daily *and* weekly at resistance, entry level
weekly-confirmed) and ranked above OGDC, whose daily entry had no weekly level behind it.

### Deep history: the one-time backfill

Weekly levels need roughly two years of daily candles, and each portal request covers one date, so that
history is archived once into `daily_bars` rather than refetched per process. Passes are **resumable**
(`daily_bar_coverage` records every date already retrieved, including non-trading days) and paced one
date at a time.

**Nothing needs to be run by hand** — `DailyCandleBackfillWorker` starts a pass 45 s after launch and
every 6 hours after that, which also picks up each new session. But because a first pass takes ~18
minutes, there are three ways to watch or drive it, all sharing one single-flight runner so two passes
can never compete for the portal:

| | How |
|---|---|
| **Web UI** | The *Candle archive* card on the Trading page: stored bars, symbols, coverage range, missing days, and a **Backfill N days** button with a live progress bar. The card polls only while a pass is running. |
| **Ask the agent** | "How far back does the candle history go?" or "backfill the candle archive" — the `manage_candle_archive` tool (`status` / `backfill`). This is the quick command. |
| **HTTP** | `GET /trading/candle-archive` for status; `POST /trading/candle-archive/backfill` (admin, optional `{"years": 2}`) to start one. Returns as soon as the pass has started. |

```bash
# Status
curl -H "X-Api-Key: $KEY" http://localhost:5000/api/trading/candle-archive

# Start a pass (returns immediately; poll the status endpoint for progress)
curl -X POST -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
     -d '{"years":2}' http://localhost:5000/api/trading/candle-archive/backfill
```

A manual trigger returns once the pass has **started**, not when it finishes — an 18-minute job must
not hold an HTTP request or an agent turn open. The pass is bound to the application lifetime, so
navigating away or letting the tool call return does not abandon it; only shutdown stops it, and the
next start resumes.

Measured, not estimated — 215 weekdays of 6 symbols:

| | Measured | Extrapolated to 2 years |
|---|---|---|
| Backfill runtime | 7 min 41 s for 215 dates (2.1 s/date) | **~18 min**, once, in the background |
| Archive size | 336 KB for 1,230 bars | **~4 MB** for ~40 symbols |
| Warm read, 6 symbols × 205 sessions | **630 ms** (was ~25 s from the portal) | unchanged |
| `scan_watchlist` on a warm archive | **95 ms** | unchanged |

Set `Scan.BackfillYears` to `0` to disable it and stay on the shallower on-demand window (no weekly
structure). The backfill archives `MonitoredUniverse.ForArchiveAsync()` — the watchlist plus
`AllowedSymbols` — so **a symbol added to the watchlist gets its history on the next pass** (within 6
hours automatically, or immediately from the UI button or the `manage_candle_archive` tool). Until then
the UI badges it *no weekly*. The portal answers bursts with empty tables, so an empty date is retried
once and a pass aborts after four empty weekdays in a row rather than recording that stretch as if the
market had been closed (the UI shows that outcome in amber).

**Only settled sessions are archived.** A pass stops at the last session whose candles are final:
today counts once the market is closed *and* the PKT clock is past `Scan.ArchiveSettleAfterPkt`
(default `17:30`, an hour after Friday's 16:30 close). This matters because coverage is what makes the
backfill resumable, and fetching a session still in progress writes a coverage marker that would stop
the partial bar from ever being corrected — or, if the portal answers with an empty table, records the
day as a non-trading day, which is a permanent hole. Each pass also *clears* coverage for anything
past the settlement point, so a session recorded prematurely by an earlier build repairs itself on the
next run at a cost of one request.

### What the scan's universe is

`scan_watchlist` defaults to the **monitoring** universe — the editable watchlist plus
`AllowedSymbols` (see [Watchlist vs AllowedSymbols](#watchlist-vs-allowedsymbols--two-different-universes)).
Scanning wider than the tradable list is deliberate: a symbol you are watching should appear in a scan.

Because of that, every result carries **`tradable`**, and the result notes how many scanned symbols are
monitor-only. A candidate outside `AllowedSymbols` is information only — the risk engine will refuse an
order for it — and the specialist is instructed to say so rather than present it as actionable. Pass
`symbols` explicitly to scan something else again; the same `tradable` flag applies.

### What the scan computes

All of it is deterministic (`Analysis/TechnicalAnalyzer.cs`) — no model produces any number, which is
what lets the specialist quote the figures without breaking its "never invent a price" rule:

- **Levels** — swing pivot highs/lows plus range and 52-week extremes, merged into clusters with a
  touch count. Levels are classified support/resistance by where they sit relative to the *current*
  price, so a broken support correctly reappears as overhead resistance.
- **Position** — nearest support/resistance, percent distance to each, and range position (0 = on the
  range low, 1 = at the high).
- **Indicators** — SMA20/50, RSI(14), ATR(14), volume vs 30-session average, consecutive up/down runs.
- **Trade math** — entry at support, stop one ATR below it, target at the nearest resistance, and the
  resulting reward:risk. The math is buy-side; on a sell setup the target level *is* the sell level,
  and the reasons say so explicitly.
- **Setup** — `buy_at_support`, `sell_at_resistance`, `wait`, `avoid_breakdown`, or
  `insufficient_data`.

`avoid_breakdown` is the load-bearing one: a stock making fresh range lows on consecutive down
sessions is at the bottom of its range *because it is still falling*. It is reported under `avoid`,
never as a buy candidate — that is exactly the trade a naive "price near the low" screen would hand
you. A pullback that holds above the prior low is still a normal `buy_at_support`.

### Intraday candles

`analyze_candles` with `interval` set to `60m`, `30m`, `15m`, or `5m` returns true intraday OHLCV.
There is no intraday endpoint on the portal — bars are aggregated from the **complete tick tape** of
the current session (`GET /timeseries/int/{symbol}`), which publishes every executed trade. The
aggregation is verifiable: for FFC on 2026-08-11 the tape held 4,140 trades whose quantities summed to
1,062,699, exactly the day's published volume, and rebucketing at 5m/15m/60m conserved that volume
and the session's O/H/L/C exactly.

**The constraint that shapes everything: PSX serves the current session only.** The tick endpoint
ignores a date parameter and no historical intraday exists anywhere on the portal. So:

- Every intraday call rebuilds today from ticks (never trusting a bar that was still forming when it
  was last saved) and appends any **archived** earlier sessions.
- Completed bars are written to `intraday_bars` in `trading.db` when `Scan.ArchiveIntradayBars` is on
  (default). Intraday history therefore **accumulates from the day it is switched on** — expect a few
  weeks before multi-session intraday levels mean anything. Only symbols actually analysed get
  archived, so run the analysis across the watchlist near the close if you want the whole list covered.
- Until then, a warning says how many sessions the series covers, and every intraday result carries
  `daily_context` — the daily levels, setup, and trade plan.

That last point is the intended workflow, not a workaround: **levels come from the daily candles,
timing comes from the intraday bars.** The specialist is instructed to trade the daily levels and use
intraday only to time the entry against them, because levels drawn from a single session's range are
noise. Bar boundaries are epoch-aligned, which lands them on clean exchange-clock times (PKT is UTC+5
with no DST), so a 60m series breaks at `:00` rather than 60 minutes after the first trade.

Cost is one request per symbol per call (~90 KB for a liquid name), so intraday checks on a handful of
scan candidates are cheap.

### Data sources and cost

Candles come from the official portal's market-wide tables, which is why a scan is cheap:

| Endpoint | Provides |
|---|---|
| `POST dps.psx.com.pk/historical` (`date=yyyy-MM-dd`) | Settled daily OHLC for **every** symbol on one date |
| `GET dps.psx.com.pk/market-watch` | The live forming daily bar for **every** symbol |
| `GET dps.psx.com.pk/timeseries/int/{symbol}` | Every trade of the current session, for one symbol — the source of all intraday bars |

History therefore costs **one request per trading day, regardless of symbol count** — a 12-symbol and
a 200-symbol scan load the same days. Settled dates are immutable and cached for the process
lifetime, so:

- **First scan of a session: ~25–35 s** (about 68 requests for a 60-day window).
- **Every scan after that: well under a second**, plus one request for the live bar.

Two portal behaviours the implementation has to work around, both observed live:

- A rate-limited request is answered with **HTTP 200 and an empty table**, indistinguishable from a
  market holiday. Empty days are therefore cached for only 15 minutes, and a scan that recovers
  materially fewer sessions than requested says so in `warnings` instead of quietly analysing a
  third of the intended history.
- The live market-watch table's column labelled `CURRENT` is `data-name="close"` — during a session
  it is the last trade, not a settled close. Both tables are read by header `data-name`, so a
  reordered column cannot shift volume into a price field.

### Daily trading mode

Scheduled scanning uses the existing `CronScheduler` — no extra configuration. Add a job whose task
runs the scan, for example:

```
Ask the trading agent to scan the watchlist for buy setups at support and sell setups at
resistance, then report the top candidates with their levels, entry, stop, target and reward:risk.
```

Schedule it a little after the open (say 09:45 PKT) so the live bar has formed. The scan only
proposes: everything it produces still passes through `ExecutionMode`, the risk engine, and HITL
before any order exists.

A second job near the close (say 15:35 PKT) that runs `analyze_candles` at `15m` across the watchlist
archives that session's intraday bars, so intraday history accumulates for every symbol rather than
only the ones you happened to ask about.

---

## Safety gates summary

All gates are evaluated in `PlaceOrderTool` before the browser is touched:

| Gate | Config key | Behaviour on fail |
|---|---|---|
| AutoExecute flag | `TradingAgent.AutoExecute` | Returns `skipped` — never reaches the broker |
| Confidence gate | `TradingAgent.MinConfidence` | Returns `skipped` |
| Order value cap | `Ahk.MaxOrderValuePkr` | Returns an error — order blocked |
| Duplicate filter | `TradingAgent.DuplicateWindowMinutes` | Returns `skipped` |
| HITL approval | `Hitl.RequireApprovalForTools` | ApprovalRequired startup validates both execution tool names |
| Market calendar | regular sessions + configured holidays/overrides | Trading Manager checks deterministically immediately before execution |
| Configured universe | `TradingAgent.AllowedSymbols` | Empty or unknown universe fails closed by default |
| Kill switch | `TradingAgent.KillSwitch` | Blocks all orders independently of the LLM |
| Durable idempotency | SQLite unique key | Restarts and repeated signals return the persisted result instead of resubmitting |

---

## Recommended startup sequence

1. Set `AutoExecute: false` and start AgentFox
2. Send a test signal via the bridge: `BUY OGDC @ 165 target 185 sl 158`
3. Verify the agent calls `parse_signal` → `check_market` → `log_signal` and produces a correct summary
4. Check `logs/trading/signals_YYYYMMDD.jsonl` for the recorded entry
5. Configure `AllowedSymbols`, set `ExecutionMode: "ApprovalRequired"`, enable `AutoExecute`, and add both `place_order` and `place_orders` to HITL
6. Send another test signal and confirm the HITL approval prompt arrives on the channel
7. Approve with `/approve <id>` and verify the browser opens and fills the AHK form
8. Confirm the submit button selector (see [AHK Submit Button](#ahk-submit-button))
9. Only after a successful dry-run approval: remove HITL if fully automated trading is desired
