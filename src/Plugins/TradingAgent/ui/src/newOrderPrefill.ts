/**
 * Pre-filling the New Order dialog: what a caller may ask for, and how each ask lands on the order
 * composer's own fields. Pure — no Svelte, no I/O — so every rule is testable on its own.
 *
 * The prefill carries ONLY what the composer already models: a side, a size, and one of the order
 * shapes the composer already offers ("At my price", "Wait for a price", "Trailing", or an edition's
 * order type from `orderTypeExtensions.ts`). It adds no order capability and no submission path. The
 * composer still shows every field, and the person still reads the summary and presses its own button;
 * nothing here sends anything.
 *
 * Bad values are DROPPED, not guessed: a number that is not usable is left out so the composer shows its
 * own suggestion for that field, and only a request that cannot be placed at all (no symbol, no side,
 * an unknown shape, a price trigger with no direction) is refused whole.
 */
import type { OrderIntentDefinition } from './api';

export type NewOrderSide = 'BUY' | 'SELL';
/**
 * `limit`: an immediate limit order. `priceTrigger`: wait for a price, then a limit. `trailing`: a trailing limit.
 * `extension`: an edition's order type (`orderTypeExtensions.ts`), opened in its own editor with `conditionSpec`.
 */
export type NewOrderKind = 'limit' | 'priceTrigger' | 'trailing' | 'extension';
export type NewOrderTriggerDirection = 'below' | 'above';

export interface NewOrderPrefill {
  symbol: string;
  action: NewOrderSide;
  kind: NewOrderKind;
  /** Whole shares. */
  quantity?: number;
  /**
   * The limit price (`limit`, `priceTrigger`, and `extension`, whose editor decides whether it uses one). A
   * `priceTrigger` without one limits at its trigger price.
   */
  price?: number;
  /** `priceTrigger`: the price level to wait for. */
  triggerPrice?: number;
  /** `priceTrigger`: wait until the price is at or `below` / at or `above` the trigger. Required there. */
  triggerDirection?: NewOrderTriggerDirection;
  /** `trailing`: percent move from the low (BUY) or high (SELL) that triggers, 0.1 to 50. */
  triggerPercent?: number;
  /** `trailing`: optional worst price — a maximum for a BUY, a minimum for a SELL. */
  priceBound?: number;
  /** "Keep the unfilled remainder working". A trailing order always does, so this is moot there. */
  keepWorking?: boolean;
  /** Days before a waiting or keep-working order expires, 1 to 365 whole days. */
  expiresInDays?: number;
  /** `extension`: the armed-order kind whose editor opens (`OrderTypeExtension.triggerKind`, e.g. `Rule`). Required there. */
  triggerKind?: string;
  /**
   * `extension`: the condition document that editor starts from — opaque here, as it is to core, and checked by the
   * edition's server when it is armed. Required there, at most `MAX_CONDITION_SPEC` characters.
   */
  conditionSpec?: string;
}

/** The longest condition document a prefill carries; the rule document's own limit (docs/rule-orders.md in the edition). */
export const MAX_CONDITION_SPEC = 4096;

/** A request after validation: only usable values remain; the symbol is trimmed and upper-cased. */
export type CleanNewOrderPrefill = NewOrderPrefill;

const positive = (v: unknown): number | null =>
  typeof v === 'number' && Number.isFinite(v) && v > 0 ? v : null;

/** Whole shares above zero, otherwise null. */
export const wholeShares = (v: unknown): number | null =>
  typeof v === 'number' && Number.isInteger(v) && v > 0 ? v : null;

/** The composer's own bounds for a percent trigger and for an expiry. */
export const validTrailPercent = (v: unknown): number | null =>
  typeof v === 'number' && Number.isFinite(v) && v >= 0.1 && v <= 50 ? v : null;
export const validExpiryDays = (v: unknown): number | null =>
  typeof v === 'number' && Number.isInteger(v) && v >= 1 && v <= 365 ? v : null;

/**
 * Validates a caller's prefill. Returns null — the request is a no-op — for a blank symbol, a side that
 * is not BUY or SELL, an unknown kind, or a `priceTrigger` whose direction is not `below` / `above`.
 * Every other field is kept only when usable and only for the kinds that read it.
 */
export function cleanNewOrderPrefill(input: NewOrderPrefill | null | undefined): CleanNewOrderPrefill | null {
  if (!input || typeof input !== 'object') return null;
  const symbol = typeof input.symbol === 'string' ? input.symbol.trim().toUpperCase() : '';
  if (!symbol) return null;
  if (input.action !== 'BUY' && input.action !== 'SELL') return null;
  const kind = input.kind;
  if (kind !== 'limit' && kind !== 'priceTrigger' && kind !== 'trailing' && kind !== 'extension') return null;

  const out: CleanNewOrderPrefill = { symbol, action: input.action, kind };
  if (kind === 'extension') {
    // Both are what makes it this kind; without either there is nothing to open, so the request is refused whole.
    const triggerKind = typeof input.triggerKind === 'string' ? input.triggerKind.trim() : '';
    const spec = typeof input.conditionSpec === 'string' ? input.conditionSpec.trim() : '';
    if (!/^[A-Za-z][A-Za-z0-9]{0,39}$/.test(triggerKind) || !spec || spec.length > MAX_CONDITION_SPEC) return null;
    out.triggerKind = triggerKind;
    out.conditionSpec = spec;
  }
  const quantity = wholeShares(input.quantity);
  if (quantity != null) out.quantity = quantity;

  if (kind === 'priceTrigger') {
    if (input.triggerDirection !== 'below' && input.triggerDirection !== 'above') return null;
    out.triggerDirection = input.triggerDirection;
    const triggerPrice = positive(input.triggerPrice);
    if (triggerPrice != null) out.triggerPrice = triggerPrice;
  }
  if (kind === 'limit' || kind === 'priceTrigger' || kind === 'extension') {
    const price = positive(input.price);
    if (price != null) out.price = price;
  }
  if (kind === 'trailing') {
    const percent = validTrailPercent(input.triggerPercent);
    if (percent != null) out.triggerPercent = percent;
    const bound = positive(input.priceBound);
    if (bound != null) out.priceBound = bound;
  }

  // A trailing order always keeps working; for the others it is the person's choice, and a prefill may
  // only turn it ON (a missing or false value leaves the composer's own default).
  const keepWorking = kind === 'trailing' || input.keepWorking === true;
  if (input.keepWorking === true) out.keepWorking = true;
  // The composer shows an expiry only on a waiting order or a keep-working one.
  const expiry = validExpiryDays(input.expiresInDays);
  if (expiry != null && (kind !== 'limit' || keepWorking)) out.expiresInDays = expiry;
  return out;
}

/**
 * The composer's order choice for a request, found by what the server's intent describes rather than by
 * its id, so a renamed intent still matches. Null when this server does not offer it.
 */
export function intentFor(
  prefill: CleanNewOrderPrefill, intents: readonly OrderIntentDefinition[]
): OrderIntentDefinition | null {
  // An edition's order type is not one of the server's intents: its own editor opens instead.
  if (prefill.kind === 'extension') return null;
  const side = intents.filter(item => item.action === prefill.action && !item.requiresActivationDate);
  if (prefill.kind === 'limit')
    return side.find(item => item.submission === 'immediate' && item.orderType === 'LIMIT' && item.priceField === 'limit') ?? null;
  if (prefill.kind === 'trailing')
    return side.find(item => item.trailing && item.priceField === 'trail-bound') ?? null;
  const trigger = prefill.triggerDirection === 'below' ? 'PriceBelow' : 'PriceAbove';
  return side.find(item => item.submission === 'conditional' && item.priceField === 'trigger-and-limit'
    && item.triggerKind === trigger) ?? null;
}

/**
 * Whether the composer offers "keep the unfilled remainder working" for this order: any non-market
 * order. Decided from the intent itself, because the composer's own reactive flag has not been
 * recomputed yet in the same synchronous call that chooses the intent.
 */
export const canKeepWorking = (intent: OrderIntentDefinition | null): boolean =>
  intent != null && intent.orderType !== 'MARKET';

/** What to put in the composer's own state, field for field. Null means "leave the composer's default". */
export interface ComposerPrefillState {
  side: NewOrderSide;
  intent: OrderIntentDefinition | null;
  quantity: number | null;
  price: number | null;
  triggerPrice: number | null;
  triggerPercent: number | null;
  priceBound: number | null;
  persistentUntilFilled: boolean;
  expiresInDays: number | null;
}

/**
 * Pure mapping from a validated request to the composer's initial state, as a person would set it by
 * hand: choose the side, choose the order shape, then type the numbers.
 *  - limit:        "At my price"       -> `price`
 *  - priceTrigger: "Wait for a price"  -> "At or below / At or above" variant, `triggerPrice`, and the
 *                  limit `price` (its own trigger price when none is given, as the composer defaults)
 *  - trailing:     "Trailing"          -> `triggerPercent` (the Trail %), `priceBound` (max buy / min
 *                  sell), and keep-working, which the composer forces on
 */
export function composerStateFor(
  prefill: CleanNewOrderPrefill, intents: readonly OrderIntentDefinition[]
): ComposerPrefillState {
  const intent = intentFor(prefill, intents);
  const trigger = prefill.kind === 'priceTrigger' ? prefill.triggerPrice ?? null : null;
  const price = prefill.kind === 'trailing' ? null : prefill.price ?? trigger;
  return {
    side: prefill.action,
    intent,
    quantity: prefill.quantity ?? null,
    price,
    triggerPrice: trigger,
    triggerPercent: prefill.kind === 'trailing' ? prefill.triggerPercent ?? null : null,
    priceBound: prefill.kind === 'trailing' ? prefill.priceBound ?? null : null,
    persistentUntilFilled: canKeepWorking(intent) && (prefill.kind === 'trailing' || prefill.keepWorking === true),
    expiresInDays: prefill.expiresInDays ?? null
  };
}
