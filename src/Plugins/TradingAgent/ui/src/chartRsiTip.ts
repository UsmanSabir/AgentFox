/**
 * The RSI pane's hover tip, as pure functions: which band a value sits in, and where the tip goes.
 * ChartPane owns the drawing; this owns the decisions, so they are tested without a canvas.
 *
 * The tip sits beside the RSI LINE at the hovered bar, not beside the pointer: the crosshair's axis
 * label already reports the pointer's height, and a tip that followed the pointer up and down would
 * read like that label rather than the line's value.
 */

export type RsiZone = 'overbought' | 'oversold' | null;

/** The band the analyzer classifies a value into (its own thresholds, defaults 35/70), or none between. */
export function rsiZone(value: number, oversold: number, overbought: number): RsiZone {
  if (value >= overbought) return 'overbought';
  if (value <= oversold) return 'oversold';
  return null;
}

/**
 * Where the tip sits, in the plot's coordinates. `anchor` is the line's point at the hovered bar;
 * `pane` is the RSI pane's top and the plot's width (the price axis excluded). Right of the line,
 * flipped to its left when it would run past the plot's right edge; above the line, dropped below it
 * when it would cross into the price pane; never left of the plot or below the pane.
 */
export function rsiTipPosition(
  anchor: { x: number; y: number },
  pane: { top: number; height: number; width: number },
  tip = { width: 150, height: 24 }, gap = 12
): { left: number; top: number } {
  const right = anchor.x + gap;
  const left = right + tip.width > pane.width ? anchor.x - gap - tip.width : right;
  const above = anchor.y - gap - tip.height;
  const top = above < pane.top + 2 ? anchor.y + gap : above;
  return {
    left: Math.max(0, left),
    top: Math.min(Math.max(pane.top, top), pane.top + pane.height - tip.height)
  };
}
