# The AHL Analytics portal (`data.arifhabibltd.com`)

Captured live on 2026-08-19 via Chrome DevTools against a logged-in broker session, market
**closed** (`st: "SUS"`, last update 15:50). Everything below was verified by replaying the calls
from the page context; nothing here is inferred from reading JS alone unless said so.

The portal is a Laravel app ("Capital Stake" white-labelled for Arif Habib Limited). It is a
**separate product from the trading terminal** with its own auth, its own data, and its own
websocket. It is a research/analytics source, not an execution surface.

No tokens or session values from the capture are reproduced in this document — they are
per-session and one of them is valid for a year. Re-derive them with the handshake below.

## The authentication chain

Three hops. The only secret needed is the broker portal session the plugin already holds.

```
①  GET https://web.ahletrade.com/Home/GetAnalyticsURL
    Cookie: .AspNetCore.Session=…; trader=<client-code>; HouseName=AHL
    X-Requested-With: XMLHttpRequest
    → 200, body is a JSON *string*:
      "http://data.arifhabibltd.com/dashboard?token=<laravel-encrypted-blob>"

②  GET <that url>                                    (307 → https, then 200 text/html)
    → sets  laravel_session  +  XSRF-TOKEN  cookies
    → HTML <head> carries the two values everything else needs:
         <meta name="csrf-token"    content="…40 chars…">
         <meta name="access-token"  content="<RS256 JWT>">

③  All /api/** calls:   Authorization: Bearer <access-token>
                        Cookie: laravel_session=…     ← REQUIRED, see below
                        X-Requested-With: XMLHttpRequest
```

**The Bearer token alone is not enough.** Verified 2026-08-20: every `/api/v3` call — GET and POST
alike — answers `401 Unauthenticated` without the `laravel_session` cookie that hop ② sets. The token
authorises, the session cookie authenticates, and both are needed. An HTTP client must therefore keep
a cookie jar across the handshake and the calls that follow, and `AhlAnalyticsClient` now declares one
explicitly rather than relying on `SocketsHttpHandler.UseCookies` defaulting to true.

`X-CSRF-TOKEN` is **not** required — the snapshot POST returns 200 with or without it. It is sent
anyway because it is free and matches the portal's own page.

The `token=` blob in ① is Laravel `Crypt::encryptString` output — base64 of
`{"iv":…,"value":…,"mac":…,"tag":""}`. It decrypts server-side to the trader identity and maps to
portal user `sub: 1130`.

Three properties of this chain matter for implementation:

**The SSO blob is replayable.** Re-fetching `/dashboard?token=<same blob>` returns 200 with the
**same** `access-token` in the meta tag. It is not single-use and not nonce-bound, so a captured
URL keeps working.

**The Bearer token is long-lived.** Its `exp` sits ~365 days out (`iat` 2026-08-19 →
`exp` 2027-08-17), `aud: "4"`, `scopes: []` — a Laravel Passport personal access token, not a
short session token. So the handshake is a once-a-year event, not a per-request one; cache the
token and only re-run ①–② on a 401.

**Hop ② needs no JavaScript.** The Bearer token is in a `<meta>` tag in the server-rendered HTML,
so one regex over the response body replaces a browser. The entire chain is `HttpClient`-able —
this is *not* another Puppeteer dependency.

Two caveats seen during the capture:
- ① returns an `http://` URL that 307s to `https://`. Follow redirects, or rewrite the scheme
  before the request — don't send the trader-identifying blob over cleartext.
- A burst of ~25 `/api/v3` calls in a few seconds started returning `{"message":"Unauthenticated."}`
  (not 429) until the page was reloaded. Responses carry `X-RateLimit-Limit: 60`. **A 401 here
  means "slow down", not "token dead"** — retry with backoff before re-running the handshake, or a
  transient throttle will burn a fresh login every time.

## Hop ① is a single point of failure, and it has already failed

Observed 2026-08-20, roughly a day after the capture: `GET /Home/GetAnalyticsURL` on the **broker**
portal began answering **500 with an empty body** and stayed that way across repeated attempts.
Everything about the diagnosis is worth recording, because the symptom pointed away from the cause.

What was true at the same moment, on a session logged in seconds earlier:

| call | result |
| --- | --- |
| `GET /Home/GetAnalyticsURL` | **500, empty body** (also 500 as `GetAnalyticsUrl`; 404 for POST, so GET is the right verb) |
| `GET /Home/GetUpperLowerCap` | 200, 100 KB |
| `GET /Home/GetClientInfo` | 200 |
| `GET /Home/GetCollaterals` | 200 |
| `GET /Home/GetOutstanding` | 200 |

So the broker session was entirely healthy and only this one endpoint was broken. **The portal's own
"AHL Analytics" button is equally broken**: clicking `#AHLAnalytics` produced the same 500, and
because `OpenAnalytics()` only does `console.log(err)` in its error branch, `window.open` is never
called and the button silently does nothing. That is the check to run before suspecting local code —
if the button does nothing, hop ① is down and nothing downstream can work.

Two consequences were designed in rather than discovered later:

**A failed handshake now opens a five-minute cooldown.** Hop ① runs against the broker session, so
restoring a dead one launches a browser and logs in. Without a cooldown, every caller wanting fresh
data re-attempts a handshake that cannot succeed, and each attempt can cost a login — against an
account the broker blocked once already for roughly fifteen logins in two hours. Failing fast for a
few minutes beats rediscovering a permanent outage every thirty seconds.

**Polled callers may never trigger the handshake at all.** `GetMarketSnapshotAsync` takes
`allowHandshake`, and both dashboard endpoints pass `false`. The movers panel had been calling it
with the default `true` on a 30-second timer, which turned a broken upstream into a login generator —
visible in the activity log as `Browser session opened` / `Logging in to the broker portal` firing the
moment the dashboard loaded. Agent- and user-initiated calls still pass `true`, because a person asked
for the data and one login is a fair price; a timer never asked for anything.

The panel now distinguishes three states — portal disabled, no session yet (and it says it will not
start one), handshake cooling down — and prints the upstream status and body snippet rather than
guessing at a cause.

## Market depth: what is actually available

**There is no L2 / order-book depth on this portal.** This is the one thing worth being blunt
about, because the MBO/MBP ladders visible in the trading terminal make it look like there should be.

- Every REST probe for a book — `/depth/{sym}`, `/orderbook/{sym}`, `/book/{sym}`, `/mbp/{sym}`,
  `/quote/{sym}` — returns `500 Server Error`. The `?path=` proxy is a **whitelist**, and only
  `/req`, `/daily/{sym}`, `/intraday/{sym}/{1D,2D,5D}` are on it.
- The websocket tick payload carries exactly four book fields: `bp`, `bv`, `ap`, `av` — **best bid
  and best ask with sizes. L1 only.**
- The stream token's scopes are `["market:read", "market:announcements"]`. There is no depth scope
  to ask for.

The MBO/MBP ladders in the terminal are fed by the broker's own feed (`web.ahletrade.com`).
**Depth stays there.** This portal adds nothing to that path, and any plan that routes depth through
here is built on a wrong premise.

**Correction, 2026-08-20.** An earlier version of this section said the broker feed "is what
`AhkFeedWorker` / `AhkQuoteBook` already talk to", which was true of quotes and wrongly implied depth
came with them. It did not: the plugin collected **no depth at all, by any means**. The only
subscription ever sent was `feedtype=MKT-FEED`, and although `AhkFeedResponse` declared `mboFeed` and
`mbpFeed`, they were typed `List<object>` with **zero consumers anywhere in the codebase** — so the
arrays were always empty and nothing could have read them if they were not.

Depth is now collected, on the broker feed:

- `POST /Home/SendSubscriptionofSymbols` with `feedtype=MBP-FEED` or `MBO-FEED` and a single symbol.
  MBP is the ladder aggregated per price level; MBO lists individual orders. Both are requested.
- Rows then arrive in the `mbpFeed` / `mboFeed` arrays of the ordinary `GET /Home/GetFeed` response, so
  depth adds **no polling** once subscribed.
- `AhkDepthBook` stores them, `get_market_depth` and `GET /trading/feed/depth` read them, and
  `AhkFeed:DepthEnabled` gates it (off by default).

### The depth payload, captured 2026-08-20 (PPL, market open)

```
mbpFeed[i] = {"orders":3, "volume":5510, "price":238.52,     ← BID side
              "sOrders":1, "sVolume":82,  "sPrice":238.7}    ← ASK side

mboFeed[i] = {"price":238.52,"volume":10,"flag":"dc","orderNo":null,
              "sPrice":238.7,"sVolume":82,"sFlag":"dc","sOrderNo":null}
```

Four properties, each of which produces a plausible-looking wrong answer if mishandled:

**Every row carries BOTH sides.** Unprefixed fields are the bid ladder, `s`-prefixed the ask, zipped
by index — row 0 is the best bid beside the best ask. It is *not* a flat list of levels with a side
marker, so reading a row as one side pairs each bid price with the opposing quantity, and the result
looks like a perfectly ordinary book.

**The arrays are fixed-length and zero-padded.** Thirteen MBP rows arrive whether or not thirteen
levels exist, with unused rows all zeros. Padding must be dropped or "best ask" resolves to 0 and
total depth is inflated with nothing — the same "zero means unknown, never a real price" rule the
quote path already applies.

**Depth is published only when the book CHANGES, as a full replacement.** Most polls carry empty
arrays; over fourteen consecutive polls of PPL, one carried data. So an empty array means "unchanged",
never "the book is empty", and the last known ladder has to be retained or it blinks out constantly.

**Rows carry no symbol at all.** The portal follows one depth symbol at a time, so rows are attributed
to whichever symbol is subscribed — which is why that field is authoritative rather than decorative.

`AhkDepthBook` owns all four rules and derives what a decision actually needs: best bid/ask with the
quantity at the touch, spread, total resting volume per side, and book imbalance (−1 all offered to +1
all bid). A one-sided book yields a null spread rather than a fabricated one, since a bid with nothing
offered is normal at a circuit cap.

`Page5` is **confirmed accepted** — both `MBP-FEED` and `MBO-FEED` subscriptions answered 200 there
while the quote feed held Page1–Page4, so the depth slot genuinely sits outside the quote set.

**`pagenum` is one namespace shared by every feed type, and a subscription REPLACES the slot.** So
subscribing depth on a page the quote feed uses evicts that page's quote symbols, and the portal
reports nothing at all: `GetFeed` answers 200 with an empty array whether nothing traded or nothing is
subscribed. The only symptom would be quotes silently stopping for fifty symbols. `DepthPage` must
therefore not appear in `Pages`, and the subscription is **refused outright** on overlap rather than
trusting configuration to be careful. The default quote pages are Page1–Page4, so `DepthPage` defaults
to `Page5` — which is **unverified**: whether the portal accepts a fifth slot was never tested, and if
it does not, the honest fix is to shrink `Pages` rather than to overlap.

What it *does* add is breadth: 857 equities of history, ratios, and precomputed indicators in a
handful of calls.

## The live websocket

```
POST /api/market-stream/token        (laravel_session cookie + X-CSRF-TOKEN, empty body)
→ { token, url: "wss://market.capitalstake.com/stream/secure?token=…",
    expires_in: 3600, claims: { scope: ["market:read","market:announcements"], sub: "1130" } }
```

**Server-push firehose — there is no subscribe protocol.** The client sends nothing; sending
`{action:"subscribe",…}` is ignored. Every message is `{ t: <type>, d: <payload> }`:

| `t` | payload |
| --- | --- |
| `tick` | `{s: symbol, m: "REG"\|"IDX", st: "OPN", t: unixSec, o, h, l, c, v, val, ch, pch, ldcp, bp, bv, ap, av, lt: {t, v: price, x: volume}}` |
| `announcement` | corporate announcement, `is_update` flag |
| `signal` | published to a `SIGNALS` topic; no sample captured (market closed) |

Field mapping is taken from the portal's own `formatTick`, so it is the vendor's own naming.
Note `lt` is confusingly ordered: `parseLt` maps `lt.v → price` and `lt.x → volume`.

The portal's client filters client-side on `st === "OPN"`, same-day `t`, and
`m ∈ {IDX, REG}` — so a consumer must expect stale and off-market frames to arrive and drop them
itself. Token TTL is 3600s and the client refreshes 60s early; the socket survives the refresh by
reconnecting with a new token.

**The socket was silent for a 9s hold with the market closed** — no heartbeat, no snapshot on
connect. It could not be confirmed to carry live ticks in this capture, and that verification has
to happen during market hours before anything depends on it.

## REST surface

Authoritative list — extracted from the portal's `script.js` and then each one replayed.

### `POST /api/v3/market?path=/req` — body `item=market`

**The single highest-value call on the portal.** One request, ~1.1 MB, the entire market:

```
{ st: "SUS", lu: "2026-08-19 15:50:00",
  eq:  { <857 symbols> }, in: { <18 indices> },
  fut: { <308 futures> },  odl: { <9 ETFs/odd-lot> } }
```

An equity record (`eq.LUCK`, all fields observed):

| group | fields |
| --- | --- |
| identity | `nm` name, `sc` sector code, `ty`, `st` state, `d` last tick time |
| OHLC | `o h l c v` , `ldcp`/`ldcv` last-day close/volume, `ch` `pch`, `avg`, `tr` trade count |
| **L1 book** | `bidp bidv askp askv` — **all 0 when closed**; only populated intraday |
| **circuit** | `uc` upper cap, `lc` lower lock, `var` %band, `hc` haircut |
| ranges | `h52` `l52` |
| **float** | `sh` shares out, `ff` free float |
| **technicals** | `rsi`, `std`, `pp:{pp,r1,r2,r3,s1,s2,s3}` pivots, `bt:{1m,3m,6m,1y}` **beta** |
| fundamentals | `eps` `dps`, `pm` net margin %, `di` div yield %, `pr` (unresolved — see below), `sa` sales, `pat`, `as` assets, `sg3y` `scagr5y` `pcagr5y` `eg3y` growth |
| price history | `p1w p1m p3m p6m p1y pytd pfy p5y` |
| volume history | `vw vm v3m v6m vy vytd vfy`, averages `vaw va10d vam va3m va6m vay vaytd v30a` |
| **corp actions** | `xb` ex-bonus, `xd` ex-div, `xr` ex-right, `sd` |
| membership | `li: ["KSE100","KMI30",…]` — index membership per symbol |

Index records (`in.KSE100`) add `val` turnover, `cw cm c3m c6m cy cytd c5y` historic closes, and
`v5a v10a v30a v90a v180a` average volumes. Futures (`fut`) add `fut:{dd, dm, ltd, sm, cm}`
delivery/maturity/last-trade dates and `eq` the underlying — a ready-made futures→spot mapping.

#### Decoding the two-letter keys

The portal ships its own key map (`cs.market.Mappings`), reproduced here because the field names
are otherwise unguessable:

```
st→status  lu→last_update  d→date  sc→sector  nm→name  ds→description  ty→type
o→open  h→high  l→low  c→close  v→volume  ch→change  pch→percent_change
l52→low_52  h52→high_52  ldcp/ldcv/ldci→last-day close/volume/index
sh→shares  ff→free_float  sa→sales  as→assets  eps→eps  li→listed_in  pp→pivot_points
p1w/p1m/p3m/p6m/p1y→price N ago
```

Its own map is **incomplete and in one place wrong**, so three fields were resolved by comparing
`/req` against `company-statement` for LUCK:

| key | value (LUCK) | actually is |
| --- | --- | --- |
| `pm` | 34.1539 | **net profit margin, ×100** (`npm` = 0.34153…). The vendor map omits it. |
| `di` | 1.0649 | **dividend yield, ×100** (`div_yield` = 0.010649…). |
| `pr` | 15.7084 | **unresolved.** The vendor map labels it `profit_margin`, which it is not. It is not `pe_ratio` (14.75), nor `close/eps` (13.74), nor `pb_ratio`/`ps_ratio`. Implies an EPS of 27.83 on an unknown basis. |

`pm` and `di` are percent-scaled while the `company-statement` equivalents are fractions — mixing
the two silently produces a 100× error. **Do not use `pr`**; take P/E from `pe_ratio` in
`company-statement`, which reconciles.

Sector codes (`sc`) are the PSX scheme: `0801` Automobile Assembler, `0804` Cement, `0805`
Chemical, `0807` Commercial Banks, `0809` Fertilizer, `0812` Insurance, `0813` Inv. Banks/Securities,
`0818` Miscellaneous, `0820` O&G Exploration, `0821` O&G Marketing, `0823` Pharmaceuticals,
`0824` Power Gen & Distribution, `0825` Refinery, `0826` Sugar, `0828` Technology & Communication,
`0829`–`0831` Textile (Composite/Spinning/Weaving), `0832` Tobacco, `0833` Transport, `0836` REIT,
`0837` ETF, `0838` Property. Full table (0801–0838) is in the bundle's `sectors` object.

### Market movers — derived client-side, not an endpoint

The dashboard's **Market Performance** widget (Leaders / Gainers / Losers) has **no backing API**.
It is computed in the browser from the `/req` snapshot, and the logic is worth copying verbatim
because of the filter in front of it:

```js
fresh   = filter(data.eq, v => v.d.substr(0,10) === data.lu.substr(0,10))  // traded TODAY only
gainers = fresh.sort((a,b) => b.pch - a.pch).slice(0,5)
losers  = fresh.sort((a,b) => a.pch - b.pch).slice(0,5)
leaders = fresh.sort((a,b) => b.v   - a.v  ).slice(0,5)   // "Leaders" = most active by volume
```

**That date filter is the whole trick.** The `eq` map contains every listed symbol including ones
that have not traded in months (e.g. `786R` carries a `d` of 2026-01-02 with a live-looking
`pch: -6.44%`). Ranking without comparing each row's `d` against the market's `lu` puts long-dead
symbols at the top of a "today's biggest movers" list. A gainers screen is one sort — the filter is
the part that makes it correct.

Since this is pure client-side derivation over a snapshot the plugin can already fetch, **the
plugin can produce the same movers lists, and better ones**, with no extra API surface: top movers
by `pch`, most active by `v`, unusual volume via `v / va10d`, gap-ups from `o` vs `ldcp`, movers
restricted to KSE100 members via `li`, or sector rotation by aggregating `pch` over `sc`. None of
these need a request the plugin isn't already making.

### Charts

| endpoint | returns |
| --- | --- |
| `GET /api/v3/market?path=/daily/{sym}` | **1235 daily bars ≈ 5 years**, one JSON call |
| `GET /api/v3/market?path=/intraday/{sym}/{1D\|2D\|5D}` | **1-minute bars**; 365 / 727 / 1812 rows |

**Indices work in both**, with the index symbol in place of the equity: `/daily/KSE100` → 1241 bars
back to 2021-08-20, `/intraday/KSE100/5D` → 1881 one-minute bars. So index overlays and
relative-strength-vs-KSE100 come from the same two calls, no separate index API.

Row shape `{date, open, high, low, close, volume, shares, value}`. Three gotchas:
- **Newest-first.** `data[0]` is today; `data[last]` is the oldest.
- `shares` and `value` are **always 0**. Don't build turnover on them.
- **Daily closes are corporate-action adjusted** and the `?adj=false` the JS hints at is
  *ignored* — LUCK's 2021 close comes back as `162.09221369698164`. `from`, `limit`, and any other
  query param are ignored too; the windows are fixed. Fractional-paisa closes are the adjustment
  fingerprint, and they will not reconcile against a broker fill price.
- Only `1D`, `2D`, `5D` exist. `10D`/`1W`/`1M`/`3M`/`6M`/`1Y`/`MAX` all 500.

#### Compared against the PSX source the plugin already uses

Measured on 2026-08-19 by fetching LUCK from both sources for the same dates.
`PsxDataClient.Candles` POSTs a date to `dps.psx.com.pk/historical` and parses the returned HTML
table; the analytics portal serves `/daily/LUCK` as JSON.

| | PSX (`dps.psx.com.pk/historical`) | AHL (`/daily/{sym}`) |
| --- | --- | --- |
| Request unit | one POST **per date**, whole market | one GET **per symbol**, whole history |
| 5 years of one symbol | **~1235 POSTs** + HTML parse | **1 request** |
| Format | HTML table scrape | JSON |
| Prices | **raw, as traded** | **corporate-action adjusted** |
| Volume | as traded | **also adjusted** (scaled by the action) |
| Extra fields | `LDCP`, change, change % | none (`shares`/`value` always 0) |
| Intraday | no native path | **native 1-minute, up to 5 days** |
| History depth | deeper, but with holes | 5 years, continuous |

The measured divergence, and why it decides the split of responsibilities:

| date | PSX close | AHL close | ratio | PSX volume | AHL volume | ratio |
| --- | --- | --- | --- | --- | --- | --- |
| 2026-08-18 | 439.30 | 439.30 | 1.0000 | 1,250,095 | 1,250,095 | 1.00 |
| 2025-08-19 | 425.98 | 422.41 | 0.9916 | 3,914,791 | 3,914,791 | 1.00 |
| 2024-08-19 | 853.02 | 166.29 | 0.1949 | 40,874 | 204,370 | **5.00** |
| 2021-08-20 | 859.90 | 162.09 | 0.1885 | 924,995 | 4,624,975 | **5.00** |

The exactly-5.00 volume ratios pin the mechanism: LUCK had a 5:1 action between Aug 2024 and Aug
2025, and AHL back-adjusts every prior bar — price ÷5, volume ×5. The 0.9916 in 2025 is subsequent
dividend adjustment. Today's bar is identical in both, so **recent data agrees exactly** and only
history diverges.

**Neither source is simply better; they answer different questions, and both should be kept.**

- **AHL is the right source for charts, indicators and levels.** One request instead of 1235 is the
  headline, and native 1-minute intraday is a capability the PSX path does not have at all. But the
  deeper reason is the adjustment: a *raw* series contains an artificial −80% cliff on the split
  date, and RSI, MACD, ATR and every swing-pivot level computed across that cliff are garbage. The
  adjusted series is the correct input for technical analysis, not merely the cheaper one.
- **PSX remains the source of record for anything touching money.** Adjusted prices do not
  reconcile against a fill, and adjusted volumes are not the shares that changed hands. A stop
  computed off AHL's 166.29 for a position actually bought near 853 would be catastrophically wrong.
  Reconciliation, realised P&L, and audit keep using PSX.

So the integration is additive: `AhlCandleSource` alongside the PSX one, each tagged with its
source, and a hard rule that adjusted and raw series are never concatenated. Both agree on the
current session, which is what makes them safe to diff as a staleness check.

One incidental finding: PSX returned an empty table for 2023-08-18 while AHL had a bar for it.
Whether that is a portal gap or a settlement-calendar quirk was not chased, but it means the PSX
archive can have holes that the AHL series fills — another reason to keep both rather than migrate.

### `GET /api/v3/indicators` — precomputed, whole market

529 symbols × 11 indicator families in one call:
`sma(25/50/100/200)`, `bb(20,2)` → `[upper, mid, lower]`, `volt(10)`, `rsi(14)`,
`stoch(9,6,3)` → `[%K, %D]`, `macd(12,26,9)` → `[macd, signal, hist]`, `roc(9)`, `cci(14)`.

Daily-resolution latest values only — no history, no intraday.

### `GET /api/v3/company-statement?symbol={sym}&interval={annual|quarterly}&type={…}`

`type` ∈ `fundamentals | income | balance | other | profile | shareholders`.

Statements return `{periods[], fields[{label, key, unit, description, latex, values[]}]}` —
`values[i]` aligns to `periods[i]`. Quarterly periods are labelled `{year, quarter, period_end}`;
annual `fundamentals` gives **TTM + 18 fiscal years**.

`type=fundamentals` is 41 ratio series — and each carries **`sector_stats: {key, min, max,
median}`**, so a symbol can be ranked against its sector without pulling peers:

`gpm opm pbtm npm ooi roce roe roa dps div_yield div_cover retention shares payout s_ps mp_ps
eps bkv pe_ratio pb_ratio ps_ratio na_ps cap_emp eq_mul eta ltde ltsa lta solr intc cash_ps
cur_ratio qu_ratio wc_rs rec_days inv_days pay_days wc_days tat fat invt`

`type=profile` gives sector, description, employees, `year_end` (**fiscal year end — needed to read
quarterly results correctly**), `par_value`, capacity/utilisation, auditors, registrar, website,
`status`. A few `income` rows have `key: null` (e.g. "Distribution costs") — key on `label` as a
fallback or those rows silently vanish.

### Announcements, payouts, news, research

| endpoint | returns |
| --- | --- |
| `GET /api/v3/payouts/announcement-break-down/{sym}` | **265 rows for AKGL** — full payout history: `dividend`, `bonus`, `rightPrice`, `exDate`, `book_closure_date_from/to`, plus parsed `unconsolidatedSales/Pat/QuarterEps`, `periodEndDate`, `quarter`, and the PSX PDF link |
| `GET /announcements/{sym}?rangeFrom=&rangeTo=&type=ALL` | per-symbol announcements, same shape |
| `GET /api/v1/announcements/board-meeting` | **market-wide upcoming board meetings** — `details` packs `datetime`, `location`, `periodEndDate`, `agenda` |
| `GET /api/v1/announcements/financial-result` | market-wide results as they post; `details` packs sales/PAT/EPS |
| `POST /api/v3/news/{sym\|GENERIC}` | Business Recorder headlines + summary + link; cursor-paginated (`next`, `total`) |
| `GET /client-research-v2/data/list?count=&offset=&symbol=` | **AHL's own analyst notes** — title, full body (`dsc`), analyst + category ids. Real sell-side research, e.g. *"AHL Alert – LUCK Highest Ever EPS in FY26 of PKR 60.78/share"* |
| `GET /insider-transaction/api?sort=desc[&symbol={sym}]` | **insider trades** — `{date, type: buy\|sell, symbol, name, description: "Executive"\|"Senior Management"\|…, price, shares, share_type, market, notice_id, notice_date}`. 50/page market-wide; `&symbol=` filters (LUCK → 23 rows). Note `date` (dealt) vs `notice_date` (disclosed) differ by days — **key off `notice_date` for anything time-sensitive**, since that is when the information became public. |
| `GET /insider-transaction/api/stats?freq=daily&sort=asc&from=&to=` | aggregate buy/sell counts per period — `{date_from, date_to, buy, sell}` |
| `GET /persons/organization?code={sym}` | management roster with designations and tenure |
| `GET /api/v3/economy-data` | macro series — GDP, etc., `{indicator, period, current, previous}` |
| `POST /api/v3/currency-rates` | FX |

**Empty for this account** (verified, not assumed): `/api/v3/settlement/{sym}` and
`/api/v3/portfolio-investments?type={foreign|local}` (FIPI/LIPI flows) both return
`{"response":"success","message":"No data found","data":null}` for every period value tried. The
FIPI/LIPI foreign-flow dataset — which would be genuinely valuable on PSX — is **not provisioned**.
`/api/v3/payouts/industry-average` 404s.

## What this changes for the plugin

Measured against what `TradingAgent` does today.

### 1. Candle history — replaces an HTML scrape

`PsxDataClient.Candles` POSTs a form to `dps.psx.com.pk/historical` and parses HTML, one symbol at
a time. `/daily/{sym}` returns 5 years of JSON in one call, and `/intraday/{sym}/5D` gives
1-minute bars the PSX scrape has no equivalent for. This feeds `CandleHistoryProvider`,
`CandleResampler`, and `AnalyzeCandlesTool` directly, and it is the change that most improves the
charts in `ui/`.

The adjustment caveat above is a real constraint, not a footnote: **adjusted history must not be
mixed with broker fill prices** in the same series, or a stop computed off it will sit at the wrong
level after any bonus issue. Keep the source tagged on the candle.

### 2. `?path=/req` replaces per-symbol polling for screening

`ScanWatchlistTool` and `StockAssessmentService` currently walk symbols individually. One `/req`
call covers 857 equities with RSI, pivots, beta, 52-week range, free float, circuit caps, average
volumes over seven windows, and index membership. A whole-market screen becomes one request
instead of N — and `va10d`/`v30a` give the liquidity filter that position sizing needs.

`sh`/`ff` (shares out / free float) also enable a check the plugin cannot currently make: whether
an intended order size is sane relative to the symbol's actual tradeable float.

### 3. Market movers / daily-trader screens — free, off the same snapshot

The dashboard's Leaders/Gainers/Losers is the single most directly useful thing here for a
day-trading loop, and it costs **nothing extra**: it is five lines of LINQ over the `/req` snapshot
already fetched for §2. Reimplement it with the today-only `d == lu` filter described above, then
extend past what the portal shows:

- **Unusual volume** — `v / va10d`, the strongest intraday-continuation screen available in the
  snapshot, and something the portal itself doesn't display.
- **Gap detection** — `o` vs `ldcp`.
- **Movers within a tradable universe** — filter by `li` containing `KSE100`, or by `ff`/`va10d`
  above a liquidity floor, so the list isn't topped by illiquid names the agent shouldn't touch.
- **Near-circuit warnings** — `c` against `uc`/`lc`. A symbol pinned at its upper cap cannot be
  bought higher, which changes the order decision, not just the display.
- **Sector rotation** — aggregate `pch` by `sc`.

This belongs in `ScanWatchlistTool` (or a new `market_movers` tool) and in the `ui/` dashboard.

### 4. Fundamentals + sector percentiles — new capability

There is no fundamentals path in the plugin at all today. `company-statement` with its
`sector_stats` gives `research_stock` a real valuation view (P/E, P/B, ROE, leverage, working-capital
days) *and* the sector median to judge it against.

### 5. Event risk — the highest-value addition for safety

`/api/v1/announcements/board-meeting` and the `xd`/`xb`/`xr` flags plus `exDate` /
`book_closure_date_from` from the payouts feed let the agent know **before** it trades that a symbol
goes ex-dividend tomorrow or has results due. Given that PSX day orders are cancelled at market
close and protective stops don't survive overnight (see `ahk-direct-api-migration.md`), holding
through an unknown event is exactly the exposure worth avoiding. This deserves a hard pre-trade
gate in `Safety/`, not just a note in a report.

### 6. Cross-check, not replacement, for the live feed

`AhkQuoteBook` stays the execution-path quote source: it is the broker's own feed, it has the
MBO/MBP depth, and it is what fills reconcile against. The portal's `bp/bv/ap/av` and `/req`
snapshot are useful as an **independent** second opinion — a stale-feed detector — which is worth
having given the feed re-subscription problems already documented. Two sources disagreeing is a
signal; one source silently going stale is the failure mode that has already cost sessions here.

### 7. Analyst research as agent context

`client-research-v2` is AHL's own sell-side notes in full text. That is a qualitative input
`ResearchStockTool` currently has to go to the open web for, and it is the broker's actual house
view on symbols this account can trade.

## What was built

Implemented and live-verified. The current full suite is 541 tests: 535 passed and 6 opt-in live
broker/order tests were skipped, with 0 failures.

| file | role |
| --- | --- |
| `Config/AhlAnalyticsConfig.cs` | `Plugins:AhlAnalytics`, `Enabled` defaulting **false** |
| `Feed/AhkPortalClient.GetAnalyticsUrlAsync` | hop ①, on the class that owns the broker session |
| `AhlAnalytics/AhlAnalyticsModels.cs` | typed DTOs; every two-letter key mapped once via `[JsonPropertyName]` |
| `AhlAnalytics/AhlAnalyticsClient.cs` | handshake, token cache, rate limiter, snapshot and per-symbol daily-candle caches, typed calls |
| `AhlAnalytics/AhlDailyCandleCache.cs` | case-insensitive, per-symbol single-flight daily-series cache; empty responses are retried |
| `AhlAnalytics/AhlCandleSource.cs` | daily bars as `PsxCandle`, preferred over the PSX scrape |
| `AhlAnalytics/AhlMovers.cs` | the screens — pure computation over a snapshot, no I/O |
| `Tools/MarketMoversTool.cs` | `market_movers` agent tool |
| `Tools/StockDossierTool.cs` | `stock_dossier` agent tool, dimension-addressable |
| `Tools/MarketDepthTool.cs` | broker Page5 MBP/MBO focus path; shared by chat and the authenticated diagnostic action |
| `ui/src/MoversPanel.svelte` | dashboard panel; `GET /trading/movers`, `/trading/movers/sectors` |
| `tests/…/AhlMoversTests.cs` | 14 tests, weighted on the freshness filter |

Three decisions worth recording, because each was made against a live-capture finding:

**The 401 handler backs off before it re-authenticates.** This portal returns
`401 Unauthenticated` for rate-limiting rather than 429, while the token stays valid. Treating 401 as
"token dead" would re-run the handshake on every throttle — and hop ① can launch Chromium to restore
a dead broker session, so that is a *login per throttle* against a broker that has withdrawn access
before. The client retries twice on the same token first, and only the third attempt re-handshakes.

**Indicators are computed locally, not taken from `/api/v3/indicators`.** The two disagree
materially and the portal's own UI ignores that endpoint. `PreferPortalIndicators` exists but
defaults false.

**Candle reads never trigger the SSO handshake.** `AhlCandleSource.ReadyWithoutHandshake` gates on a
token being *already held*, not merely on `Enabled`. Hop ① runs against the broker session and
restoring a dead one can launch a browser, and a candle read happens on every scan — so it falls back
to PSX rather than becoming the thing that logs in. Once an agent- or user-initiated call
(`market_movers`, `stock_dossier`) has obtained a token, candle reads start using it.

`CandleHistoryProvider` therefore resolves per symbol: AHL when a token is held and it returns usable
depth, otherwise the existing archive/PSX path. An AHL series is used **whole** — never concatenated
with archived PSX bars, since the two sit on different price scales either side of any corporate
action — and AHL bars are **never written to `daily_bars`**, which holds raw exchange data and is what
reconciliation reads. `CandleHistory.Sources` reports the choice per symbol, and the mix is posted to
the trading activity log (visible in the dashboard's activity panel) whenever it changes, so a
fallback to PSX is visible rather than silent.

**No AHL handshake creates a broker session on a timer.** An explicit agent-tool call may establish
one. The dashboard is passive: while the broker is disconnected it only reports that it is waiting;
after the AHK feed already has a session, it may perform the cheap authenticated SSO GET and populate
the separate AHL session. Snapshot and candle caches keep subsequent polls local.

### The agent-facing surface

`market_movers` — nine screens (`gainers`, `losers`, `most_active`, `most_valuable`,
`unusual_volume`, `gap_up`, `gap_down`, `near_upper_cap`, `near_lower_lock`), filterable by index,
sector, and turnover/volume/price floors, plus session breadth and sector rotation. All from one
cached snapshot, so screen count does not multiply upstream traffic.

`stock_dossier` — one symbol, addressed by dimension so a caller pays only for what it asks:
`quote`, `technicals`, `levels` cost nothing beyond the shared snapshot; `fundamentals`, `valuation`,
`income`, `balance`, `profile`, `events`, `payouts`, `insiders`, `news`, `research` cost one to two
calls each. This is the read surface a future autopilot is expected to sit on, which is why each
dimension states its units, carries sector medians next to the values they contextualise, and reports
the two data hazards (adjusted prices, unconsolidated-only statements) inline rather than leaving
them to be rediscovered.

Deliberately out of scope: anything on the order path. This portal is read-only research, and
`web.ahletrade.com` remains the only execution surface.

## Pages not yet captured

The portal nav exposes more than the dashboard and company pages this capture covered. Each is a
page whose XHRs would need the same treatment; listed so the next pass doesn't have to rediscover
them:

`/screener` (Advanced Screener — likely the highest value of these, a server-side filter over the
whole market), `/sectors/overview`, `/indices`, `/map` (Market Map / heatmap),
`/pivot-points`, `/historical-data`, `/advance-charting`, `/announcement-calendar?type={FR|BRM|EOGM|PYT}`
(the calendar behind the per-type announcement feeds), `/client-research/v2` (PDF research library).

`/portfolio-investments` (FIPI/LIPI) and Settlement Analysis are also in the nav but their APIs
return no data for this account, so their pages will be empty regardless.

## Verified 2026-08-20, market open

Settled against a live open session (`st: "OPN"`), so these are no longer assumptions:

- **The L1 book populates intraday.** LUCK returned `bidp 439.60 × 26` / `askp 439.61 × 239`, and 507
  of 857 equities carried a non-zero bid or ask. The all-zero capture on 2026-08-19 was purely the
  market being closed.
- **`laravel_session` is required, CSRF is not.** See the auth chain above. The earlier claim that a
  POST without `X-CSRF-TOKEN` is rejected with 419 was **wrong** — a guess made while the SSO endpoint
  was down and no live POST could be tried. It has been corrected in the code comments too.
- **The snapshot model parses the real payload.** `AhlSnapshotDeserializationTests` runs against a
  trimmed verbatim copy of a live response, including the awkward parts: a string market state beside
  an integer per-symbol state, `pch` as a fraction against percent-scaled `pm`/`di`, the nested
  `pp`/`bt` objects whose keys are not valid identifiers, a populated book, and odd-lot rows with null
  nested objects. This test exists because a parse failure and an outage are indistinguishable from
  the outside — both surface as "portal unavailable".
- **Hop ① recovered.** `GET /Home/GetAnalyticsURL` returns the SSO URL again. Its 500 on 2026-08-20
  was a transient broker-side outage, which is what the cooldown and the no-handshake-on-poll rules
  were built for.
- **The running dashboard establishes AHL from an existing AHK session.** With 30/30 AHK quote
  symbols fresh, the passive movers request completed the separate SSO hop without another broker
  login and returned a live 489-symbol market snapshot. A current payload also exposed `bt: false`
  for symbols without beta history; the beta converter now treats non-object optional beta values as
  missing instead of discarding the entire snapshot.
- **The plugin depth path is now verified end to end.** With `AhkFeed:DepthEnabled=true`, the
  `get_market_depth` tool called `FocusDepthAsync("PPL")`, subscribed Page5, and returned 10 MBP plus
  10 MBO rows (best bid 237.63, best ask 238.17) while all 30 quote symbols stayed fresh.
- **Daily candle calls are cached per symbol.** The default 720-minute cache is case-insensitive,
  single-flight for concurrent cold reads, and does not cache empty/failing responses. A 31-symbol
  scan now costs 31 AHL GETs only on the first cold pass; routine two-minute scans reuse the series,
  leaving the shared limiter available for the market snapshot POST. On a completely cold start,
  candle loaders also wait behind the first whole-market snapshot, so token publication cannot let
  31 GETs jump ahead of the POST that populates the movers dashboard.

## Verification still owed

1. **Confirm the websocket actually delivers ticks during market hours** — it was silent in the closed
   capture, and a firehose that turns out to be idle would quietly starve whatever consumes it. Not
   retried while open.
