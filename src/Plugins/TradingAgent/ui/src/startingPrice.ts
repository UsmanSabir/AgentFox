/**
 * Whether a percent order's STARTING price has drifted from the live price while the ticket was open.
 *
 * The ticket reads the starting price once, when the order type is chosen, and the server measures the
 * move from whatever the ticket sends. So a ticket left open while the stock moves arms from a stale
 * price: a fixed "sell if it drops 3%" sits 3% under a price the stock has left, and a drop order whose
 * old starting price is far enough above the market is met the moment it is armed.
 *
 * A trail is only harmed in ONE direction: the server adopts any better price at once, so a trailing
 * sell started below the live price simply moves up on the first check. Only a start ABOVE the live
 * price (a trailing buy: below it) arms a trigger closer than the operator asked for.
 *
 * Display only; it never refuses. Pure, so the arithmetic is testable without the dialog.
 */
export interface StartingPriceDrift {
  /** The price the ticket will send. */
  reference: number;
  /** What the stock trades at now. */
  live: number;
  /** The live price already meets the trigger worked out from the starting price. */
  triggersNow: boolean;
}

/**
 * A drift worth saying something about is a QUARTER of the move or more: the trigger is then off by a
 * quarter of the distance the operator chose. Smaller differences are a moving tape, not a stale ticket.
 * A documented constant rather than a setting, because it gates nothing (CLAUDE.md §0.2).
 */
const SHARE_OF_MOVE = 0.25;

export function startingPriceDrift(
  kind: string | null | undefined,
  trailing: boolean,
  reference: number | null | undefined,
  live: number | null | undefined,
  percent: number | null | undefined,
  level: number | null | undefined
): StartingPriceDrift | null {
  if (kind !== 'PercentDrop' && kind !== 'PercentRise') return null;
  if (reference == null || !(reference > 0) || live == null || !(live > 0)) return null;
  if (percent == null || !(percent > 0) || percent > 50) return null;

  const drop = kind === 'PercentDrop';
  const triggersNow = level != null && level > 0 && (drop ? live <= level : live >= level);

  // For a trail, a start on the favourable side of the live price is adopted at once — no harm done.
  const adverse = drop ? reference > live : reference < live;
  if (trailing && !adverse) return null;

  const threshold = Math.max(0.01, reference * (percent / 100) * SHARE_OF_MOVE);
  if (!triggersNow && Math.abs(reference - live) + 1e-9 < threshold) return null;

  return { reference, live, triggersNow };
}
