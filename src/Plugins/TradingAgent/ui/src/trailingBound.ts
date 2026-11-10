/**
 * Whether a trailing order's optional price bound can ever let it trade, as things stand now.
 *
 * A trailing SELL triggers at a level BELOW the high it has seen, and its minimum refuses any trigger
 * below the minimum. Both must hold at once, so a minimum ABOVE the current trigger means no drop can
 * sell until the high climbs far enough to lift the trigger to the minimum — every move before that
 * is skipped. The mirror holds for a trailing BUY and its maximum. That is sometimes intended ("only
 * take profit above 82"), so the dialog WARNS rather than refusing; what it must not do is let the
 * operator believe they are protected from a fall when nothing will sell. Measured 2026-11-10: a LUCK
 * trailing sell armed with a minimum of 545 against a trigger near 444 could not have sold at all.
 *
 * Pure, so the arithmetic is testable without the dialog.
 */
export interface TrailBoundBlock {
  /** The trigger as it stands now. */
  trigger: number;
  /** The bound that blocks it. */
  bound: number;
  /** The high (SELL) or low (BUY) the trail must reach before the trigger clears the bound. */
  neededExtreme: number;
}

/**
 * Null when the bound does not block the current trigger (or any input is missing). Inclusive at the
 * bound, exactly as the server's `TrailingPriceBound.Allows` is: a trigger equal to the bound trades.
 */
export function trailBoundBlock(
  action: string | null | undefined,
  trigger: number | null | undefined,
  bound: number | null | undefined,
  trailPercent: number | null | undefined
): TrailBoundBlock | null {
  if (trigger == null || !(trigger > 0)) return null;
  if (bound == null || !(bound > 0)) return null;
  if (trailPercent == null || !(trailPercent > 0) || trailPercent > 50) return null;

  const buy = String(action ?? '').toUpperCase() === 'BUY';
  const blocked = buy ? bound < trigger : bound > trigger;
  if (!blocked) return null;

  // The extreme whose trigger reaches the bound: high × (1 − p) ≥ minimum, or low × (1 + p) ≤ maximum.
  // Rounded AWAY from the current price to the paisa, so "or more" / "or less" is never one paisa short.
  const raw = buy ? bound / (1 + trailPercent / 100) : bound / (1 - trailPercent / 100);
  const neededExtreme = buy
    ? Math.floor(raw * 100 + 1e-9) / 100
    : Math.ceil(raw * 100 - 1e-9) / 100;

  return { trigger, bound, neededExtreme };
}
