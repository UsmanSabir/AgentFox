# Live test plan — broker feed and order cancel

End-to-end validation of the two things built against the AHK portal's JSON API: the live quote feed
(`AhkFeedWorker`) and order cancellation (`cancel_order`). Everything here runs against a **real
brokerage account with real money**, so the plan is built around keeping the worst case small rather
than around convenience.

## The constraint that shapes everything: this cannot all be done before the open

The request was to test before market open. Half of it can be, and half cannot:

- **Order cancel cannot.** `AhkConfig.VerifyOrderInBook` records what was learned against the live
  portal: an off-hours submission returns **HTTP 200 with an empty body and a green success alert
  while placing nothing at all**. So a pre-open test would show a "successful" placement, nothing in
  the order book, and a cancel with nothing to cancel — and every one of those results would be
  indistinguishable from a broken implementation. There has to be a resting order to cancel, and
  that needs a live session.
- **The feed cannot either, for real data.** Pre-open, `GetFeed` returns
  `{"feed":[], … ,"marketStatus":"OHO"}`. The plumbing can be proven; the quotes cannot.

So the plan splits in two. **Phase A** is everything provable with zero orders and zero risk, and can
run right now. **Phase B** needs the market open and needs someone watching.

PSX sessions (from `PsxMarketCalendar`): Mon–Thu **09:32–15:30 PKT**; Fri **09:17–12:00** and
**14:32–16:30**.

---

## Before either phase: four gates that will silently block the test

These are configuration facts, not bugs. Each one would make a working implementation look broken.

**1. The default order size exceeds the value cap.** `Ahk.MaxOrderValuePkr` is 50,000 and
`Ahk.DefaultQty` is 100. MARI at 650 × 100 = **65,000 PKR**, so the risk engine rejects the order
before it ever reaches the broker, and the test never gets as far as exercising the portal. Pass an
explicit small quantity. At 650 the cap allows at most 76 shares; **use 1–10** — the point is to test
the mechanism, and a smaller quantity is a smaller worst case if the order somehow fills.

**2. `BoundedAuto` submits with no approval prompt.** `ExecutionMode` is currently `BoundedAuto`,
which `ApprovalGate` treats as "the operator has authorised unattended execution". There is no
confirmation step — the order goes to the broker the moment the tool is called. Consider switching to
`ApprovalRequired` for the duration of the test so there is a human gate on each submission, and
switching back afterwards.

**3. The SELL leg needs MARI holdings.** PSX does not permit retail short selling, so a sell of stock
the account does not hold will be rejected by the broker. That is a valid negative test but it does
**not** exercise cancel, because nothing rests. Check holdings with `get_portfolio` first; if there is
no MARI position, run the cancel test on the BUY leg only.

**4. Price-band clamping can move the price toward the market.** `Ahk.ClampPriceToBand` silently
raises a BUY below the Lower Lock up to the lock. If the intended price is outside the band, the
order that actually gets placed is closer to market than intended — which is the one way a
deliberately-unfillable test order becomes fillable.

### The chosen levels check out

MARI closed at **679.56** on 2026-08-17. At PSX's ±10% band:

| | Level | Distance from close | Band | Clamped? |
| --- | --- | --- | --- | --- |
| Lower Lock | ~611.6 | −10% | — | — |
| **BUY test** | **650** | −4.4% | inside | no |
| **SELL test** | **710** | +4.5% | inside | no |
| Upper Cap | ~747.5 | +10% | — | — |

Both rest, neither clamps. The residual risk is a genuine 4.4% intraday move against the test order;
MARI has been moving under 1% a day recently, so this is small but not zero — which is the reason for
a quantity of 1–10 rather than 100.

**Re-derive these from the live price on the day, do not hard-code them.** If MARI gaps overnight,
650 could land near or below the Lower Lock and get clamped upward toward market. Check the live
price and the band first (`GetUpperLowerCap` gives the whole market's bands in one call).

---

## Where the running app actually reads its configuration

Editing `src/Agent/appsettings.json` does **not** affect a running host. `Program.cs` sets
`ContentRootPath = AppContext.BaseDirectory` and clears the default providers, so the stack is:

```
appsettings.defaults.json   (in bin/ — the build copies appsettings.json here; replaceable baseline)
appsettings.user.json       (in bin/ — authoritative, never shipped in a release archive)
environment variables       (highest precedence before command line)
command line
```

So a source edit needs a **rebuild** to reach `appsettings.defaults.json`. For a one-off test run,
environment variables are cleaner because they override everything and leave no file to revert:

```bash
Modules="web,trading-agent" Logging__MinLevel="Information" Plugins__AhkFeed__Enabled="true" Plugins__AhkFeed__OnlyDuringMarketHours="false" dotnet run --project Agent/
```

Two further notes. `Logging:MinLevel` defaults to `Warning`, so the `[AhkFeed]` / `[AhkPortal]`
Information lines are invisible without raising it — and they go to the **file** logger
(`logs/agentfox.log` under the working directory), not to stdout. And `OnlyDuringMarketHours` must be
relaxed for any pre-open run, or the worker correctly refuses to poll and proves nothing.

---

## Phase A — pre-open, no orders placed (zero risk)

Runnable any time, including right now. Proves session handling, subscription, and the cancel tool's
refusal paths without touching the market.

**Setup**
1. Set `Plugins:AhkFeed:Enabled = true` in `appsettings.json`.
2. Confirm `Plugins:Ahk:PortalUrl` is `https://web.ahletrade.com/` and credentials are populated.
3. Confirm MARI is in the monitored universe (it is already in `AllowedSymbols`).
4. Start the host and watch the log for `[AhkFeed]` and `[AhkPortal]` lines.

**A1 — Session harvest works outside the browser.** *This is the single biggest unverified
assumption in the whole implementation.* `AhkPortalClient` takes cookies from the Puppeteer session
and uses them from a plain `HttpClient`; that was never proven end to end, because the one attempt to
verify it with curl was blocked. If the portal rejects a non-browser client, everything else fails
here and nothing later in this plan will work.
- Expect: `[AhkPortal] Direct API session established for account CC45698.`
- Failure looks like: repeated `session expired` / `redirected to …` warnings.

**A2 — Subscription is accepted.** Expect `[AhkFeed] Subscribed N symbol(s) across 4 page(s).`

**A3 — Feed responds, market correctly reported shut.** `GetFeed` returns 200 with an empty `feed`
and `marketStatus` of `OHO` or `CLOSED`. An empty feed here is the **correct** result.

**A4 — Order book reads.** Ask the agent to list working orders. Expect
`list_outstanding_orders` → `count: 0` (already confirmed live: the endpoint returns `[]`).

**A5 — Cancel refuses cleanly rather than crashing.** Ask it to cancel order number `99999999`.
Expect a clean failure naming the problem, not an exception.

**A6 — The subscription-clobber fix works.** This is the fix for the empty-watchlist problem: the
portal's `site.js` re-subscribes Page1 from its own (empty) watch table on every page load, so
opening the trading screen wipes our subscription out from under the feed.
- Trigger `get_portfolio`, which launches the browser onto the trading screen.
- Expect during: `[AhkFeed] Browser holds the trading screen; skipping this poll.`
- Expect after: `[AhkFeed] Re-subscribing because the browser released the trading screen …`
  followed by `[AhkFeed] Subscribed N symbol(s)…`
- If that re-subscribe line never appears, the feed will go silent after every order placed for the
  rest of the session.

---

## Phase B — market open, real orders (someone must be watching)

Start after **09:32 PKT**, ideally 15+ minutes in so there is real trade flow. Do not start in the
first minute — the open auction is the least representative moment.

**B1 — Live quotes (no orders).** Let the feed run 2–3 minutes, then ask the agent for a watchlist
scan or run `analyze_candles` on MARI.
- Expect `marketStatus: OPEN`, the book filling to roughly the subscribed symbol count.
- Expect quotes tagged `source: "ahk"` carrying **bid/ask** — the thing PSX cannot provide.
- **Cross-check the value:** compare the AHK last price against `dps.psx.com.pk` for the same symbol.
  The whole premise is that AHK is fresher; this is where that gets confirmed rather than assumed.
- Also worth settling here: whether `GetFeed` is a snapshot or a drain-once queue (currently
  unknown — see `ahk-feed-api.md`). If a single poll returns every subscribed symbol, it is a
  snapshot; if it returns only what moved, it is a delta. The implementation is correct either way,
  but knowing which one closes an open question.

**B2 — BUY, verify, cancel, verify.** The core test.
1. Check MARI's live price and band; adjust the test price if it is no longer ~4% below market.
2. Place: **BUY MARI, quantity 1–10, limit 650, order type LIMIT.**
3. `list_outstanding_orders` → the order appears, with an `order_no`. **Record that number.**
4. `cancel_order` with that `order_no`.
5. Expect `cancelled: true, verified: true` — the tool re-reads the book and only reports success
   once the order has actually left it.
6. `list_outstanding_orders` → `count: 0`.

**If step 3 shows nothing**, the order did not place. Do **not** retry blindly — check the Activity
Log, because a silently-placed order that the book has not yet shown is exactly the case the
verification logic exists for.

**B3 — SELL, same shape.** Only if the account holds MARI. **SELL MARI, quantity 1–10, limit 710**,
then the same verify → cancel → verify.

**B4 — Feed survives the order.** After B2/B3, confirm quotes are still flowing. This is A6 under
real conditions and is the highest-value check in the whole plan: order placement wiping out the
price feed would be a silent, session-long failure.

---

## Abort conditions

Stop and reassess if any of these occur:

- A test order **fills**. Nothing is broken by this per se, but the test's premises are wrong and it
  should not continue on the same assumptions.
- `cancel_order` returns `verified: false`. The order may still be live — resolve it manually in the
  portal before doing anything else.
- Repeated `[AhkPortal]` session-expired warnings — the session handling is wrong and further orders
  would compound the problem.
- Any order appears that nobody asked for.

**Rollback:** set `Plugins:AhkFeed:Enabled = false` and restart. Every consumer reverts to the PSX
market watch, and the only thing lost is freshness. Cancellation and order listing keep working —
they do not depend on the feed.

---

## Phase A run log — 2026-08-18 06:30–06:42 PKT

Attempted pre-open. **Blocked at A1: no broker credentials are configured**, so nothing past the
login could be exercised. `Plugins:Ahk:Username` / `Password` / `TradingPin` are empty in
`appsettings.defaults.json`, absent from `appsettings.user.json`, absent from `plugin-configs/`
(no encrypted overlay), and unset as `Plugins__Ahk__*` environment variables. The failure is explicit:

```
[AhkBroker] Portal requested character #2 but the configured password has only 0 character(s).
[AhkPortal] No session cookies available; the direct API stays offline.
```

**Proved anyway** (worth having, none of it needed credentials):

- The DI graph resolves — previously verified only by inspection.
- The plugin registers **15 tools**, up from 13: `list_outstanding_orders` and `cancel_order` load.
- `AhkFeedWorker` starts, honours env-var config, and targets the corrected portal URL.
- The whole session path runs and fails **soft**: a specific, actionable warning, no exception, host
  keeps serving, quotes fall back to PSX exactly as designed.

**Two real defects found by running it, both fixed:**

1. `AhkBroker.BrowserHoldsTradingScreen` was `_initialized && _page is not null` — "a browser
   exists", not "the trading screen is in use". Any surviving browser (a failed login, or
   `CloseBrowserAfterOrder = false`) latched it true, and the feed worker then yielded to a browser
   doing nothing **for the rest of the session**, never polling again. The only symptom was silence
   at Debug level. Now an `Interlocked` counter incremented solely by the four operations that
   actually drive the portal UI.
2. No backoff on session-establishment failure. Establishing a session launches Chromium and performs
   a real login, so failure retried on the 2s quote cadence is a browser relaunch and a login attempt
   every two seconds — a local resource fire and the sort of traffic that gets an account locked.
   (Defect 1 was masking this.) Now 30s → 60s → … capped at 10 minutes, verified in the log.

**Still unverified — the critical one.** Whether the portal accepts a plain `HttpClient` carrying
browser-harvested cookies (A1). Everything downstream depends on it and it needs credentials.

`get_portfolio` could not be run either: it authenticates through the same browser login, so the MARI
holdings question for the B3 sell leg remains open.

---

## Phase A run 2 — 2026-08-18 06:50–07:02 PKT, with credentials

Credentials had been added to `appsettings.user.json` at the **top level** as `"Ahk"`, but the
binding is `Plugins:Ahk`, so they were not read at all. Moved under `Plugins` (a `.bak` of the
original sits beside it). Worth knowing: a misplaced section is silently ignored, and the only
symptom is the login failing as though the password were blank.

### Results

| Check | Result |
| --- | --- |
| **A1 — session from a plain HttpClient** | **PASS.** `Login successful` → `Handed 8 session cookie(s)` → `Direct API session established for account CC45698`. The portal accepts a non-browser client carrying browser-harvested cookies. This was the single biggest unknown in the design. |
| **A2 — subscription accepted** | **PASS.** `Subscribed 30 symbol(s) across 4 page(s).` |
| **A3 — feed responds** | **PASS, and better than expected** — see below. |
| **Bonus — quotes reach consumers** | **PASS.** `/api/trading/candles?symbol=MARI` returned a live bar for today, `isLive: true`, close 680.00, on top of the settled 17-Aug bar at 679.56. |
| A4 / A5 / A6 | **Blocked** — LM Studio (`127.0.0.1:1234`) is not running, so no agent tool can be invoked. Not an implementation problem. |
| MARI holdings (for B3) | **Blocked** on the same thing. |

### Two corrections to what this document previously claimed

**The feed works pre-open.** `/api/trading/feed/status` during `marketStatus: OHO` reported
`bookSymbols: 30, freshSymbols: 30, secondsSinceUpdate: 0.6`. The earlier conclusion that the feed is
empty outside market hours was wrong — it was empty because the captured session had **no
subscription**. Once subscribed, the portal serves reference data pre-open.

**`GetFeed` is a SNAPSHOT, not a drain-once delta queue.** Sampled twice eight seconds apart with
nothing trading; the book's last-update timestamp advanced on every poll, so each response carries
all 30 subscribed symbols. Consequences: a second reader cannot steal data, so the browser polling
`GetFeed` alongside us is harmless, and the yield-to-browser logic is politeness rather than
correctness. The subscription-clobber problem is unaffected and remains real.

### A third defect found and fixed

`AhkFeedConfig.Pages` defaulted to the four page names as a property initializer. .NET's
`ConfigurationBinder` **appends** to a pre-populated collection instead of replacing it, so the four
names in appsettings bound to a list of **eight** — every slot duplicated. Since slots are
overwritten in order, index 4 re-sent `Page1` with the empty slice at that offset and **wiped the
30-symbol subscription index 0 had just made**. The live log said `Subscribed 30 symbol(s) across
8 page(s)`, which is the only reason it was caught.

Fixed by defaulting `Pages` to empty with the fallback and de-duplication in `FeedPagePlanner`,
covered by four regression tests. This one would have produced exactly the symptom to expect:
a feed that connects, subscribes, reports healthy, and returns nothing.

### Also added

`GET /api/trading/feed/status` — every failure mode of this worker is silent, and there was no way to
tell a lost subscription from a dead session from a quiet market without reading Debug logs. Reports
session state, account, subscribed/book/fresh symbol counts, seconds since last update, silent-poll
count, failure counters and whether the browser holds the screen.

### To finish Phase A

Start LM Studio, then A4 (`list_outstanding_orders` → expect 0), A5 (cancel a bogus order number →
expect a clean refusal), A6 (`get_portfolio`, which opens the browser → expect the re-subscribe line
afterwards) and the MARI holdings check. A6 is also covered naturally by B4, since placing an order
opens the trading screen — which is the real scenario anyway.

---

## Watchlist sync — verified live, 2026-08-18 07:18 PKT

The feed tracks runtime watchlist changes. `AhkFeedWorker` calls
`MonitoredUniverse.ForMonitoringAsync` on **every** poll and re-subscribes the moment the resolved
set differs from what it last sent, so a change reaches the portal within one poll (~2s). All four
watchlist mutation endpoints call `universe.Invalidate()`, and even without that the universe cache
is only 30 seconds — so a missed invalidation degrades to a 30-second delay, never to a permanently
stale subscription.

Measured against the live portal:

```
                                              subscribed  book  fresh
baseline                                          30       30     30
POST /watchlist {"symbol":"SEARL"}   (+6s)        31       31     31
DELETE /watchlist/SEARL              (+6s)        30       30     30
   log: Subscribed 30 symbol(s) across 4 page(s); evicted 1 unwatched symbol(s) from the book
```

A **removal** needed a fix. The portal stops sending an unsubscribed symbol, but the quote book never
expired entries within a session, so its last quote stayed servable for the whole freshness window
(`MaxQuoteAgeSeconds`, default 10 minutes) and kept inflating the fresh-symbol count operators use to
judge feed health. `AhkQuoteBook.RetainOnly` now evicts on every subscription change; covered by two
tests.

Note that `ForMonitoringAsync` is the union of the watchlist **and** `AllowedSymbols`, so removing a
configured tradable symbol from the watchlist does not unsubscribe it. That is existing, deliberate
behaviour, not a feed concern.

## Outstanding: A4 / A5 / A6

Still blocked on the LLM. `172.17.80.1:1234` times out from both this shell and the host process, and
there is **no listener on port 1234 anywhere on the machine** — `netstat` shows nothing, no containers
are running, and the address does not answer ping. LM Studio's local server needs to be started (the
desktop app can be open with the server off), and the address it reports should be the one configured.

Note also that `appsettings.user.json` pins `LLM.BaseUrl` to `http://localhost:1234/v1` and
`LLM.Model` to the placeholder `"any model loaded in LM Studio"`, and that file **overrides**
`appsettings.json`. Either fix it there or pass `LLM__BaseUrl` / `LLM__Model` as environment
variables.

The substance of A5 is now covered without the agent: the cancel selection rules were extracted to
`CancelTargetResolver` and tested directly — ambiguous symbol refused with candidates named, side
filter disambiguating BUY vs the portal's `SEL`, unknown order number refused while listing what
actually exists, empty book, and exact order number winning over an ambiguous symbol. What remains
untested is the agent wiring around them, not the safety logic.

---

## Phase A COMPLETE — 2026-08-18 07:44–07:47 PKT

LM Studio was reachable at `http://192.168.100.50:1234/v1` (not the earlier addresses), model
`qwen2.5-14b-instruct`. Every Phase A check passed.

| Check | Result | Evidence |
| --- | --- | --- |
| A1 session from plain HttpClient | **PASS** | `Direct API session established for account CC45698` |
| A2 subscription | **PASS** | `Subscribed 30 symbol(s) across 4 page(s)` |
| A3 feed responds | **PASS** | 30 subscribed / 30 fresh, `marketStatus: OHO`, updating every poll |
| A4 order book read | **PASS** | tool returned `count: 0` in **46 ms** |
| A5 cancel refuses cleanly | **PASS** | `"The account has no working orders. Nothing to cancel."` in 44 ms, no exception |
| A6 subscription-clobber fix | **PASS** | see below |
| Quotes reach consumers | **PASS** | live bar for today via `/api/trading/candles` |
| Watchlist add/remove sync | **PASS** | 30↔31 within ~6s, with book eviction |

A6 produced exactly the sequence the fix was written for:

```
07:46:48 [AhkBroker] Portfolio read: balance=78141.00 holdings=10 warnings=0
07:46:49 [AhkFeed] Re-subscribing because the browser released the trading screen
                   and its page load will have overwritten the subscription.
07:46:49 [AhkFeed] Subscribed 30 symbol(s) across 4 page(s).
```

Verify tool results from the **audit trail**, not the model's prose:
`bin/.../sessions/specialist/trading-agent/*.md` records every `[tool_call]` and `[tool_result]`
verbatim. In A4 the model narrated its answer in Thai and read like a hallucination, but the audit
record showed the tool genuinely ran and returned `count: 0`. Prose is not evidence.

### A fifth defect, found here

The two new tools were registered by the plugin but **not exposed to the specialist agent**.
`BuildSpecialistToolNames` is a hand-maintained allow-list and I had not added them, so the agent
answered "I don't have that tool" while the plugin reported 15 registered. Added — with the note that
an agent able to place an order but not cancel one is the wrong half of the pair to expose.

### Account state for Phase B (read 07:46 PKT)

Cash **78,141.00 PKR**; 10 holdings; **MARI: 75 shares** @ avg 646.12, last 679.56, P/L +2,508.

Both legs are therefore viable, and both worst cases are benign:
- **BUY 10 MARI @ 650** = 6,500 PKR — well inside cash and the 50,000 order cap. If it filled, it
  would buy below the last traded price.
- **SELL 10 MARI @ 710** — covered by the 75 held, so it will rest rather than being rejected. If it
  filled, it would sell 10 of 75 at a profit against the 646.12 average.

---

## Order-cancel round-trip: attempted 07:58 PKT, BLOCKED until the open

Asked to run a place→cancel test pre-open. It cannot be done, and the reason is worth recording.

`place_order` (BUY MARI 10 @ 650 LIMIT, confidence HIGH) returned:

```json
{"skipped": true, "reason": "PSX regular market is closed. Next session opens at 09:32 PKT."}
```

The market-calendar gate refuses before anything reaches the broker, so no order rests and the cancel
path cannot be exercised. This is the correct behaviour and a *better* guard than the portal's own,
which returns HTTP 200 with a green success alert while placing nothing. The full round-trip is now
the first item in `phase-b-runbook.md`.

### The attempt found a sixth defect — in the operator procedure, not the code

Given the explicit instruction *"quantity=10"*, `qwen2.5-14b-instruct` **omitted `quantity` entirely**.
`place_order` fell back to auto-sizing from `PerStockBudgetPkr` and tried to place **75 shares for
48,750 PKR** — 7.5× the intended order. It was caught only because a temporary
`Plugins__Ahk__MaxOrderValuePkr=7500` guardrail had been set for the run:

```
Order value 48,750 PKR exceeds limit of 7,500 PKR.
```

A second attempt with the requirement restated did pass `quantity: 10` correctly. The lesson is not
"prompt harder" — it is that **the model cannot be trusted to pass exact order parameters**, so any
live order test must constrain both the explicit-quantity path (`MaxOrderValuePkr`) and the auto-size
path (`PerStockBudgetPkr`), and must verify the actual `[tool_call]` arguments in the audit trail
after every placement. Both are mandatory steps in the runbook.

Worth considering separately: `place_order` silently auto-sizing to 75 shares when `quantity` is
omitted is generous behaviour for a caller that may be a small local model. Requiring an explicit
quantity above some value, or logging loudly when auto-sizing produces an order many times the
requested size, would be a cheap safety improvement.

---

## The market-calendar gate was wrong, and OHO proves it — 2026-08-18 08:18 PKT

Challenged on why an order could not be placed if the broker accepts it. The challenge was correct.

### Evidence that OHO accepts orders

The portal's own `site.js` renders market states like this:

```js
if (state == "OHO")        result = '<lable class="text-success">OHO</lable>';    // GREEN
else if (state == "Close") result = '<lable style="color:#ff0000">Closed</lable>';
else if (state == "OPN")   result = '<lable class="text-success">Open</lable>';   // GREEN
else if (state == "CLO")   result = '<lable style="color:#ff0000">Closed</lable>';
```

OHO is styled as a success state alongside Open, and **nothing in `site.js` disables the order form
based on market state**. Then confirmed empirically: an order placed during OHO was accepted and
**verified in the account's own outstanding log**.

### Why the gate existed, and what was wrong with it

`if (!market.IsOpen) reject` arrived in the initial commit with **no comment and no recorded
rationale**, and the README simply repeats "market open" as one of the risk-engine checks. So there
was no stated reason to weigh — only an inherited assumption.

The defensible purpose is real, though: this portal returns HTTP 200 with a green success alert while
placing **nothing** when the market is genuinely shut (`AhkConfig.VerifyOrderInBook`), so submitting
into a closed market loses orders silently. The gate should not be deleted.

The mistake was conflating two different states: *genuinely shut* versus *accepting orders into the
queue but not yet matching*. OHO is the second kind, and blocking it forfeited queue priority at the
open — exactly when an overnight signal wants to act.

### The fix

`Market/OrderWindow.cs` decides whether the **venue** is accepting orders:

1. Prefer the broker's own reported market state (`GetFeed.marketStatus`), which knows about halts,
   extended sessions and unscheduled closures in a way a hardcoded 09:32–15:30 schedule cannot.
2. Allow the states in `Ahk.OrderAcceptingMarketStates` — empty means the defaults `OPEN`/`OPN`/`OHO`
   (empty rather than pre-populated, because ConfigurationBinder appends; same trap as `AhkFeed.Pages`).
3. Fall back to the calendar when the broker has reported nothing — feed disabled, or not yet polled.
4. `Ahk.TrustBrokerMarketState = false` restores calendar-only gating.

The execution audit record now stores which authority allowed the order (`broker` / `calendar`) and
what it said, so "why was this accepted at 09:05" is answerable afterwards. Covered by
`OrderWindowTests` — including the direction that protects money: a broker-reported halt blocks the
order even while the local calendar thinks the market is open.

### The live round-trip — PLACEMENT confirmed, CANCELLATION still unverified

```
place_order BUY MARI 10 @ 650  ->  order_id 6427, verified in the outstanding log   [CONFIRMED]
list_outstanding_orders        ->  6427 resting, plus 6298 SEL SELECT (also a test order)
cancel_order order_no=6427     ->  cancelled:false verified:false  (still there at 8s)
list_outstanding_orders x2     ->  count: 0                        [NOT what it looked like]
```

**Order PLACEMENT during OHO is confirmed.** The order was accepted and verified in the account's own
outstanding log. That result stands.

**Order CANCELLATION is NOT confirmed, and the earlier conclusion here was wrong.** The `count: 0`
reads were first interpreted as "the cancel took effect, just slower than the 8s window". They were
not. The broker **blocked account access** at around that moment, and `GetOutstanding` began
returning an empty array because the read was failing — not because the book was empty. Whether
order 6427 was ever cancelled is unknown from this evidence.

The cancel flow therefore remains **untested end to end** and is still the first item in
`phase-b-runbook.md`.

### The misreading exposed a real defect, now fixed

`GetOutstandingAsync` returned an empty list both for "no working orders" and for "the read failed" —
session expired, redirect to login, HTTP error, access withdrawn. Everything downstream believed the
former. Consequences:

- `WaitUntilGoneAsync` saw an empty book and returned true, so `cancel_order` would report
  **`cancelled: true, verified: true`** for an order that might still be live. Under exactly the
  access block that occurred, the tool would have falsely confirmed a cancellation. This is the
  precise failure the verify-against-the-book design exists to prevent, defeated by an ambiguous
  return value one layer down.
- `list_outstanding_orders` would report `count: 0`, telling an operator the account is flat when it
  may not be — inviting a duplicate order or an abandoned live one.

Fixed with `OrderBookRead(Ok, Orders, Error)`:

- a failed read is never "gone" — `WaitUntilGoneAsync` keeps polling and never concludes from it;
- `cancel_order` refuses outright when the book cannot be read, cancelling nothing;
- `list_outstanding_orders` reports the failure and explicitly instructs against saying "no orders".

Covered by tests. The general lesson is one this codebase already states elsewhere and this violated:
**an unavailable answer must not be encoded as an empty one.**

### Account access blocked by the broker

The broker blocked account access during the test. Cause not yet established — the operator is
checking. Rate limiting is the leading hypothesis, and this session's usage is a plausible trigger:

- **15 logins** in roughly two hours. Each host restart performs a full browser login, and the host
  was restarted ~10 times while iterating on config and fixes. This is the most likely cause and the
  easiest to avoid.
- The 2s feed poll matches the portal's own client cadence (1–2s), so it is unlikely to be the
  trigger on its own — but it ran alongside everything else.
- Order placement and cancellation were a handful of calls; not plausibly a factor.

**Mitigations to apply before resuming** (see the runbook): do not restart the host repeatedly against
a live account; keep one session and reuse it. The session profile persists in `session_ahk`, so a
restart usually skips the full login — but a deleted profile or a changed credential forces a fresh
one each time. Consider a minimum interval between login attempts in `AhkBroker`.

**All access to the broker was stopped once the block was reported**, and verified stopped: no
AgentFox process, and no Chrome running against the `session_ahk` profile.

---

## CANCEL FLOW CONFIRMED — 2026-08-18 08:41 PKT (pre-open, OHO)

Access restored. Re-ran with the feed **disabled** so the only broker traffic was the cancel path
itself: **1 login + 4 tool calls ≈ 7 requests total.**

```
list_outstanding_orders   -> count 2:  6298 SEL SELECT 32.50 x50,  6427 BUY MARI 650.00 x10
cancel_order 6427         -> cancelled:true verified:true   (2.16s)
cancel_order 6298         -> cancelled:true verified:true   (2.13s)
list_outstanding_orders   -> count 0   (success:true — a genuine read, not a failed one)
```

Log evidence:
```
[CancelOrder] Cancelling #6427 BUY 10 MARI @ 650.00  ->  Confirmed: order 6427 left the book.
[CancelOrder] Cancelling #6298 SEL 50 SELECT @ 32.50 ->  Confirmed: order 6298 left the book.
```

**The whole flow now has live confirmation:** place (order 6427, verified in the outstanding log),
read, cancel, and verify — including a second order the agent had never placed itself.

### What the earlier "failure" actually was

Both orders were still resting when access came back, which proves **6427 was never cancelled** by the
earlier attempt. The `cancelled:false verified:false` and the subsequent `count: 0` were entirely the
access block. Two corrections to what was written before:

- Cancellation is **not** slow. It completes in about **2 seconds**, one verification poll. The
  earlier 8s "timeout" was the block, not latency. The 30s default is now generous headroom rather
  than a necessity, and is left as-is.
- `count: 0` never meant the book was empty. It meant the read was failing — see below.

### A third defect, found by this run

The first attempt after restore failed with *"No broker session is established"*. `GetOutstandingAsync`
read `AccountCode` — which is populated from the login cookies — **before** anything established a
session. It had only ever worked because the feed worker happened to run first and populate it; with
the feed off, the order tools could not work at all. Now the session is established first.

Worth noting how this surfaced: the tool **failed loudly** instead of reporting `count: 0`. Before the
`OrderBookRead` fix, this exact condition would have said "the account has no working orders" — with
two live orders resting.

### Confirmed traffic profile for the cancel path

One login, one book read, one POST per cancel, one verification poll per cancel (2s interval), one
final read. Nothing polls in the background when `AhkFeed:Enabled=false`. This is the shape to use
for order operations against a rate-sensitive account.
