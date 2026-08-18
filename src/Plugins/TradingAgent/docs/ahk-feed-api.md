# AHK "Web Trade Cast" portal API

Captured live from `https://web.ahletrade.com` on 2026-08-18 by attaching Chrome DevTools to an
authenticated session and reading the network log plus the portal's own `/js/site.js`. Everything
below is observed, not inferred — where a detail could not be confirmed (the market was closed) it
says so explicitly.

The portal is an ASP.NET Core (Kestrel) app whose entire UI is driven by JSON endpoints under
`/Home/`. There is **no WebSocket and no SignalR** — live pricing is HTTP polling.

## Session

Login is `POST /Home/_Login` with `UserName` plus `Digit1..Digit12`, the positional
single-character password boxes the browser broker already automates. Authentication state is
carried by cookies:

| Cookie | Meaning |
| --- | --- |
| `.AspNetCore.Session` | The session. Everything else depends on it. |
| `trader` | Account code (e.g. `CC45698`). |
| `HouseName` | Broker house (`AHL`). |
| `page1symbols` … `page4symbols` | The symbol set currently subscribed on each market-watch page. |
| `maxorderno` | Highest order number the client has seen. |

`POST /Home/Relogin` refreshes the session and is called by the UI roughly once a minute. Its
response body is a status string: containing `"0"` means OK, containing `"8"` means the session is
dead and the client must log in again.

## Live pricing

Three calls, in order:

1. `GET /Home/GetSymolsList` *(sic — the endpoint really is spelled "Symols")* — the full symbol
   master, ~198 KB, one entry per symbol **per market**:
   ```json
   {"market":"REG","symbol":"OGDC","symbolName":"Oil & Gas Development Company Limited",
    "sectorName":"OIL & GAS EXPLORATION COMPANIES","approved":"Approved"}
   ```
   `market` is `REG` (regular), `ODL` (odd lot), `FUT` (futures). A symbol appears once per market
   it trades in, so `(market, symbol)` — not `symbol` — is the identity throughout this API.

2. `POST /Home/SendSubscriptionofSymbols` — declares what to stream. jQuery-serialised form body:
   ```
   formData[0][mkt]=REG&formData[0][symbol]=OGDC&formData[1][mkt]=REG&formData[1][symbol]=PPL
   &feedtype=MKT-FEED&pagenum=Page1
   ```
   `feedtype` is `MKT-FEED` (quotes), `MBO-FEED` (market by order) or `MBP-FEED` (market by price).
   `pagenum` is `Page1`…`Page4`, or `sectorwatch` / `futurewatch` / `shariawatch`. Each page is a
   separate, independently replaceable subscription slot; the portal's own UI puts **50 symbols**
   in a page and pages through larger lists. Whether 50 is a server limit or just the UI's table
   size was not established. An empty subscription posts just `feedtype=…&pagenum=…`.

3. `GET /Home/GetFeed` — the quotes. Response:
   ```json
   {"feed":[],"exchangeStats":[],"mboFeed":[],"mbpFeed":[],"marketStatus":"OHO\r\n"}
   ```
   `marketStatus` is compared by the client against `OPEN` and `CLOSED` after stripping `\r\n`;
   `OHO` was the value observed pre-open.

Each `feed[]` element has exactly these fields (destructured verbatim from `site.js`
`fillMarketWatch`):

| Field | Meaning |
| --- | --- |
| `mkt`, `symbol` | Identity. |
| `lastPrice`, `openPrice`, `high`, `low`, `closePrice` | Session prices. `closePrice` is the prior close the UI computes `change` against. |
| `change` | **Absolute** price change, not a percentage. |
| `average` | Session VWAP. |
| `buy`, `bVol`, `sell`, `sVol` | Best bid / bid size / best ask / ask size. |
| `totalVolume`, `totTrd`, `lTrdVolume`, `lTrdTime` | Cumulative volume, trade count, last trade size, last trade time. |
| `state`, `dir`, `flag` | Board state, tick direction, status flag. |

The bid/ask columns are the material gain over the PSX portal, which publishes no depth at all.

**Cadence.** The portal's own client polls `GetFeed` every **1000–2000 ms** (`setInterval(fillMarketWatch, …)`)
and `PingPong` every 4000 ms. Matching that is therefore not extra load on the broker; exceeding it is.

**Resolved — it is a SNAPSHOT, not a delta queue.** Confirmed live on 2026-08-18 pre-open: with 30
symbols subscribed and nothing trading (`marketStatus: OHO`), every single poll returned all 30 and
the book's last-update timestamp advanced on each one. So each response carries the full state of the
subscribed set, and a reader cannot "drain" updates away from another reader.

The earlier belief that the feed was empty outside market hours was **wrong**, and wrong for an
instructive reason: the capture was taken on a session with an EMPTY subscription (`page1symbols=`),
so the emptiness was the absence of a subscription, not the absence of a market. Once subscribed, the
feed serves reference data (previous close, prior session's OHLC, last price) pre-open.

The client still upserts into a book keyed by `(mkt, symbol)` rather than replacing state wholesale.
That is now belt-and-braces rather than load-bearing, and it stays: it costs nothing, it tolerates a
partial message, and it keeps the code correct if the portal's behaviour ever changes.

**Contention.** Because the feed is a snapshot, a second reader does not steal data from the first —
the browser broker's own Chromium running `site.js` can poll `GetFeed` harmlessly alongside the
direct poller. `AhkFeedWorker` still yields while the browser holds the trading screen, which is now
a politeness rather than a correctness requirement.

**The real hazard is the SUBSCRIPTION, not the feed.** `site.js` calls
`SendSubscription(…, "MKT-FEED", "Page1")` on every page load, built from its own market-watch table
— and the portal does not persist that table, so it is almost always EMPTY. Any page load therefore
**replaces the session's subscription with nothing**, and the feed silently goes quiet. This is the
mechanism behind the familiar "the watch list is empty again after login". `AhkFeedWorker` handles it
by re-subscribing the moment the browser releases the trading screen, with a silence watchdog as a
backstop — see `FeedSubscriptionGuard`.

## Other endpoints of interest

`GET /Home/GetUpperLowerCap` returns `[{symbol, market, upperCap, lowerLock}]` for the **whole
market in one call** — the price band that `Ahk.ClampPriceToBand` currently recovers by scraping the
order dialog one symbol at a time.

`GET /Home/PingPong` is not a keepalive despite the name. It returns an array of
`{time, type, message}` order events, where `type` is `BUYQ` / `SELLQ` / `BUYC` / `SELLC`
(buy queued, sell queued, buy cancelled, sell cancelled) and the terminal marker is
`message == "ENDUP"`. This is a live order-status stream.

The full `/Home/` surface referenced by `site.js`:

```
PlaceOrder          CancelOrder         GetOutstanding      GetActivityLog
GetOrderHisotry     GetTradeLog         GetExposureData     GetJSPorfolioDetails
GetAccountBalance   GetAccountStatement GetUpperLowerCap    GetMarketStates
GetTickers          GetFeed             PingPong            Relogin
GetUserData         GetSymolsList       SendSubscriptionofSymbols
Logout              GetPassCode         CreatePIN           ChangePIN
ChangePassword      GetBranchesCityName GetAnalyticsURL     GetFundtransferDetails
SetWithdrawlRequest GetCashWithdrawalStatement              CancelCashWithdrawalRequest
```

Note the misspellings (`GetSymolsList`, `GetOrderHisotry`, `GetJSPorfolioDetails`,
`SetWithdrawlRequest`) — they are the real paths.

This surface means the browser automation in `AhkBroker` is, in principle, replaceable end to end:
`GetOutstanding` / `GetActivityLog` would give `IBrokerStateReader` the fills and positions it
currently reports as `Unsupported`, and `GetExposureData` / `GetAccountBalance` would replace the
Exposure-dialog scrape. **That is deliberately out of scope here.** Only the read-only pricing path
is implemented; moving order placement off the browser is a much larger decision, because the
portal's own confirmation semantics (documented at length in `AhkConfig.VerifyOrderInBook`) are
known to be unreliable and would have to be re-validated against the JSON endpoints.
