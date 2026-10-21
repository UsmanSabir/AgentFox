/**
 * What the chart draws for an edition's overlay MARKERS, as pure functions: the shape a marker asks
 * for, and the lines shown while the pointer is on a bar that carries marker detail. ChartPane owns
 * the drawing; this owns the decisions, so they are tested without a canvas.
 */

export type MarkerShape = 'circle' | 'square' | 'arrowUp' | 'arrowDown';

const SHAPES: readonly MarkerShape[] = ['circle', 'square', 'arrowUp', 'arrowDown'];

/** An unknown or absent shape is a circle, so a newer backend cannot break an older client. */
export function markerShape(shape: string | null | undefined): MarkerShape {
  return SHAPES.includes(shape as MarkerShape) ? (shape as MarkerShape) : 'circle';
}

/**
 * Where a marker is drawn: on its bar (above or below the candle), or in the event strip along the
 * foot of the price pane. An unknown position is above the bar, as it always was.
 */
export function markerPlacement(position: string | null | undefined): 'aboveBar' | 'belowBar' | 'strip' {
  if (position === 'bottom') return 'strip';
  return position === 'belowBar' ? 'belowBar' : 'aboveBar';
}

export interface MarkerDetailLine { text: string; kind: string; }

/**
 * Bar time → the detail lines of every marker on that bar, oldest marker first. A bar can carry
 * several (a weekly bar holding both an ex date and a results day), and all of them are shown.
 * Markers without detail contribute nothing, so a bar with none has no entry and shows no card.
 */
export function markerDetailsByTime(
  markers: readonly { time: number; kind: string; detail?: string | null }[]
): Map<number, MarkerDetailLine[]> {
  const byTime = new Map<number, MarkerDetailLine[]>();
  for (const m of [...markers].sort((a, b) => a.time - b.time)) {
    const text = m.detail?.trim();
    if (!text) continue;
    const lines = byTime.get(m.time) ?? [];
    if (!lines.some(l => l.text === text)) lines.push({ text, kind: m.kind });
    byTime.set(m.time, lines);
  }
  return byTime;
}

/**
 * Where the detail card sits: beside the pointer, flipped to its left when it would run past the
 * plot's right edge, and never above or left of the plot.
 */
export function detailCardPosition(
  point: { x: number; y: number }, plot: { width: number; height: number },
  card = { width: 240, height: 64 }, gap = 14
): { left: number; top: number } {
  const right = point.x + gap;
  const left = right + card.width > plot.width ? point.x - gap - card.width : right;
  const top = Math.min(point.y + gap, plot.height - card.height);
  return { left: Math.max(0, left), top: Math.max(0, top) };
}
