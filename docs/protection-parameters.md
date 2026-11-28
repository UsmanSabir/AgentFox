# Protection parameters

Generated from `TradingAgent.Config.ProtectionParameters` — edit the catalog, not this file.
Each is set under `Plugins:TradingAgent` (appsettings or environment, e.g. `Plugins__TradingAgent__DailyLossLimitPct`).
Where a Settings key is listed, the value saved in Settings overrides the startup value without a restart.

## Trading stop

| Parameter | Default | Unit | Settings key | Meaning |
|---|---|---|---|---|
| `TradingStop` | `Off` | Off / EntriesOff / FullStop | `tradingStop` | EntriesOff refuses buys and keeps every sell (stops, take-profits, exits) working; FullStop sends nothing. |
| `KillSwitch` | `false` | true / false | `killSwitch` | Legacy emergency stop: true is FullStop. |
| `DailyLossLimitPct` | `3` | % of start-of-day value | `dailyLossLimitPct` | The day's loss at which new buys switch off until the next session. 0 = off. |
| `DailyLossLimitPkr` | `0` | PKR | `dailyLossLimitPkr` | The same as an amount; the first limit reached applies. 0 = off. |

## Protective stops

| Parameter | Default | Unit | Settings key | Meaning |
|---|---|---|---|---|
| `ProtectiveStopPollMinutes` | `1` | minutes | — | Time between stop passes inside a session. |
| `ProtectiveStopLiveCoverage` | `Shadow` | Off / Shadow / On | — | Read coverage from the book order by order, re-placing a stop whose order left it mid-session. |
| `ProtectiveStopPropagationSeconds` | `60` | seconds | — | After a placement, how long an order may be missing from the book and still count as covering. |
| `ProtectiveStopAbsenceConfirmSeconds` | `60` | seconds | — | How long an order missing with no recorded reason must stay missing before it counts as gone. |
| `ProtectiveStopMissedGraceSeconds` | `120` | seconds | — | How long a triggered stop may rest unfilled before it is reported missed (and sold at market if opted in). |
| `ProtectiveStopRealertMinutes` | `15` | minutes | — | Repeat interval for 'stop refused' alerts while the stop stays unplaced. 0 = once a session. |
| `ProtectiveStopCatastrophicPercent` | `5` | % below the trigger | — | Default catastrophic level; a position plan may set its own. |
| `StopLimitOffsetPct` | `1` | % below the trigger | — | Default distance of a stop-limit's limit below its trigger; a position plan may set its own. |

## Exit rules

| Parameter | Default | Unit | Settings key | Meaning |
|---|---|---|---|---|
| `ExitRulesAction` | `Propose` | Off / Propose / Act | `exitRulesAction` | What a position plan's exit rules do when they fire: propose a sale you approve, or arm it. |

## Halts

| Parameter | Default | Unit | Settings key | Meaning |
|---|---|---|---|---|
| `MarketHaltHandling` | `Shadow` | Off / Shadow / On | — | Gate the workers on a detected market halt and its reopen. |
| `HaltIndexSymbol` | `KSE30` | index symbol | — | The index whose move starts a halt. |
| `HaltIndexMovePercent` | `5` | % from previous close | — | The index move, either way, that starts a halt. |
| `HaltMaxMinutes` | `90` | minutes | — | How long a halt may go unconfirmed-ended before it is Unknown. |
| `ReopenBackstopHoldMinutes` | `10` | minutes | — | After a reopen, how long backstops and breached stops wait unless the price is below the catastrophic level. |
| `MarketStatePollSeconds` | `15` | seconds | — | Time between market-state evaluations inside a session. |

## Reconciliation

| Parameter | Default | Unit | Settings key | Meaning |
|---|---|---|---|---|
| `RequireReconciliationHealthy` | `true` | true / false | — | Entries need a healthy, recent account read; sells need only a fresh book read. |
| `ReconciliationIntervalSeconds` | `60` | seconds | — | Time between account reads. |
| `ReconciliationMaxAgeSeconds` | `180` | seconds | — | How old an account read may be for an entry. |
| `ReduceOnlyBookMaxAgeSeconds` | `60` | seconds | — | How old the book read behind a sell may be. |

## Audit

| Parameter | Default | Unit | Settings key | Meaning |
|---|---|---|---|---|
| `LedgerRetentionDays` | `400` | days | — | How long orders, their order events and broker answers, stop order rows and shadow decisions are kept. 0 = forever. |
| `ReconciliationRetentionDays` | `30` | days | — | How long account snapshots are kept. 0 = forever. |

## Dead-man

| Parameter | Default | Unit | Settings key | Meaning |
|---|---|---|---|---|
| `Watchdog.HeartbeatUrl` | `(empty)` | URL | — | Where heartbeats go. Empty = off. |
| `Watchdog.IntervalSeconds` | `60` | seconds | — | Time between heartbeats inside a session. |
| `Watchdog.FeedSilentSeconds` | `120` | seconds | — | No price from any source for this long counts as feed-silent. |
