# Replacing the browser automation with the portal's JSON API

**Status: proposed, not started.** Nothing in this document is implemented. It is written down so a
future session does not have to rediscover it, and so the decision is made deliberately rather than
drifted into.

The live pricing work (see `ahk-feed-api.md`) established that the AHK portal is a plain JSON API
behind a session cookie, and built `AhkPortalClient` to talk to it. Read-only pricing and order
cancellation now go that way. Everything else in `AhkBroker` — 2,700 lines of PuppeteerSharp driving
a real Chromium — is still clicking through the DOM to reach endpoints that would answer directly.

## What the browser currently does, and what could replace it

| Today (`AhkBroker`) | Direct endpoint | Notes |
| --- | --- | --- |
| Fill `#buysymbol`/`#buyvolume`/`#buyprice`/`#buyPIN`, click BUY, confirm a `swal` prompt | `POST /Home/PlaceOrder` | The high-risk one. See the warning below. |
| Read the Outstanding Log tab to verify an order exists | `GET /Home/GetOutstanding` | **Already migrated** — used by `list_outstanding_orders` and `cancel_order`. |
| Read the Activity Log tab | `GET /Home/GetActivityLog`, `GET /Home/GetTradeLog`, `GET /Home/GetOrderHisotry` | Fills and history. |
| Open the Exposure modal, pick the account, flip tabs, scrape `#collateralstable` and `#exposuretable1` | `GET /Home/GetExposureData`, `GET /Home/GetJSPorfolioDetails`, `GET /Home/GetAccountBalance` | Would replace the whole `PortfolioTabSequence` / `HoldingsColumnMap` heuristic apparatus in `AhkConfig`. |
| Read the day's band off the order dialog, one symbol at a time (`ClampPriceToBand`) | `GET /Home/GetUpperLowerCap` | Returns `[{symbol, market, upperCap, lowerLock}]` for the **whole market in one call**. `AhkPortalClient.GetPriceBandsAsync` already implements it; nothing consumes it yet. |
| — | `GET /Home/PingPong` | Not a keepalive. A live order-event stream: `{time, type, message}` with `type` in `BUYQ`/`SELLQ`/`BUYC`/`SELLC`, terminated by `message == "ENDUP"`. |

Endpoint spellings are the portal's own and several are misspelled — `GetSymolsList`,
`GetOrderHisotry`, `GetJSPorfolioDetails`, `SetWithdrawlRequest`, and the `orignalorderno` form field.
Correcting them silently no-ops.

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

**Suggested order of work**, each independently shippable and reversible:

1. `GetUpperLowerCap` → replace the per-order dialog band scrape. Pure read, immediate win, and
   `GetPriceBandsAsync` already exists.
2. `GetExposureData` / `GetAccountBalance` / `GetJSPorfolioDetails` → replace `GetPortfolioAsync`.
   Pure read. Keep the browser scrape behind a config flag for one release to cross-check the numbers.
3. `GetActivityLog` / `GetTradeLog` → give `IBrokerStateReader` a real implementation so
   reconciliation starts working.
4. `PingPong` → a live order-event stream, which would let order confirmation stop polling.
5. `PlaceOrder` / order modification — **only** with captured evidence from real submissions, and
   only with order-book verification retained.

Steps 1–4 are read-only and carry no execution risk. Step 5 is a different category of change and
deserves its own decision.

## Prerequisite

`AhkPortalClient` gets its session by harvesting cookies from `AhkBroker`'s Chromium, so the browser
is still required for **login** even after everything else moves. Logging in over HTTP means
reproducing the portal's twelve positional single-character password boxes, of which a random subset
is enabled per attempt. That is worth leaving on the browser path — it is solved, it is exercised on
every session, and it is the one place where the DOM genuinely is the interface.
