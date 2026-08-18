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

`POST /Home/PlaceOrder` has not been observed at all — no capture exists of its request shape, its
response, or how it reports rejection. Migrating placement therefore means re-establishing, against
real money, all of the confirmation semantics that took real incidents to learn on the browser path.
The order-book verification pattern would carry over intact and would still be the actual evidence,
but the migration has to be driven by captures of real submissions, not by reading `site.js`.

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
