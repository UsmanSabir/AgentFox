# Protective stops attached to an armed entry

## What the user asked for

> Arm a BUY at 564.93. Once it is *executed*, protect it with a stop at 554.

Two hard parts, and neither is the UI:

1. **How do we know the BUY executed?** The portal gives no fill callback.
2. **How does the stop survive the night?** PSX clears outstanding orders at market close, so a
   native stop placed today is gone tomorrow while the risk is not.

## What the broker can and cannot do

Established by reading the adapter, not assumed:

| Capability | Status | Where |
| --- | --- | --- |
| Place a native **Stop Loss** SELL (trigger + limit) | **Yes** | `AhkBroker.PlaceSellAsync` selects `"Stop Loss"` in `#sellordertype` |
| Read holdings (per-symbol qty, avg buy price) | **Yes** | `AhkBroker.GetPortfolioAsync` |
| Read the **outstanding** (resting) and **activity** books | **Yes** | `AhkBroker.ReadOrderBookAsync` |
| Fill / position / balance API | **No** | `AhkBrowserBrokerAdapter.ReadSnapshotAsync` returns `Unsupported` |
| **Cancel** a resting order | **No** | nothing in `AhkBroker` submits a cancel |

Everything below is shaped by the last two rows.

## Model: a standing intent, not a child order

A protective stop is **not** a queued second order. It is a durable *intent to keep N shares of a
symbol protected at a level*, which is re-materialised as a native day order every session until the
position is gone.

```
ProtectiveStop
  Id, Symbol, ParentArmedId?
  StopTrigger, StopLimit, DesiredQuantity
  Recurring (default true)
  State: pending_fill -> active -> closed
  PlacedQuantity, LastPlacedSessionDate, LastOrderNo
  LocalBackstopArmedId?
```

`ArmedOrderEvaluator.ShouldFire` already refuses any state other than `armed`, so a stop waiting on
a fill is inert for free — no new suppression logic.

## Lifecycle

### 1. Arm
The dialog's "also protect this with a stop" checkbox creates the parent BUY (`armed`) **and** a
`ProtectiveStop` in `pending_fill`. Nothing is sent to the broker.

### 2. Entry fires
Existing path, unchanged. Parent goes `armed -> firing -> fired`.

### 3. Confirm the fill — holdings delta, corroborated by the book

**The baseline comes first.** A fill is proved by holdings *rising*, so the holding from before the
entry went in is what makes the later reading mean anything: 145 held proves nothing until you know
it was 100. A stop with no baseline refuses to activate (`FillOutcome.NoBaseline`) rather than
assume zero — assuming zero would read a pre-existing 100-share holding as a 100-share fill and
place a stop over stock this entry never bought.

It is captured twice over, because the periodic pass alone leaves a hole:

- **Immediately on arming**, in the background (`CaptureBaselineSoon`). Not awaited — blocking the
  arm request on a page scrape would freeze the dialog for seconds on every arm to close a window
  most orders never enter.
- **Refreshed every pass** while the entry is still `armed`, so the recorded number is the one from
  just before it went in.

Both paths only record while the entry has **not yet gone in**. Recording afterwards would enshrine
the post-fill holding as the "before" figure, making the delta zero and a real fill look like it
never happened.

The residual window is the few seconds while the first read is in flight; an entry that triggers
inside it lands on `NoBaseline` and asks for a person, which is a visible gap rather than a
wrongly-sized sell.

Then poll during market hours
(~2 min, **not** the 30s monitor pass — see *Cost* below):

- **Held qty increased by N** → N shares are real. Stop goes `active` with `DesiredQuantity = N`.
- **Order absent from the outstanding book, holdings unchanged** → the entry died without filling.
  Stop is `closed` with reason "entry never filled". No naked SELL survives.
- **Watch times out** (parent expiry / market close) → same close, logged loudly.

Partial fills protect what is actually owned: the stop is placed for the confirmed quantity, and
`DesiredQuantity` is raised as further fills land, re-placing for the larger size.

**Invariant: a SELL is never placed without a confirmed increase in holdings.** Selling shares you
do not own is a rejection at best and a short at worst.

### 4. Place the native stop
Through `ApprovalGate` and the same `ExecutionMode=BoundedAuto` guard `TakeProfitRetryWorker`
already enforces — a hosted worker must not bypass `ApprovalRequired`.

### 5. Recur each session
The portal drops outstanding orders overnight, so `active` stops are re-placed on the open→ edge
(`IMarketCalendar` is poll-only; the worker detects the transition itself).

Before placing, in order:

1. **Position check.** Held qty is 0 → the position is gone (stop executed, or sold by hand).
   Close the intent, disarm the backstop, notify. Otherwise clamp
   `DesiredQuantity = min(DesiredQuantity, heldQty)`.
2. **Own-record dedup.** `LastPlacedSessionDate == today` → already placed this session.
3. **Book dedup.** Read outstanding rows for the symbol. A resting row matching this stop's price
   and quantity → already protected today.

**Conservative bias: when dedup is ambiguous, do NOT place.** A missing stop is visible in the
panel; a duplicate stop sells the position twice and there is no cancel to undo it.

## The local backstop, and the double-fire it could cause

The user asked for native **plus** a local armed trigger. The gap it covers is real: between the
fill confirming and the next open, or when a placement is rejected, the native stop does not exist.

The risk is equally real — both fire, the position sells twice, and the second sale is a short. So
the local backstop is **conditional**: before firing it re-reads the outstanding book, and stands
down if a native stop for that symbol is resting. It is a gap-filler, never a parallel stop.

This costs one book read at fire time, which is rare and is exactly what `VerifyOrderInBookAsync`
already does.

## Consequences of having no cancel

Stated plainly wherever it applies, because it cannot be engineered away:

- **Disarming an active stop mid-session does not retract a resting native order.** The UI must say
  so and point at the portal. Silently marking it "cancelled" would be a lie about a live sell order.
- The same applies to closing a position by hand while a stop rests.

## Cost

Every broker action serialises on one semaphore (`AhkBroker._gate`), so fill polling queues behind
order placement. Hence the ~2 min cadence, only while a watch is open, only during market hours —
and the recurring pass is once per session, not on a timer.

## Pending — needs a live session with the market open

Both of these are blocked on observing the real portal, not on design. Plan to capture them over a
browser-MCP session during market hours.

### 1. Cancel-order support

The design above assumes no cancel exists, because nothing in `AhkBroker` submits one. The portal
**does** have it — it just has not been captured yet. Once the selectors and confirm flow are
recorded and a `CancelOrderAsync` lands, three things in this document get better:

- **Disarming an active stop can actually retract the resting order**, instead of telling the
  operator to go and cancel it in the portal by hand.
- **Top-ups stop being additive.** With cancel, raising coverage from 30 to 75 replaces one order
  rather than resting a second one for the shortfall — fewer orders, and no chance of a partial
  fill against the first one leaving the pair mis-sized.
- **The local backstop can be retired to a true fallback**, since a native stop that is no longer
  wanted can be pulled rather than worked around.

Until then every "cannot be cancelled from here" warning in the UI and the API is literal and must
stay.

### 2. Verify the outstanding-book columns

`AhkBroker.GetOutstandingOrdersAsync` locates columns by header name, and the names for **side**,
**order type**, and **remaining quantity** are inferred rather than observed — only *Scrip*,
*Price*, and *Order No* are confirmed, from the header list already recorded in
`ReadOrderBookAsync`. Candidates currently tried:

| Field | Header names tried |
| --- | --- |
| side | `side`, `type`, `buy/sell`, `order side`, `trade type` |
| order type | `order type`, `ordertype` |
| remaining | `remaining`, `quantity`, `qty`, `volume` |

A missing column reads as null, which the decision logic treats as ambiguity and declines to act
on — so a wrong guess makes the stop *over-cautious*, never wrong. But over-cautious means a stop
that quietly does not go in, so the real headers should be read off the live grid and pinned.

## Files

| File | Change |
| --- | --- |
| `Watchlist/ProtectiveStop.cs` | new — entity plus pure decision logic (table-testable) |
| `Persistence/*TradingRepository*` | new `protective_stops` table + CRUD |
| `Broker/AhkBroker.cs` | outstanding-book reader returning all rows for a symbol, for dedup |
| `Broker/IBrokerAdapter.cs` | expose holdings + open orders to the worker |
| `Trading/ProtectiveStopWorker.cs` | new — fill watch, native placement, session recurrence |
| `TradingAgentModule.cs` | endpoints + DI |
| `ui/src/ArmOrderDialog.svelte` | attach-stop checkbox and fields |
| `ui/src/ArmedOrdersPanel.svelte` | render stops under their entry |
