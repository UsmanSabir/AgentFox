# Phase B runbook — live market test of the broker feed and order cancel

**Self-contained.** Everything needed to run this on a fresh machine in a fresh session, with no
prior context. Phase A (pre-open, no orders) is already complete and passed — see
`ahk-live-test-plan.md`. This document covers only what requires an open market.

**This trades a real account with real money.** Every step is sized so the worst case is a few
thousand rupees, and the guardrails in §2 are not optional.

---

## 0. Status — what is already proven

Access was blocked by the broker on 2026-08-18 mid-test and has since been **restored**. Suspected
rate limiting; the leading contributor was **~15 browser logins in about two hours** from repeated
host restarts, not the feed poll (which matches the portal's own 1–2s cadence).

**Keep it that way:** start the host once and leave it running. The `session_ahk` profile persists a
logged-in session so restarts usually skip the login; deleting it or changing credentials forces a
fresh login every time. For order-only work, run with `Plugins__AhkFeed__Enabled=false` — the whole
place/read/cancel/verify cycle then costs about **7 requests**.

### Already confirmed live (pre-open, `OHO`)

| | Status |
| --- | --- |
| Order **placement** | **CONFIRMED** — order 6427, verified in the account's outstanding log |
| Order **book read** | **CONFIRMED** |
| Order **cancellation** | **CONFIRMED** — 2 orders cancelled, `verified:true`, ~2.1s each |
| Feed, watchlist sync, quotes to consumers | **CONFIRMED** (see `ahk-live-test-plan.md`) |

PSX's `OHO` pre-open state accepts orders, which queue for the open — the order gate now prefers the
broker's own reported state rather than a hardcoded 09:32–15:30 clock (`OrderWindow`).

So **B2/B3 below are a regression check, not a first run.** They remain worth doing during an open
session, because behaviour under live matching may differ from OHO.

### One thing to watch for

If `list_outstanding_orders` ever **fails** rather than returning `count: 0`, that is the safety fix
working: the book could not be read. Previously that condition looked identical to a flat account, and
it caused a cancel to be reported `verified: true` while the order was still live. Never treat a
failed read as "no orders".

PSX sessions: **Mon–Thu 09:32–15:30 PKT**, **Fri 09:17–12:00 and 14:32–16:30**.

## 1. Prerequisites

**Repo and build**
```bash
cd <repo>/src
dotnet build AgentFox.sln
```

**Broker credentials** must be in `src/Agent/bin/Debug/net10.0/appsettings.user.json`, nested under
`Plugins` — this is the trap that cost time on the first attempt:

```jsonc
{
  "Plugins": {
    "Ahk": {
      "PortalUrl":   "https://web.ahletrade.com/",
      "Username":    "...",
      "Password":    "...",
      "TradingPin":  "...",
      "Headless":    false,
      "ExecutablePath": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe"
    }
  }
}
```

A top-level `"Ahk"` (not under `Plugins`) is **silently ignored** — the only symptom is the login
failing as though the password were blank.

**LM Studio** must be serving, with a model loaded. Verify from the machine that will run AgentFox:
```bash
curl -s http://<lm-studio-host>:1234/v1/models
```
On the original machine the working address was `http://192.168.100.50:1234/v1` with
`qwen2.5-14b-instruct`; `localhost`, `127.0.0.1` and `172.17.80.1` were all unreachable. Find the one
that answers on **your** machine and use it.

**Note:** `appsettings.user.json` also pins `LLM.BaseUrl` and `LLM.Model`, and it overrides
`appsettings.json`. Passing them as environment variables (below) sidesteps both files.

---

## 2. Guardrails — mandatory, and here is why

The model **cannot be trusted to pass exact order parameters.** Given the explicit instruction
*"quantity=10"*, `qwen2.5-14b-instruct` **omitted `quantity` entirely**. `place_order` then auto-sized
from `PerStockBudgetPkr` and tried to place **75 shares for 48,750 PKR** — 7.5× the intended order,
and entirely *within* policy, because with no quantity the effective ceiling is the budget rather than
whatever the requester had in mind.

Three settings close that, all as environment variables (they override every config file and vanish
when the process exits):

| Variable | Value | Purpose |
| --- | --- | --- |
| `Plugins__TradingAgent__RequireExplicitQuantity` | `true` | **The direct fix.** Refuses any order that omits `quantity`, reporting the size budget-sizing *would* have used. Prefer this over relying on the caps below. |
| `Plugins__Ahk__MaxOrderValuePkr` | `7500` | Hard ceiling — rejects anything above ~10 shares at these prices. |
| `Plugins__Ahk__PerStockBudgetPkr` | `6600` | Backstop: if auto-sizing is ever reached, it lands at ~9 shares. |

Auto-sizing now also logs at **WARNING** and returns an `auto_sized_warning` field in the tool result,
so an omission is visible in the answer the caller acts on rather than only in the log.

Still verify each order's **actual** arguments in the audit trail (§6) after **every** placement.

## 3. Start the host

```bash
cd <repo>/src

Modules="web,trading-agent" \
Logging__MinLevel="Information" \
Plugins__AhkFeed__Enabled="true" \
Plugins__TradingAgent__RequireExplicitQuantity="true" \
Plugins__Ahk__MaxOrderValuePkr="7500" \
Plugins__Ahk__PerStockBudgetPkr="6600" \
LLM__BaseUrl="http://192.168.100.50:1234/v1" \
LLM__Model="qwen2.5-14b-instruct" \
dotnet run --project Agent/
```

Notes:
- `Modules="web,trading-agent"` drops the `cli` module, which otherwise wants an interactive stdin.
- `Logging__MinLevel="Information"` is required or the `[AhkFeed]` / `[AhkPortal]` lines are invisible.
- Logs go to **`src/logs/agentfox.log`**, not stdout. Stdout only shows the startup banner.
- Leave `OnlyDuringMarketHours` at its default (`true`) — during market hours it polls normally.
- A visible Chrome window will open (`Headless: false`). That is expected. **Do not interact with it.**
- **Start the host ONCE.** Repeated restarts each perform a full broker login, and ~15 logins in two
  hours is the suspected cause of the 2026-08-18 access block. Change settings with environment
  variables on the next start, not by restarting mid-session.

Watch for:
```
[AhkBroker] Login successful.
[AhkPortal] Direct API session established for account CC45698.
[AhkFeed] Subscribed 30 symbol(s) across 4 page(s).
```

---

## 4. B0 — pre-flight (no orders)

```bash
curl -s http://localhost:8080/api/trading/feed/status | python -m json.tool
```

Require **all** of:
- `"enabled": true`, `"healthy": true`, `"sessionEstablished": true`
- `"portalMarketStatus": "OPEN"` ← if this still says `OHO` or `CLOSED`, **stop and wait**
- `"subscribedSymbols"` ≈ `"freshSymbols"` (30 by default)
- `"secondsSinceUpdate"` under ~5
- `"silentPolls": 0`, `"consecutiveFailures": 0`

Then confirm the book is empty before you add to it:
```bash
curl -s -X POST http://localhost:8080/api/specialist-agents/trading-agent/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Call list_outstanding_orders with no filters and report the count."}'
```
Expect `count: 0`. **If it is not 0, stop** — there are pre-existing orders and this runbook's
symbol-based steps could touch the wrong one.

---

## 5. B1 — the feed under live trading

Let it run 2–3 minutes after the open, then:

```bash
curl -s "http://localhost:8080/api/trading/candles?symbol=MARI&bars=3&includeLive=true" \
  | python -c "import json,sys; d=json.load(sys.stdin); [print(c) for c in d['candles'][-2:]]"
```

**Check 1 — the live bar is moving.** The `isLive: true` bar for today should now show a genuine
intraday range (`high` > `low`, volume climbing), not yesterday's values repeated. Pre-open it
correctly showed reference data.

**Check 2 — AHK is fresher than PSX.** This is the whole premise of the work, so measure it:
```bash
curl -s "https://dps.psx.com.pk/market-watch" | grep -A5 -i "MARI" | head -20
```
Compare the PSX last price against the feed's. Note the difference and roughly how far PSX lags.

**Check 3 — depth is present.** Bid/ask is what PSX cannot provide. Confirm `BestBid`/`BestAsk` are
populated (visible via the chart endpoint's snapshot, or the trading UI at `/ext/trading`).

**Record the live price and the day's band before placing anything** — §6 depends on it.

---

## 6. B2 — BUY: place → verify → cancel → verify

**Re-derive the price first.** Do not hard-code 650. Take the current MARI price and set the limit
**~4–5% below** it, and confirm that value sits above the day's Lower Lock. If MARI gapped overnight,
650 could now be below the lock, and `ClampPriceToBand` would silently raise it *toward* market —
the one way a deliberately-unfillable test order becomes fillable.

Reference: MARI closed **679.56** on 2026-08-17; ±10% band ≈ **611.6 / 747.5**.

**Step 1 — place.**
```bash
curl -s -X POST http://localhost:8080/api/specialist-agents/trading-agent/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Call place_order once with: action=BUY, symbol=MARI, quantity=10, price=<YOUR_PRICE>, order_type=LIMIT, confidence=HIGH. The quantity argument is REQUIRED - you must include quantity=10. Do not pass target. Report what the tool returned."}'
```

**Step 2 — verify the arguments actually used.** Non-negotiable, given §2:
```bash
cd src/Agent/bin/Debug/net10.0
ls -t sessions/specialist/trading-agent/*.md | head -1 | xargs cat
```
Find the `[tool_call]` line and confirm `quantity` is **10** and `price` is what you intended. If the
model omitted `quantity` again, the auto-size path caps it at ~9 shares — acceptable, but know which
happened before continuing.

**Step 3 — confirm it is resting, and capture the order number.**
```bash
curl -s -X POST http://localhost:8080/api/specialist-agents/trading-agent/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Call list_outstanding_orders with no filters. Report every order with its order_no, side, symbol, price and remaining quantity."}'
```
Read the `order_no` from the **`[tool_result]`** in the audit file, not the model's prose.

> **If nothing is resting:** the order did not place. Do **not** retry blindly — check the portal's
> Activity Log in the open Chrome window first. A silently-placed order that has not yet surfaced in
> the outstanding book is exactly the case the verification logic exists for, and a blind retry
> doubles the position.

**Step 4 — cancel by order number.**
```bash
curl -s -X POST http://localhost:8080/api/specialist-agents/trading-agent/chat \
  -H "Content-Type: application/json" \
  -d '{"message":"Call cancel_order with order_no=<ORDER_NO>. Report exactly what the tool returned."}'
```

Expected in the tool result:
```json
{"cancelled": true, "verified": true, "order_no": "...", "message": "... no longer in the broker's outstanding order book."}
```

`verified: true` means the tool re-read the order book and the order is genuinely gone. The portal
itself returns **no** success indicator — its own UI fires the cancel and closes the dialog without
reading the response — so this re-read is the only real evidence.

**If you get `cancelled: false, verified: false`:** the request was accepted but the order was still
in the book 8 seconds later. It may be processing at the exchange, or the cancel may not have taken.
**Do not retry blindly.** Re-run `list_outstanding_orders`; if it is still there after a minute,
cancel it manually in the Chrome window.

**Step 5 — confirm the book is empty again.** Re-run `list_outstanding_orders`, expect `count: 0`.

---

## 7. B3 — SELL: same shape

The account holds **75 MARI** (avg 646.12, verified 2026-08-18 07:46), so a sell of 10 is covered and
will rest rather than being rejected. PSX does not permit retail short selling, so this leg only works
because the position exists — re-confirm with `get_portfolio` if any time has passed.

Set the limit **~4–5% above** the live price, below the Upper Cap. Then repeat §6 steps 1–5 with
`action=SELL`.

If it filled, you would sell 10 of 75 at a profit against the 646.12 average — a benign worst case,
which is why this leg is safe to run.

---

## 8. B4 — the feed survives the orders

**The highest-value check in the whole runbook.** Placing an order opens the portal's trading screen,
and the portal's own `site.js` re-subscribes `Page1` from its (empty) market-watch table on every page
load — which **wipes the feed's subscription**. There is a fix for this; B4 confirms it under real
conditions.

After B2/B3, check the log:
```bash
grep -E "browser released|Re-subscribing|Subscribed .* page" src/logs/agentfox.log | tail -5
```

Expect, after each order:
```
[AhkFeed] Re-subscribing because the browser released the trading screen and its page load
          will have overwritten the subscription.
[AhkFeed] Subscribed 30 symbol(s) across 4 page(s).
```

Then confirm quotes are still flowing:
```bash
curl -s http://localhost:8080/api/trading/feed/status | python -m json.tool
```
`freshSymbols` back to ~30 and `secondsSinceUpdate` low. **If `silentPolls` is climbing, the feed has
gone quiet** — that is the failure this fix exists to prevent, and it would mean the fix did not hold.

---

## 9. Abort conditions

Stop and reassess if any occur:

- **A test order fills.** Nothing is necessarily broken, but the premise (an unfillable resting order)
  is wrong and continuing on that assumption is not safe.
- **`cancel_order` returns `verified: false`** and the order is still there after a minute — cancel it
  manually in the portal before doing anything else.
- **Repeated `[AhkPortal]` session-expired warnings** — session handling is misbehaving and further
  orders would compound it.
- **Any order appears that nobody asked for.**
- **`list_outstanding_orders` shows an order you did not place.**

**Emergency stop:** set `killSwitch` via the trading API, or just kill the host process
(`taskkill /F /IM AgentFox.exe`). Note the kill switch blocks *placement* but deliberately **not**
cancellation — cancelling reduces risk, and a stop that also blocked cancels would trap the account in
the exposure it was flipped to escape.

**Manual recovery:** the Chrome window is a fully logged-in portal session. Cancel anything by hand
from the Outstanding Log tab.

---

## 10. Cleanup

1. Confirm `list_outstanding_orders` shows `count: 0`.
2. Stop the host. The `Plugins__Ahk__*` guardrails were environment variables, so they disappear with
   the process — no config to revert.
3. `git status` should show no changes to `appsettings.json` from the test.

---

## 11. What to report back

- B0 feed status JSON at the open.
- B1: whether the live bar showed real intraday movement; the AHK-vs-PSX price difference and lag;
  whether bid/ask was populated.
- B2/B3: for each — the `[tool_call]` arguments actually used, the `order_no`, and the full
  `cancel_order` result including `cancelled` / `verified`.
- B4: the re-subscribe log lines, and feed status after the orders.
- Anything in §9 that triggered.
