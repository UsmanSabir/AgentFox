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
