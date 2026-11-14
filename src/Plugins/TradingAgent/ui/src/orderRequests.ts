/**
 * Order requests: a neutral way for a sibling component to ask the dashboard to OPEN one of its own
 * existing dialogs, pre-filled.
 *
 * WHY THIS EXISTS. A host panel that works out a price level (an exit, a re-entry, an alert) wants to
 * hand it to the user as a ready-to-confirm order, not as numbers to retype. The dashboard already
 * has the right dialogs — the conditional-order dialog (`ArmOrderDialog`) and the price-alert dialog
 * — and the chart already opens them from a clicked level. This store is the same door for a caller
 * that is not the chart.
 *
 * WHAT IT DOES NOT DO. It submits nothing and decides nothing. The dialog opens with the prefill, shows
 * every field for the user to read and edit, and sends only when the user presses ITS button, through
 * the same server review and approval it always had. There is no "send" request type.
 * A community build never calls either function, so nothing here runs there.
 *
 * MECHANISM. One pending request, memory only. `TradingSurface` consumes it as soon as it sees it. A
 * request nobody consumes within `REQUEST_TTL_MS` is dropped, so a call made while no dashboard is
 * mounted cannot open a dialog minutes later, over something unrelated.
 */
import { get, writable, type Readable } from 'svelte/store';
import type { ArmOrderDialogContext } from './api';

export type PriceAlertDirection = 'above' | 'below';

export type OrderRequest =
  | { kind: 'arm'; prefill: ArmOrderDialogContext }
  | { kind: 'priceAlert'; symbol: string; target: number; direction: PriceAlertDirection };

export interface PendingOrderRequest {
  /** Increases with every request, so two identical requests in a row are still two events. */
  seq: number;
  /** When it was made (ms since the epoch). */
  atMs: number;
  request: OrderRequest;
}

/** How long an unconsumed request stays valid. */
export const REQUEST_TTL_MS = 15_000;

const pending = writable<PendingOrderRequest | null>(null);
let seq = 0;

/** The pending request, if any. `TradingSurface` subscribes; hosts normally need not. */
export const orderRequests: Readable<PendingOrderRequest | null> = { subscribe: pending.subscribe };

const cleanSymbol = (symbol: string | null | undefined) => (symbol ?? '').trim().toUpperCase();

/**
 * Asks the dashboard to open the conditional-order dialog with `prefill` (the same shape a clicked
 * chart level uses; see `ArmOrderDialogContext`). Returns false, doing nothing, without a symbol.
 */
export function requestArmOrder(prefill: ArmOrderDialogContext, nowMs = Date.now()): boolean {
  const symbol = cleanSymbol(prefill.symbol);
  if (!symbol) return false;
  pending.set({ seq: ++seq, atMs: nowMs, request: { kind: 'arm', prefill: { ...prefill, symbol } } });
  return true;
}

/**
 * Asks the dashboard to open the price-alert dialog for `symbol` at `target`, alerting when the price
 * goes `direction` of it. Returns false, doing nothing, for a blank symbol or a non-positive target.
 */
export function requestPriceAlert(
  symbol: string, target: number, direction: PriceAlertDirection, nowMs = Date.now()
): boolean {
  const clean = cleanSymbol(symbol);
  if (!clean || !Number.isFinite(target) || !(target > 0)) return false;
  pending.set({
    seq: ++seq, atMs: nowMs,
    request: { kind: 'priceAlert', symbol: clean, target, direction: direction === 'below' ? 'below' : 'above' }
  });
  return true;
}

/** Pure: a caller-supplied starting size is whole shares above zero, otherwise null (ignored). */
export function validArmQuantity(quantity: unknown): number | null {
  return typeof quantity === 'number' && Number.isInteger(quantity) && quantity > 0 ? quantity : null;
}

/** Pure: whether a pending request is still within its lifetime. */
export function isFresh(p: PendingOrderRequest, nowMs: number): boolean {
  return nowMs - p.atMs >= 0 && nowMs - p.atMs <= REQUEST_TTL_MS;
}

/**
 * Takes the pending request, if it is still fresh, and clears it — so exactly one consumer acts on it.
 * A stale one is cleared and ignored.
 */
export function takeOrderRequest(nowMs = Date.now()): OrderRequest | null {
  const current = get(pending);
  if (!current) return null;
  pending.set(null);
  return isFresh(current, nowMs) ? current.request : null;
}
