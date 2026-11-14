/**
 * Chart annotations: a neutral store a sibling component can write and the chart reads.
 *
 * WHY THIS EXISTS. A host built on this dashboard sometimes wants to draw on the chart a stock is
 * showing — a few horizontal levels, a shaded price band, an arrow at a past bar — from a panel that
 * lives beside the chart and recomputes as the user moves a control. The server overlay contract
 * (`ChartOverlays`) cannot do that: it is fetched with the candles, refetched on symbol/interval/tick,
 * and hidden by the Levels toggle. So this is the CLIENT-side counterpart: write here, the chart
 * redraws instantly, nothing is fetched.
 *
 * WHAT IT IS NOT. It carries no vocabulary from any edition and no colours. `kind` is the same
 * SEMANTIC token the server overlays use ('target', 'stop', 'entry', 'support', 'resistance',
 * 'projection', 'circuit', 'neutral', ...) and the chart maps it to a theme token, so light and dark
 * both stay legible and a caller cannot take over the palette. An unknown kind draws as neutral.
 * A community build never calls `setChartAnnotations`, so the store stays empty and the chart is
 * byte-for-byte unchanged.
 *
 * WHAT THE CHART DOES WITH IT (ChartPane):
 *  - draws it as a third group, apart from server overlays and the level lines: always drawn (the
 *    Levels toggle does not hide it) and EXCLUDED from autoscale, so a far-away line never squashes
 *    the candles. A line outside the visible price range simply is not on screen; see `offscreen`.
 *  - only for the symbol the chart is showing; a symbol change draws nothing of another symbol's.
 *  - memory only. Owners are expected to clear their own entry (`null`) when they stop caring.
 *
 * Several owners may annotate one chart at once; each owner's set is independent and replaced whole
 * by its next `setChartAnnotations`.
 */
import { derived, get, writable, type Readable } from 'svelte/store';

/** A semantic kind, mapped to a theme token by the chart. Never a colour. */
export type AnnotationKind = string;

export interface AnnotationLevel {
  /** The price the horizontal line sits at. */
  price: number;
  /** Shown on the price axis chip. Keep it short; near-coincident labels are merged on the chart. */
  label: string;
  kind: AnnotationKind;
  /** Line width 1..3; default 1. Out-of-range values are clamped. */
  weight?: number;
  /** Dashed when true (default solid). */
  dashed?: boolean;
}

/**
 * A translucent rectangle between two prices, across the plot.
 *
 * `fromTime` (UTC seconds, the chart's own bar time) lets ONE zone read as "history, then forecast":
 * it is drawn fainter over bars before `fromTime` and at full strength from there on. Omitted, the
 * whole zone is drawn at full strength. (Two zones with different kinds work as well; this is the
 * simple way to ask for the faint/strong split.)
 */
export interface AnnotationZone {
  priceFrom: number;
  priceTo: number;
  label?: string;
  kind: AnnotationKind;
  fromTime?: number;
}

export type AnnotationMarkerShape = 'arrowUp' | 'arrowDown' | 'circle';

export interface AnnotationMarker {
  /** The bar's UTC seconds. A time with no bar draws nothing. */
  time: number;
  position: 'above' | 'below';
  shape: AnnotationMarkerShape;
  label?: string;
  /** One line shown in the chart's hover card while the pointer is on that bar. */
  detail?: string;
  kind: AnnotationKind;
}

export interface ChartAnnotations {
  levels?: AnnotationLevel[];
  zones?: AnnotationZone[];
  markers?: AnnotationMarker[];
}

/** One owner's set, bound to the symbol it was written for. */
export interface OwnedAnnotations {
  symbol: string;
  annotations: ChartAnnotations;
}

/** Bounds, so a runaway writer cannot make the chart grind. Excess entries are dropped, not thrown. */
export const MAX_LEVELS = 12;
export const MAX_ZONES = 6;
export const MAX_MARKERS = 24;

const normaliseSymbol = (symbol: string | null | undefined) => (symbol ?? '').trim().toUpperCase();

const finite = (n: unknown): n is number => typeof n === 'number' && Number.isFinite(n);

/**
 * Drops anything the chart could not draw (non-finite prices or times) and enforces the bounds.
 * Pure, so a bad writer is handled once, here, rather than in the draw code.
 */
export function sanitiseAnnotations(a: ChartAnnotations): ChartAnnotations {
  return {
    levels: (a.levels ?? []).filter(l => finite(l.price) && l.price > 0).slice(0, MAX_LEVELS),
    zones: (a.zones ?? [])
      .filter(z => finite(z.priceFrom) && finite(z.priceTo) && z.priceFrom > 0 && z.priceTo > 0
        && z.priceFrom !== z.priceTo)
      .slice(0, MAX_ZONES),
    markers: (a.markers ?? []).filter(m => finite(m.time)).slice(0, MAX_MARKERS)
  };
}

const owners = writable<Record<string, OwnedAnnotations>>({});

/**
 * Sets (or with `null` clears) one owner's annotations for a symbol. Replaces that owner's previous
 * set whole, whatever symbol it was for. An empty set clears the owner as well, so a panel that has
 * nothing to draw cannot leave an empty entry behind.
 */
export function setChartAnnotations(owner: string, symbol: string, a: ChartAnnotations | null): void {
  const key = owner.trim();
  if (!key) return;
  owners.update(current => {
    const next = { ...current };
    const clean = a ? sanitiseAnnotations(a) : null;
    const empty = !clean
      || ((clean.levels?.length ?? 0) + (clean.zones?.length ?? 0) + (clean.markers?.length ?? 0)) === 0;
    if (empty || !normaliseSymbol(symbol)) delete next[key];
    else next[key] = { symbol: normaliseSymbol(symbol), annotations: clean! };
    return next;
  });
}

/** Every owner's current set. The chart subscribes to this; hosts normally need not. */
export const chartAnnotationOwners: Readable<Record<string, OwnedAnnotations>> = { subscribe: owners.subscribe };

/** Pure: each owner's annotations for one symbol, in owner-key order. Other symbols' are excluded. */
export function annotationsForSymbol(
  all: Record<string, OwnedAnnotations>, symbol: string | null | undefined
): { owner: string; annotations: ChartAnnotations }[] {
  const wanted = normaliseSymbol(symbol);
  if (!wanted) return [];
  return Object.keys(all).sort()
    .filter(owner => all[owner].symbol === wanted)
    .map(owner => ({ owner, annotations: all[owner].annotations }));
}

/** The readable store the chart uses: this symbol's owners, redrawn whenever any owner writes. */
export function chartAnnotationsFor(symbol: Readable<string | null>): Readable<{ owner: string; annotations: ChartAnnotations }[]> {
  return derived([owners, symbol], ([$owners, $symbol]) => annotationsForSymbol($owners, $symbol));
}

// ── Off-screen counts ───────────────────────────────────────────────────────

/** How many of an owner's levels/zones sit outside the chart's visible price range. */
export interface OffscreenCount { above: number; below: number; }

const offscreen = writable<Record<string, OffscreenCount>>({});

/**
 * Per owner: how many drawn levels/zones are above or below the visible price range, so the owner's
 * own panel can show a "2 below view" chip. Written by the chart (not by hosts), refreshed after each
 * draw and when the visible range moves; it does NOT follow a manual drag of the price axis, and it
 * also goes stale when a live tick rescales the price axis until the next draw or range change. An
 * owner with nothing drawn has no entry.
 *
 * ONE chart writes these. Counts are keyed by owner only, so two mounted ChartPanes showing the same
 * owner's symbol would overwrite each other's numbers; the dashboard mounts a single chart at a time.
 */
export const annotationOffscreen: Readable<Record<string, OffscreenCount>> = { subscribe: offscreen.subscribe };

/** Chart-side writer for `annotationOffscreen`. `null` removes the owner. Skips no-op updates. */
export function reportAnnotationOffscreen(owner: string, count: OffscreenCount | null): void {
  // A svelte store treats any object as changed, so an unchanged report is filtered here: the chart
  // refreshes counts on every visible-range move and its owners should not re-render for nothing.
  const current = get(offscreen);
  const previous = current[owner];
  if (!count) {
    if (!previous) return;
    const next = { ...current };
    delete next[owner];
    offscreen.set(next);
    return;
  }
  if (previous && previous.above === count.above && previous.below === count.below) return;
  offscreen.set({ ...current, [owner]: count });
}

/**
 * Pure: counts levels and zones outside `[low, high]`. A zone counts only when it lies wholly above
 * or below; one that overlaps the view at all is on screen. Unknown range counts nothing.
 */
export function countOffscreen(
  levels: readonly { price: number }[],
  zones: readonly { priceFrom: number; priceTo: number }[],
  low: number | null, high: number | null
): OffscreenCount {
  const out: OffscreenCount = { above: 0, below: 0 };
  if (low == null || high == null || !(high > low)) return out;
  for (const l of levels) {
    if (l.price > high) out.above++;
    else if (l.price < low) out.below++;
  }
  for (const z of zones) {
    const lo = Math.min(z.priceFrom, z.priceTo), hi = Math.max(z.priceFrom, z.priceTo);
    if (lo > high) out.above++;
    else if (hi < low) out.below++;
  }
  return out;
}

// ── Label merging ───────────────────────────────────────────────────────────

export interface LevelLabelPlan {
  /** The title to put on this level's line ('' when its label was merged into a neighbour's). */
  title: string;
  /** Whether this line shows an axis chip; false for the lines whose label was merged away. */
  axisLabelVisible: boolean;
}

/**
 * Pure: merges the labels of levels whose axis chips would overlap. `yOf` maps a price to its pixel
 * row (null when unknown); levels closer than `minGapPx` rows form a group, and the group's lowest
 * level (largest y) carries every label joined with " · " while the others draw their line with no
 * chip. The lines themselves are always all drawn. Result is index-aligned with the input.
 */
export function planLevelLabels(
  levels: readonly { price: number; label: string }[],
  yOf: (price: number) => number | null,
  minGapPx = 15
): LevelLabelPlan[] {
  const plans: LevelLabelPlan[] = levels.map(l => ({ title: l.label, axisLabelVisible: true }));
  const placed = levels
    .map((l, index) => ({ index, y: yOf(l.price), price: l.price }))
    .filter((p): p is { index: number; y: number; price: number } => p.y != null && Number.isFinite(p.y))
    .sort((a, b) => b.price - a.price); // top of the chart first
  let group: typeof placed = [];
  const flush = () => {
    if (group.length > 1) {
      const carrier = group[group.length - 1];
      const joined = group.map(g => levels[g.index].label).filter(Boolean).join(' · ');
      for (const g of group) plans[g.index] = { title: '', axisLabelVisible: false };
      plans[carrier.index] = { title: joined, axisLabelVisible: true };
    }
    group = [];
  };
  for (const p of placed) {
    if (group.length && Math.abs(p.y - group[group.length - 1].y) >= minGapPx) flush();
    group.push(p);
  }
  flush();
  return plans;
}

/** Pure: a level's line width, clamped to what the chart supports. */
export function levelLineWidth(weight: number | undefined): 1 | 2 | 3 {
  const w = Math.round(finite(weight) ? weight : 1);
  return Math.min(3, Math.max(1, w)) as 1 | 2 | 3;
}
