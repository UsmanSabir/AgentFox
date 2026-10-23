# Recoverable immediate client orders

`TradingManager.ExecuteClientOrderAsync(order, requestId, authorization, ct)` is an explicit opt-in
for one immediate order. Existing dashboard, persistent-order and automation callers keep their
existing execution paths. This adds no HTTP placement route or permission, and enables no native
trading transport. Persistent instructions, attached stops and modification require their own atomic
associations before using recovery. Cancellation now has the separate typed path described below.

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
`client_operation_expires_utc`. `client_operation_kind` now defaults to `order` for existing rows;
it distinguishes a newly claimed `cancel`. Existing rows remain unassociated (null ID).
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

## Recoverable cancellation

`ClientCancellationService.ExecuteAsync(request, requestId, verifiedScope, actor, editionOwners, ct)`
is an opt-in trusted-service API, not an HTTP endpoint or authority grant. Its caller must verify
current trading permission, review/consent, selected account and immutable target before calling it.
`verifiedScope` must be server-derived and identify that selected broker/account; never take it from
client JSON. The cancellation service preserves fresh broker reads, managed-stop/keep-working routing,
edition consequences and acknowledgement. Risk-removing cancellation retains its existing behavior
during the kill switch; no submission policy gate is added.

Optional `ExpectedTarget` and `ExpectedOwners` bind a reviewed cancellation to the fresh dispatch
evidence. Changed side, price, order type or owner/consequence requires another review. A partial fill
may reduce remaining quantity without changing the intended target. Only broker-reported primary /
alternate identity is used; duplicate matching broker rows are refused. This does not prove a fill
alias for replacement. The service never places a replacement or interprets a vanished order as a
confirmed cancellation.

`IClientActionOperationRepository` is optional and shares the existing execution row and unique op1
namespace with orders. A kind mismatch conflicts even if supplied hashes match. The service clones
the reviewed owner list, hashes the exact normalized target, acknowledgement, scope and actor, and
commits the claim before dispatch. Replays, process restart, changed requests and a lost result write
cannot dispatch again. Unsupported repositories refuse without legacy fallback.

The cancellation request envelope (<=16 KiB) and sanitized result are kept on that execution row.
They share existing daily retention, operation expiry, campaign floor and unknown/submitting
preservation. No second table, background task or retry queue is added. Raw broker response/message
text is not copied into the result. Cancellation `accepted` means confirmed cancellation; its separate
`cancellationOutcome` must be read with the kind. Gone/filled/refused outcomes are distinct. Pending
or interrupted results remain unknown; a failed result save leaves the durable row submitting.

Manual resolution is kind-specific and still requires a broker-check note and compare-and-set audit:
orders use placed / not_placed, cancellations use cancelled / not_cancelled / filled. Original results
remain intact. Wrong-kind resolutions fail; a resolved cancellation cannot become a placed order.
The trading UI exposes matching labels. Submitting records never become permission to retry; they
require operator investigation. No market-time or age rule silently clears uncertainty.

## Reviewed modification parent

`ClientModificationService.CancelOriginalAsync` claims a typed `modify` parent in the same request-ID namespace before its authorized caller can attempt cancellation. The caller supplies server-reviewed terms, verified scope and actor; these are fingerprinted before IO, never accepted as approval authority. The callback must use the existing ownership-aware cancellation path. Parent replay/restart only returns the recorded state, including when cancellation failed or its reply/result write was lost.

Deterministic cancellation/replacement child IDs retain the parent's original 24-hour new-claim deadline. A cancelled parent is labelled `cancelled-awaiting-review`, with executed=false: it is neither a placed replacement nor a fill. This service never calls order execution. Replacement still requires a separately confirmed exact child order, current Core policy/risk/approval, confirmed original cancellation and complete fresh post-cancellation fill reconciliation. Gone/unknown/manual-resolution states do not establish that permission.

The existing execution row holds <=16 KiB of parent binding (review terms <=12 KiB), no new table or worker. Existing client-operation expiry and execution retention apply; uncertain rows retain the explicit-reconciliation posture. Repository lookups now include server-only RequestJson for rebuilding a review after restart. HTTP projections must continue to omit request/result JSON, scope, actor, raw references and fingerprints. No new HTTP endpoint or live action is introduced by this Core seam.

Post-cancellation evidence is retained in the same parent result: <=64 exact execution IDs with quantity, price, occurrence and remaining amount, total result <=16 KiB. `IClientModificationOperationRepository` compares the previous result, accepted state and modify kind atomically. `RememberPostCancellationFillsAsync` refuses a later source that loses or changes a recorded execution; concurrent readers cannot overwrite newer evidence with an older set. A failed progress save refuses the caller and attempts to mark the parent unknown under a bounded independent persistence deadline. This capability is required before a modification parent can cancel anything.

Read/recording of post-cancel evidence submits no order and grants no approval. Callers must still verify broker/account scope, complete fresh sources, cancellation confirmation and remaining quantity. Serialize replacement review/confirmation for the selected agent so a concurrent review cannot publish newer evidence while another confirmation dispatches. HTTP projections expose only safe aggregate state; raw stored executions stay server-side. Existing parent-row retention owns the bounded extra result array, with no new store or sweeper.
