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
      daily OHLC candles → support/resistance levels → buy-at-support / sell-at-resistance
      (breakdowns excluded), ranked by distance to level and reward:risk
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
      "MaxResults":                 10
    }
  },
  "Ahk": {
    "PortalUrl":        "https://www.ahktrading.com",
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
| `Scan.MarketDayFetchConcurrency` | `4` | Concurrent portal requests while warming a cold candle cache (1–8). |
| `Scan.MaxCachedMarketDays` | `120` | Settled sessions kept in the in-memory candle cache. |
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
| `analyze_candles` | One symbol in depth: levels, indicators, and a suggested entry/stop/target. |

`research_stock` also carries a `technical` section now, so a tip's stated entry is judged against
real support and resistance rather than only the 52-week range.

### Why the universe is `AllowedSymbols`

`scan_watchlist` defaults to `AllowedSymbols` — the same list `TradingRiskEngine` enforces at order
time. Recommending outside it produces proposals the risk engine refuses, so the scanner and the
executor deliberately read one list. Pass `symbols` explicitly to scan something else; the tool notes
that those cannot be executed.

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

### Data sources and cost

Candles come from the official portal's two market-wide tables, which is why a scan is cheap:

| Endpoint | Provides |
|---|---|
| `POST dps.psx.com.pk/historical` (`date=yyyy-MM-dd`) | Settled OHLC for **every** symbol on one date |
| `GET dps.psx.com.pk/market-watch` | The live forming bar for **every** symbol |

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
