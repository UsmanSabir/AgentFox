/**
 * The price a percent trigger works out to, computed the way the server computes it: the backend's
 * `PercentTrigger.Level`, after the arm endpoint has rounded the percent.
 *
 * The server works in `decimal`: it rounds the percent to 2 places, multiplies, and rounds the level
 * to the paisa, halves AWAY from zero. The browser used binary floating point on the unrounded percent,
 * so on some prices the level shown differed from the level armed by one paisa — 2.555% read as 2.555
 * here and 2.56 there, and a product landing on a half paisa could round either way. Here every step
 * is done in whole units, so both sides land on the same number.
 *
 * Pure, so it is testable without the dialog.
 */
export function percentLevel(
  kind: string, reference: number | null, percent: number | null
): number | null {
  if (kind !== 'PercentDrop' && kind !== 'PercentRise') return null;
  if (reference == null || !(reference > 0)) return null;
  if (percent == null || !(percent > 0) || percent > 50) return null;

  // Percent to hundredths of a percent, rounded half away from zero (the arm endpoint's rounding).
  const hundredths = whole(percent * 100);
  if (hundredths <= 0) return null;

  // The reference to 1/10,000 of a rupee, which carries any price the feed quotes exactly.
  const units = whole(reference * 10_000);
  const factor = kind === 'PercentDrop' ? 10_000 - hundredths : 10_000 + hundredths;

  // units x factor is in 1e-8 rupees; to paisa (1e-2) is a division by 1e6, halves away from zero.
  // Both are exact integers well under 2^53 for any PSX price, so the floor is exact.
  const paisa = Math.floor((units * factor + 500_000) / 1_000_000);
  return paisa > 0 ? paisa / 100 : null;
}

/** Rounds a positive scaled value to an integer, half away from zero, ignoring binary noise. */
function whole(value: number): number {
  return Math.round(Number(value.toFixed(6)));
}
