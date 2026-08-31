# TradingAgent maintenance invariants

## Dashboard order choices

- `Trading/OrderIntentRegistry.cs` is the single registry for the dashboard's layman-facing **New Order** choices. Do not hard-code a second list of broker order types or intent descriptions in Svelte.
- Every registry choice must map to an order type accepted by `TradingRiskEngine` and must say whether it is `immediate` or `conditional`. Immediate choices go through `TradingManager`; conditional choices go through the durable armed-order endpoint. Neither path may bypass policy, reconciliation, market-window, risk, idempotency, approval, or kill-switch checks.
- When adding a broker order type or armed trigger, update the registry, API contract, risk validation, broker projection, plain-language description, prefill rules, and registry tests together. Disabled capabilities stay visible with an explanation; never silently reinterpret them as another order type.
- Keep existing expert entry points (chart levels, alert actions, armed-order dialog, proposal actions) when changing the consolidated flow. **New Order** is an additional simple entry point, not a replacement.
- Prefills are editable starting values, never recommendations. Show the resulting order as a plain-language sentence before submission and require explicit quantity.

## Manual-only symbols

- Manual-only is a **deny list for automation only**, never for the operator. It must not be pushed down into `TradingRiskEngine`, and `AllowedSymbols` must not be used to express it: that list decides whether an order may exist at all, so narrowing it bans the dashboard along with the workers. Two different questions, two different places.
- The effective set is `ManualOnlySymbols` (config) **union** every watchlist row with `auto_trade_enabled = 0`. Both halves may only add. Never give the API a way to clear a configured pin — config is the durable floor, and the runtime toggle is safe *because* it can only narrow.
- Keep the check in both places. `ApprovalGate.Decide` refuses early, and must stay **above** the `BoundedAuto` short-circuit — below it the check is dead in the mode most likely to fire unattended. `TradingManager.ExecuteGroupsAsync` then re-asks authoritatively, so a caller that never consults the gate (retry worker, strategy, anything new) is still refused.
- The boundary test is `ExecutionAuthorization.MayTradeManualOnly` — **attended** (a human said yes to *that* order) **or operator-originated** (a human wrote the standing instruction being carried out: an armed order they armed, its attached stop, the persistent day-order lifecycle re-placing it). Manual-only is about who *originates* an order, not who is watching when it executes; refusing the operator's own armed orders turned the flag into "you may not use armed orders on this symbol". Both fields default to false, so a caller that claims neither is denied by omission. Do not mark an automated caller `HostToolGate`/`Attendant`/`StandingInstruction` to get an order through; use `PreAuthorized` for standing *policy* permission, which is not the operator's instruction and stays refused.
- A manual-only symbol keeps every bit of its analysis: charts, scans, alerts, archive. Do not "tidy" it out of the monitoring or archive universes — muting is what `alerts_enabled` is for, and a hand-managed name usually wants louder alerts, not quieter.
- No **strategy** raises a stop or arms a take-profit for these symbols. That is the requested behaviour, not an oversight; if you add an exit automation, it must respect the deny set, and any option to exempt exits has to be explicit and off by default. A stop or take-profit the *operator* placed or armed is theirs and keeps working — carry `OperatorOriginated` wherever such an instruction is stored, and never default it to true.

## Alerts and operational status

- Alert "delete" is a dismissal/soft delete. Keep the persisted audit row and expose it through **show dismissed** until `Monitor.RetentionDays` expires. `TradingRetentionWorker` owns daily pruning independently of monitor/market state; do not move retention back into a market-hours branch. SQLite reuses the freed pages even when its file does not immediately shrink. Bulk mutations belong in repository methods so select-all is not limited to the first UI page.
- Bulk cancellation is appropriate for local waiting triggers, but do not imply it cancels a native order already resting at the broker.
- Reconciliation must remain unhealthy when the complete broker snapshot cannot be read. A missing broker session is genuine unavailability, but the UI should label it **Waiting for broker** rather than suggesting corrupt data.
- An execution in `unknown` state means broker submission may have happened but no reliable reply returned. Never auto-retry, time-expire, or clear it merely to improve the dashboard metric. Resolution must be an explicit broker-side check through `ResolveUnknownExecutionAsync`, with a required note and atomic audit event; use `resolved_placed` or `resolved_not_placed` so the original uncertainty is not rewritten as an ordinary result.
- The periodic reconciliation pass is passive and must not trigger repeated broker logins. A user-initiated **Check broker now** may call `AhkPortalClient.EnsureSessionAsync` once and then `BrokerReconciliationWorker.RunNowAsync`; keep the worker single-flight and share the singleton between its timer and the endpoint.
- During market hours, `1D` alerts intentionally use today's open candle for prompt detection. Keep the per-alert caveat and the panel explanation that the signal can change before close; do not silently present an open-candle transition as close-confirmed.

## Broker account dashboard

- The dashboard account endpoint and `PortfolioPanel.svelte` consume `IBrokerAccountReader` and the broker-neutral `BrokerAccountSnapshot`. Broker adapters translate their native balance, position, and order-book payloads into the common fields and preserve provider-only fields in `Attributes`; never make the Svelte panel depend on AHK DTOs.
- Preserve `BalancesAvailable`, `HoldingsAvailable`, and `OrdersAvailable` separately from list counts. An unreadable section is unknown, not empty—especially the working order book.
- The portfolio panel is privacy-first: values start masked, the account is not read merely because the dashboard loaded, and refresh is explicit. New numeric account fields must obey the same show/hide control.

## Verification

- Run `npm run check` and `npm run build` in `ui/` for dashboard changes.
- Run the focused `AgentFox.ChannelTests` tests for registry, alerts, armed orders, risk, and trading-manager changes, then build `TradingAgent.csproj`.
