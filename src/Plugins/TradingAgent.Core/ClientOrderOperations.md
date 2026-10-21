# Recoverable immediate client orders

`TradingManager.ExecuteClientOrderAsync(order, requestId, authorization, ct)` is an explicit opt-in
for one immediate order. Existing dashboard, persistent-order and automation callers keep their
existing execution paths. This adds no HTTP placement route or permission, and enables no native
trading transport. Persistent instructions, attached stops, cancellation and modification require
their own atomic operation associations before using recovery.

Use a fresh `op1_<10-digit Unix UTC seconds>_<32 lowercase random hex digits>` identifier once per
reviewed request, generated with trusted server time and 128 random bits. Retain that exact ID after
a timeout; do not rotate it or automatically retry. The ID is scoped to this account's ledger and is
not authorization. For ApprovalRequired, create the existing single-use approval intent against
`ClientOrderRequestId.SourceMessage(requestId)` and the exact input order/current policy.

The manager snapshots the original request and fingerprints every supplied signal field except its
incidental `Timestamp` (which defaults to now on reconstruction). SELL clamping happens afterward.
Thus unchanged requested maximums remain replays even if custody, effective quantity or policy changes.
Changed input conflicts with a retained operation. Replay is a read of an existing execution, not
permission to act again; every new operation still passes the existing approval, policy, risk,
manual-only, reconciliation and venue-window checks.

`IClientOrderOperationRepository` is an optional capability of the selected `ITradingRepository`.
SqliteTradingRepository implements it. Unsupported repositories refuse the new execution API; there
is no fallback to legacy idempotency. `TryBeginClientOrderOperationAsync` atomically creates the
execution plus its client ID/fingerprint/expiry before any broker submission. The fingerprint is
computed by the manager, never trusted from a caller's hash. A unique index chooses one owner across
concurrent repository instances; changed fingerprints return conflict. Exact lookup is indexed and
does not search a bounded recent-history page or infer broker identities.

No row is claimed for an early gate refusal. A missing row therefore reports only **not recorded**;
it does not establish that the broker did nothing or grant retry permission. Process interruption
after claim leaves `submitting`; lost/failed broker replies retain the existing `unknown` rule.
Neither state can automatically resubmit. Manual resolution retains its distinct
`resolved_placed`/`resolved_not_placed` state; replay does not reinterpret the old unknown result as a
new success. A known broker reply whose persistence failed retains the existing manager semantics.

## Storage, expiry and migration

Three nullable columns extend `trading_executions`: `client_operation_id`, `client_request_hash` and
`client_operation_expires_utc`. Existing rows remain null and are not retrospectively associated.
There is no second ledger, operation table, worker or cache.

New claims accept IDs issued within 24 hours, with at most five seconds of future clock tolerance.
The existing daily TradingRetentionWorker/PruneExecutionsAsync sweep removes terminal executions and
their operation columns after the configured ledger retention (14 days by default), subject to the
existing open-campaign floor. It additionally preserves an operation until its ID expires, even if
an earlier cutoff is requested. Submitting/unknown executions keep their existing explicit-resolution
rule and are not age-expired. Retention can still be disabled through the existing ledger setting.

Lookup and replay may read retained expired records. After an expired record is swept, the same old
ID cannot acquire a new claim. This prevents retention from turning an old replay into a second
submission. A new ID is a new instruction and still requires the caller's explicit review/authority.

Tests use temporary SQLite files and in-process fake brokers only: concurrent claims, changed request
conflicts, history-page independence, restart, schema migration, retention, expiry, write failure,
SELL sizing, approval, in-flight replay and unknown/manual-resolution behaviour.
