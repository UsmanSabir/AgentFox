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
    "DuplicateWindowMinutes": 60,
    "DatabasePath":           "trading/trading.db",
    "AllowedSymbols":         ["OGDC", "PPL"],
    "RequireConfiguredSymbols": true,
    "MaxOrdersPerBatch":      10,
    "MaxBatchValuePkr":       250000,
    "RequireReconciliationHealthy": true,
    "ReconciliationIntervalSeconds": 60,
    "ReconciliationMaxAgeSeconds": 180,
    "MarketHolidays":         [],
    "MarketSessionOverrides": []
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
| `MarketSessionOverrides` | `[]` | Date-specific `Closed` or `Sessions` overrides such as `09:30-13:00`. |
| `RequireReconciliationHealthy` | `true` | Blocks live modes when broker fills, positions, and balances cannot be reconciled. The current AHK browser adapter reports unsupported, so live entry execution remains fail-closed. |
| `MinConfidence` | `HIGH` | Minimum signal confidence required before placing an order (`HIGH`, `MEDIUM`, `LOW`). |
| `ParserModelKey` | `CheapModel` | Reserved for future use — will resolve to a named model from the `Models` config section once `IModelClientFactory` is available in AgentFox.Plugins. Currently uses the default `IChatClient`. |
| `DuplicateWindowMinutes` | `60` | Identical messages received within this window are silently discarded. |
| `DefaultQty` | `100` | Shares to trade when the signal message does not specify a quantity. |
| `MaxOrderValuePkr` | `50000` | Hard cap: `qty × price` above this value is rejected before the browser is touched. |
| `SessionDir` | `session_ahk` | Directory for the persistent Chromium profile (keeps the AHK session logged in). |
| `LogDir` | `logs/trading` | Directory for signal JSONL logs and order screenshots. |

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
