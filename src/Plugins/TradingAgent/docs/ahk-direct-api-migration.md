# Replacing the browser automation with the portal's JSON API

**Status: steps 2 and 3 are implemented and step 1 is deliberately deferred.** Read paths for the
portfolio and for broker reconciliation now go over the JSON API; order placement and login remain on
the browser, and always will under this plan.

Everything below was re-verified against the live portal on 2026-08-18 during an open session
(`marketStatus: "Open"`, all three markets `OPN`). Several claims in the original version of this
document turned out to be wrong; they are corrected in place and called out under
"What the live capture changed".

The live pricing work (see `ahk-feed-api.md`) established that the AHK portal is a plain JSON API
behind a session cookie, and built `AhkPortalClient` to talk to it. Read-only pricing and order
cancellation now go that way. Everything else in `AhkBroker` — 2,700 lines of PuppeteerSharp driving
a real Chromium — is still clicking through the DOM to reach endpoints that would answer directly.

## What the browser currently does, and what could replace it

| Today (`AhkBroker`) | Direct endpoint | Notes |
| --- | --- | --- |
| Fill `#buysymbol`/`#buyvolume`/`#buyprice`/`#buyPIN`, click BUY, confirm a `swal` prompt | `POST /Home/PlaceOrder` | The high-risk one. See the warning below. |
| Read the Outstanding Log tab to verify an order exists | `GET /Home/GetOutstanding` | **Already migrated** — used by `list_outstanding_orders` and `cancel_order`. |
| Read the Activity Log tab | `GET /Home/GetActivityLog`, `GET /Home/GetTradeLog`, `GET /Home/GetOrderHisotry` | **Migrated** for reconciliation — `AhkBrowserBrokerAdapter.ReadSnapshotAsync`. |
| Open the Exposure modal, pick the account, flip tabs, scrape `#collateralstable` and `#exposuretable1` | `GET /Home/GetCollaterals` + `GET /Home/GetAccountBalance` | **Migrated** — `PortfolioReader`. Note the endpoints: **not** `GetExposureData` / `GetJSPorfolioDetails`, for the reasons below. |
| Read the day's band off the order dialog, one symbol at a time (`ClampPriceToBand`) | `GET /Home/GetUpperLowerCap` | Returns `[{symbol, market, upperCap, lowerLock}]` for the **whole market in one call**. `AhkPortalClient.GetPriceBandsAsync` already implements it; nothing consumes it yet. |
| — | `GET /Home/PingPong` | Not a keepalive. A live order-event stream: `{time, type, message}` with `type` in `BUYQ`/`SELLQ`/`BUYC`/`SELLC`, terminated by `message == "ENDUP"`. |

Endpoint spellings are the portal's own and several are misspelled — `GetSymolsList`,
`GetOrderHisotry`, `GetJSPorfolioDetails`, `SetWithdrawlRequest`, and the `orignalorderno` form field.
Correcting them silently no-ops.

## What the live capture changed

The 2026-08-18 capture contradicted four things this document previously asserted. They are recorded
because each of them would have cost a debugging session to rediscover.

**`GetExposureData` is not JSON.** It returns three pre-rendered HTML `<table>` fragments joined by
`'|'`, which the portal injects with `innerHTML`. It carries cash and collateral TOTALS only — Net
Cash, Ledger Balance, Collaterals, After Haircut, Total NetWorth — and no per-symbol rows at all.
Migrating to it would have replaced a DOM scrape with a different DOM scrape.

**`GetJSPorfolioDetails` is not the holdings endpoint.** It returned `[]` against an account holding
ten positions. It is the intraday trading view (`buyQty` / `sellQty` / `pendingBuy` / `pendingSell`),
empty when nothing traded that day. Reading holdings from it would have reported a fully-invested
account as flat — silently, and most often before the first trade of the day.

**The holdings endpoint is `GET /Home/GetCollaterals?account=…`, which this document did not list.**
Neither did `GetOpenPosition` or `GetClientInfo`. The `/Home/` surface here was taken from a partial
scan of `site.js`; the complete set of `url:` literals is larger. `GetCollaterals` returns clean JSON,
one row per position:

```json
{"symbol":"MARI","market":"REG","quantityTotal":75,"avgRateBuy":646.12,"mtmPrice":675.0,
 "amount":50625.0,"unsettled":2166.0,"plSettled":0.0,"sold":0,"pendingSell":0,
 "haircutPer":15.0,"margVal":573.75,"avgRateSell":0.0}
```

`amount` is exactly `mtmPrice × quantityTotal` and `unsettled` is exactly
`(mtmPrice − avgRateBuy) × quantityTotal` — verified on every row of a live capture, so both are taken
as reported rather than recomputed. **But they arrive as binary floats**: one row really did return
`26215.000000000004`. Carried into a decimal, that noise survives every later sum and renders an
account total as `324972.00000000001`, so `PortfolioReader` rounds money to paisa at the mapping
boundary.

**`GetAccountBalance` returns a JSON string, not a number** — `"78141.0"` — which is why the portal's
own UI wraps it in `Number()`. It also takes `?account=`, and its value is the available cash.

One further correction, to `phase-b-runbook.md` §4: the open-market value of `marketStatus` is
`"Open"`, not `"OPEN"`. Nothing is broken by this — every comparison in the codebase is
`OrdinalIgnoreCase` — but a runbook step that greps for the literal `OPEN` will not match.

## Why this is worth doing

- **Latency.** A portfolio read is currently a browser launch, a login, a modal, a tab dance and a
  table scrape — seconds, and it takes the broker's single-page gate the whole time. The equivalent
  JSON call is milliseconds and takes no gate.
- **`IBrokerStateReader` currently returns `Unsupported`.** `AhkBrowserBrokerAdapter.ReadSnapshotAsync`
  says outright that there is "no reliable supported API for fills, positions, and balances", which
  is why `BrokerReconciliationWorker` has nothing to reconcile against. `GetOutstanding` +
  `GetActivityLog` + `GetAccountBalance` are exactly that API. This is probably the single highest
  value item here.
- **Fragility.** Every selector in `AhkConfig` — and there are dozens — is a bet on the portal's DOM.
  The JSON field names are a smaller and more stable surface.
- **The feed contention disappears.** `AhkFeedWorker` currently has to yield whenever the browser
  holds the trading screen, and re-subscribe afterwards because the portal's page load clobbers the
  subscription. With no browser on the trading screen, neither problem exists.

## Why order placement is the last thing to move, not the first

`AhkConfig.VerifyOrderInBook` documents something learned the hard way against the live portal: an
off-hours submission returns **HTTP 200 with an empty body and a green success alert while placing
nothing at all**, and even the happy path returns no order number. The portal's success signalling
cannot be trusted, which is why the current code verifies every placement against the account's own
order book.

`POST /Home/PlaceOrder`'s **request** shape is now known, read off `site.js` on 2026-08-19 (see
"The PlaceOrder request shape" below). Its **response** is still unobserved: no capture exists of what
an accepted, rejected or off-hours submission actually returns. Migrating placement therefore means
re-establishing, against real money, all of the confirmation semantics that took real incidents to
learn on the browser path. The order-book verification pattern would carry over intact and would still
be the actual evidence, but the last mile has to be driven by a capture of a real submission — reading
`site.js` gives the request, never the response.

## The PlaceOrder request shape

Derived from `GET /js/site.js` (255 KB, **served unauthenticated**), functions `placeBuyOrder` at
line 2671 and `placeSellOrder` at line 2746. No order was submitted to obtain this.

```
POST /Home/PlaceOrder
Content-Type: application/x-www-form-urlencoded    (jQuery $.ajax default; no JSON variant)
No anti-forgery token — the portal sends none, exactly as with CancelOrder.

Account     account CODE, e.g. "CC45698"   (option TEXT of #buyaccount / #sellaccount)
BuySell     "BUY" | "SEL" | "SHS"          (see the asymmetry note)
Market      "REG" | "FUT" | "ODL" | "SIF" | "SQR" | "FSR"
OrderType   see the encoding hazard below
Volume      shares
Script      symbol
Exchange    "KSE"                          (hardcoded in site.js, never varies)
Price       limit price
PIN         trading PIN
LimitPrice  #buylimitprice / #selllimitprice, the stop-limit trigger; "" when unused
```

Everything else the dialog collects is **not sent**. `placeBuyOrder` and `placeSellOrder` both compute
a `tradertype` local from `#buytradetype` / `#selltradetype` and then never put it in the payload — so
the trade type the current DOM path selects (`SelectByVisibleTextAsync("#selltradetype", "SEL")`) never
reaches the server at all. Whatever distinguishes an LB sell from a plain one is therefore *not* that
select; `LBSellOrder` sets `selltradetype` index 2 and disables it, which is cosmetic. That gap must be
resolved before any LB/SHS order goes over the API.

**Three hazards, all of them in the encoding rather than the transport:**

1. **`BuySell` is asymmetric.** BUY sends the literal `"BUY"`; SELL sends the `BuySellType` global,
   which is `"SEL"` for a normal or LB sell and `"SHS"` for a short sell. Not `"SELL"`.
2. **`OrderType` is read differently on the two sides.** BUY sends
   `options[selectedIndex].value`; SELL sends `options[selectedIndex].text`. The current DOM path picks
   by visible text — "Limit" / "Market" / "Stop Loss" — so the SELL strings are known, but the BUY
   option *values* are in the authenticated `/Home/Index` markup and have not been read. Sending the
   text where the server expects a value is the sort of mistake that produces a 200 and no order.
3. **The price-band check is client-side only.** `placeBuyOrder` compares `Price` against the
   `upperCap` / `lowerCap` globals and refuses locally; nothing in the request carries the band. An API
   caller inherits full responsibility for it — which is what makes `GetUpperLowerCap`
   (`GetPriceBandsAsync`, already implemented and unused) a prerequisite of this step rather than the
   optional tidy-up it looks like in step 1.

**What the response is used for tells us how much to trust it.** On SELL the handler is
`sweetAlert(res, "success")` — the response body IS the message the portal shows the trader. On BUY the
same line is commented out and replaced with a hardcoded `"Your buy order has been sent."`. So the
"green success alert while placing nothing at all" recorded in `AhkConfig.VerifyOrderInBook` is the BUY
path *by construction*: it cannot report a failure, because it never looks at the answer. This cuts in
favour of the migration — a direct caller reads a body the UI throws away — but it also means the
browser path has never observed that body, so we have no idea yet what an accept looks like versus a
reject. Order-book verification stays mandatory either way.

**Order of work**, each independently shippable and reversible:

1. `GetUpperLowerCap` → replace the per-order dialog band scrape. **Deferred, deliberately.**
   `GetPriceBandsAsync` exists and works, but `ReadPriceBandAsync` runs against an already-open order
   dialog on the placement path — so replacing it removes a DOM read without taking the browser off
   any read path, which is what this work is for. It also touches the code that decides the price of a
   live order, and that is not a change to make for tidiness. Worth doing when placement itself moves.
2. `GetCollaterals` + `GetAccountBalance` → replace `GetPortfolioAsync`. **Done** — `PortfolioReader`,
   with the browser scrape retained as an automatic fallback and `Ahk.PreferDirectApiForPortfolio`
   to force the old path for cross-checking.
3. `GetActivityLog` / `GetTradeLog` / `GetOutstanding` / `GetCollaterals` → give `IBrokerStateReader` a
   real implementation. **Done** — `AhkBrowserBrokerAdapter.ReadSnapshotAsync`. See the warning below.
4. `PingPong` → a live order-event stream, which would let order confirmation stop polling. Not started.
5. `PlaceOrder` / order modification — **only** with captured evidence from real submissions, and
   only with order-book verification retained. Note that the captured `/Home/` surface contains **no
   modify or amend endpoint at all** — only `PlaceOrder` and `CancelOrder` — so "order modification"
   is cancel-and-replace unless something unlisted exists.

Steps 1–4 are read-only and carry no execution risk. Step 5 is a different category of change and
deserves its own decision.

## PlaceOrder, captured live

Captured 2026-08-19 against account CC45698 with the market open, by
`AhkLiveCaptureTests.Place_Capture_And_Cancel_Orders` — one real 1-share SEL of SYS priced at the day's
upper cap so it would rest rather than fill, verified in the book, then cancelled.

```
POST /Home/PlaceOrder
Account=CC45698&BuySell=SEL&Market=REG&OrderType=Limit&Volume=1&Script=SYS&Exchange=KSE
&Price=141.65&PIN=****&LimitPrice=

200 OK · application/json; charset=utf-8 · 69ms · 38 bytes
"Order has been sent to Trade Server."
```

```
POST /Home/CancelOrder    orignalorderno=0010TJZJC700P8A6
200 OK · application/json; charset=utf-8 · 48 bytes
"Order cancelation request sent to Trade Server"
```

**The response is a transmission receipt, not an acceptance.** "has been *sent* to Trade Server" is
literally true and says nothing about whether the trade server took the order — which is precisely the
off-hours behaviour recorded in `AhkConfig.VerifyOrderInBook`, seen from the other side. A direct client
must therefore treat a 200 with this body as "submitted, outcome unknown" and go to the book, exactly as
the DOM path does. What it must NOT do is what `site.js` does on the buy side, which is assume success.

**But the API sees more than the browser ever did.** Within three seconds of the POST:

- `GET /Home/GetOutstanding` carried the order with its number —
  `{"scrip":"SYS","price":141.65,"remaining":1,"orderNo":"0010TJZJC700P8A6","hOrderNo":"0611XK1","type":"SEL"}`
- `GET /Home/GetActivityLog` carried `"action":"QUE"` for the same order number.

So the claim in this document that "the happy path returns no order number either" is true of the *popup*
and false of the *account*: the order number is available immediately, from the book, on the direct path.
`action` is the accept signal worth reading — `QUE` is queued at the exchange.

**The BUY side and a rejection, captured 2026-08-19 in the same harness.**

```
POST /Home/PlaceOrder    (ACCEPTED — 1 share BUY at the lower lock)
Account=CC45698&BuySell=BUY&Market=REG&OrderType=Limit&Volume=1&Script=SYS&Exchange=KSE
&Price=115.89&PIN=****&LimitPrice=
200 OK · application/json · 38 bytes · "Order has been sent to Trade Server."
→ GetOutstanding: orderNo 0010TJZJC700RH12, type BUY, price 115.89
→ GetActivityLog: action "QUE"

POST /Home/PlaceOrder    (REJECTED — SEL priced 200.00 against an upper cap of 141.65)
200 OK · application/json · 38 bytes · "Order has been sent to Trade Server."
→ GetOutstanding: absent
→ GetActivityLog: orderNo 0010TJZJC700RH7H, action "REJ", price 0.0, fillVolume 1, totalValue 0.0
```

Three conclusions, and they settle the design:

1. **The response cannot distinguish accepted from rejected.** Byte-identical, 200, same 38-byte body,
   for an order that queued and an order the exchange refused outright. The response is a transmission
   receipt and nothing more. There is no version of "read the response" that works here.
2. **`OrderType` takes the option TEXT on both sides.** `Limit` was accepted on the BUY, so for this
   option the `.value` `site.js` sends equals its text. The asymmetry in the portal's code is real but
   harmless for Limit; **Market and Stop Loss remain unverified**, and a mismatch there would present as
   a 200 with no order, so verify each before using it.
3. **The verdict lives in `GetActivityLog.action`.** Confirmed codes: `QUE` queued, `REJ` rejected,
   `CLX` cancelled. A rejected order still gets an order number, so "absent from the outstanding book"
   alone cannot be read as "never submitted" — the order number plus its action is the only complete
   answer, and it is available within about three seconds.

**A live bug this capture exposed.** The REJ row arrived with `fillVolume: 1` — a full quantity — while
`price` and `totalValue` were 0. `AhkBrowserBrokerAdapter.ReadSnapshotAsync` selected fills with
`a.FillVolume is > 0`, so a rejected order was counted as a completed fill: reconciliation would report a
position the account does not hold, and the protective-stop path would then set about protecting it. The
guard now also requires `Price is > 0` and excludes `REJ` / `CLX` by name, while still refusing to
classify an unknown action code by guesswork. `fillVolume` is not a fill indicator on this endpoint.

**Stop orders, captured the same day, and the response is NOT always the same.**

```
OrderType=Stop Loss   (the label the portal shows its own users, and what site.js sends on SELL)
200 OK · application/json · 2 bytes · ""
→ nothing in the order book, and NO activity row at all. Silently discarded.

OrderType=StopLoss    (the underlying option VALUE)
Account=CC45698&BuySell=SEL&Market=REG&OrderType=StopLoss&Volume=1&Script=SYS
&Exchange=KSE&Price=118.00&PIN=****&LimitPrice=116.00
200 OK · 38 bytes · "Order has been sent to Trade Server."
→ GetOutstanding: orderNo 0611XK66, price 116.00 (the LIMIT, not the trigger), type SEL
→ GetActivityLog: action "APT"
```

This adds the one distinction the earlier captures missed: **an empty body means the endpoint refused the
request outright** — no order, no order number, no activity row. So the response does carry exactly one
bit of real information, and it is not the one the portal's UI reads:

| response body | meaning |
| --- | --- |
| empty (`""`) | refused because of a FIELD it would not accept — `OrderType` is the usual one. The only refusal worth retrying through the browser, whose dialog builds the request from the portal's own selects. |
| anything else that is not the acknowledgement, e.g. `"Market is closed\r\n"` | refused, with the portal's own reason. Surface it verbatim; retrying through the browser gets the same answer. |
| `"Order has been sent to Trade Server."` | transmitted. Says nothing about the outcome — read `GetActivityLog.action`. |

The acknowledgement is **whitelisted** rather than the refusals blacklisted, and that is deliberate: the
set of things this endpoint says when it refuses is open-ended and was learned one surprise at a time.
Treating "not a known refusal" as submitted would turn every future refusal message into a phantom order
in the ledger.


Which retires the "the response is useless" conclusion above in one direction only: it can prove
*nothing was placed*, and it can never prove anything was. `AhkOrderTypes` exists so no caller has to
rediscover that `"Stop Loss"` is discarded and `"StopLoss"` is not.

Note also that the stop order's book row shows the **limit** price (116.00), not the trigger (118.00), and
that its `orderNo` is the short house-style number (`0611XK66`) rather than the long form seen on ordinary
limit orders — so any code matching order numbers must not assume a format. `APT` joins the confirmed
action codes: `QUE` queued, `APT` accepted (a stop awaiting its trigger), `REJ` rejected, `CLX` cancelled.

**Price bands are republished for the next session before the current day is over.** SYS read
`upperCap 141.65` at midday and `136.79` after the close on the same date — the second is the NEXT
session's band, computed off the day's close. So a band fetched before the bell and used after it is the
wrong band, and the fetch belongs inside the order pass rather than cached across one.

**Day orders are cancelled at the close, and the book empties with them.** The stop order above was
placed while the market was open and was cancelled by the closing bell, not purged mid-session; minutes
later `GetOutstanding` returned `[]`, taking with it a genuine protective sell that had rested since 10:00.
PSX orders are day orders. Two consequences: order-book verification only means anything during a session,
and every protective stop this system relies on has to be re-placed each trading day — an overnight stop
does not exist, whatever the ledger remembers about placing it.

**Still uncaptured:** `Market` orders on either side — deliberately deferred, and `AhkOrderTypes.MarketUnverified`
is named to say so at the call site. Also a rejection for a reason other than price (insufficient funds, an
unshortable symbol); different reasons may or may not produce different action codes.

Two smaller findings from the same session, both worth knowing before writing the client:

- `GetUpperLowerCap` covers **REG 565, SQR 565, FUT 308, ODL 9** symbols — 1,447 rows. A symbol missing
  from it cannot be priced, and `site.js`'s `getSymbolWiseUpperLoweCap` **returns early without clearing
  its `upperCap` / `lowerCap` globals on a miss**, so the portal's own dialog silently validates against
  the previously-viewed symbol's band. Never infer a band from a near match.
- `GetFeed` is **not** a drain-once queue. Consecutive polls returned the same subscribed symbols with
  refreshed prices, so two pollers on one session do not split the stream. That retires the premise
  behind `AhkBroker.BrowserHoldsTradingScreen` and `AhkFeedWorker`'s yield-to-the-browser branch; the
  `Page1` clobber on page load is the real mechanism, and it is a *subscription* problem, not contention.
- `SendSubscriptionofSymbols` answers **200 with a zero-length body** on success, so
  `AhkPortalClient.SubscribeAsync`'s `body is not null` cannot distinguish accepted from ignored. With
  valid symbols it does work — OGDC, PPL and MARI subscribed singly and as a batch all produced quotes
  within one poll.

## How the last unknown gets captured

`Ahk.CaptureOrderApiTraffic` (default **on**) records the raw request and response of the portal's own
`POST /Home/PlaceOrder` and `POST /Home/CancelOrder` to `{LogDir}/order_api_capture.log`, and dumps the
order dialog's select options once per browser session. It observes only — placement still goes through
the DOM exactly as before.

The point is that the response semantics cannot be obtained any other way. `site.js` discards the body
on the buy side, so the browser path has never seen it; reading `site.js` gives the request and nothing
else. The alternative to capturing is submitting a test order with real money purely to watch what
comes back, which is a worse trade than waiting for the next order the agent was going to place anyway.

**What to do with the output.** After one real BUY and one real SELL have gone through, the log holds:

- the exact `PlaceOrder` payload the portal sends, PIN redacted — including the `OrderType` value the
  BUY side actually transmits, which resolves the value-vs-text hazard above from evidence;
- the response status, content type, body length and body, for a submission whose outcome is
  independently known from the order-book verification in the same log.

That is the complete input to `AhkPortalClient.PlaceOrderAsync`. Until both sides are captured, do not
write it: the one thing worse than a DOM placement path is a direct one that misreads "accepted".

## The browser must not idle on the trading screen

Found on 2026-08-19, and it is the strongest practical argument for finishing this migration.

`GetSessionCookiesAsync` deliberately leaves the browser alive after harvesting cookies, so with the
feed enabled the post-login page — `/Home/Index`, the trading terminal — stayed loaded for the entire
run. That page's own `site.js` keeps polling `GetFeed` on a 1–2s timer and re-subscribes an empty
`Page1` on every load, so an idle window competes with `AhkFeedWorker` for the same server-side
session. `AhkBroker.BrowserHoldsTradingScreen` cannot see it: that flag counts our own in-flight
operations by design (see its remarks), and a window merely sitting there is not one. The symptom was
"the feed returned nothing for 30 consecutive polls" repeating indefinitely against an open market,
with a visibly empty Market Watch in the portal — the empty `Page1` subscription, on screen.

Two fixes, both shipped:

- **`Ahk.ParkPageAfterCookieHarvest`** (default on) navigates the page to `about:blank` after the
  harvest, keeping the warm session — no relaunch, no second login — while removing the competing
  poller. It also makes the existing comment on `GetSessionCookiesAsync` true: harvesting really does
  not leave a page on the trading screen any more.
- **`AhkPortalClient.SessionEpoch`**, checked by `AhkFeedWorker` on every pass. Subscriptions live on
  the session, so a re-harvested session starts with none — while `_subscribed` still names the
  previous session's symbols, which made the worker skip re-subscribing as "unchanged" and left the
  silence watchdog (thirty quiet polls) as the only recovery. The epoch turns that into a re-subscribe
  on the next pass.

Neither is a substitute for moving placement off the browser. With placement on the API the browser is
needed for login alone: launch, log in, harvest, close — and no page ever reaches the trading screen,
so the contention and the `Page1` clobber stop being things that have to be mitigated at all.

## Two hazards that came out of implementing steps 2 and 3

**A periodic reader must never establish a session.** `AhkPortalClient.EnsureSessionAsync` harvests
cookies from the browser broker, and that harvest calls `PrepareSessionWithRetryAsync`, which performs
a full portal **login** when no session is live. `BrokerReconciliationWorker` runs on a 60-second
timer. A reconciliation pass that established its own session would therefore turn a dead session into
sixty logins an hour — against a broker that withdrew access after roughly fifteen in two hours
(`phase-b-runbook.md` §0). `ReadSnapshotAsync` checks `AhkPortalClient.HasSession` before every read
and reports "no session" rather than creating one; the feed worker or a user-initiated read is what
establishes it. Anything else added to a timer must follow the same rule.

**Step 3 unblocks order modes that have never actually run.** `TradingManager` rejects orders in
`ApprovalRequired` and `BoundedAuto` modes whenever reconciliation is unhealthy, and
`RequireReconciliationHealthy` defaults to **true** — so with the reader hardcoded to `Unsupported`,
those two modes were unconditionally blocked. Giving the reader a real implementation is what lets
them run for the first time. That is the intended outcome, but it means step 3 is not purely
observational: it changes which orders the gate admits. The health criteria are correspondingly
strict — all four reads must succeed, because a reconciliation that cannot see fills or cannot see the
resting book is not reconciliation, and unhealthy blocks orders, which is the safe direction.

## Prerequisite

`AhkPortalClient` gets its session by harvesting cookies from `AhkBroker`'s Chromium, so the browser
is still required for **login** even after everything else moves. Logging in over HTTP means
reproducing the portal's twelve positional single-character password boxes, of which a random subset
is enabled per attempt. That is worth leaving on the browser path — it is solved, it is exercised on
every session, and it is the one place where the DOM genuinely is the interface.
