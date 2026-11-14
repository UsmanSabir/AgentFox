/**
 * A lightweight-charts v5 series primitive that paints translucent price zones: a rectangle across
 * the plot between two prices. It is the only drawing code the chart-annotation contract needs that
 * lightweight-charts does not already have (lines and markers are built in).
 *
 * Deliberately exposes no `autoscaleInfo`, so a zone never changes the price range the chart fits —
 * a far-away zone is simply off screen. It paints beneath the candles (`zOrder: 'bottom'`).
 *
 * Colours arrive resolved from the chart's theme tokens (see ChartPane.overlayColor); the primitive
 * only applies alpha, so light and dark follow the host theme with no colours of its own.
 */
import type {
  IPrimitivePaneRenderer, IPrimitivePaneView, ISeriesApi, ISeriesPrimitive, SeriesAttachedParameter,
  SeriesType, Time, IChartApiBase, Logical
} from 'lightweight-charts';

export interface DrawnZone {
  priceFrom: number;
  priceTo: number;
  label?: string;
  /** A resolved CSS colour. Alpha is applied by the primitive. */
  color: string;
  /** UTC seconds; bars before it are painted fainter. Omitted: one strength throughout. */
  fromTime?: number;
}

export interface ZoneStyle {
  /** Fill alpha at full strength. */
  strongAlpha: number;
  /** Fill alpha over bars before a zone's `fromTime`. */
  faintAlpha: number;
}

export interface ZoneSegment { x0: number; x1: number; strong: boolean; }

/**
 * Pure: the horizontal bands a zone is painted in. `xFrom` is the pixel column where the zone turns
 * strong (null: no split, strong throughout). Clamped to the plot; an `xFrom` at or past the right
 * edge paints the whole width faint, and one at or before the left edge paints it all strong.
 */
export function zoneSegments(width: number, xFrom: number | null): ZoneSegment[] {
  if (!(width > 0)) return [];
  if (xFrom == null || xFrom <= 0) return [{ x0: 0, x1: width, strong: true }];
  if (xFrom >= width) return [{ x0: 0, x1: width, strong: false }];
  return [{ x0: 0, x1: xFrom, strong: false }, { x0: xFrom, x1: width, strong: true }];
}

/** Pure: a zone's top and bottom rows from its two prices (either order), or null if unknown. */
export function zoneRows(
  priceFrom: number, priceTo: number, yOf: (price: number) => number | null
): { top: number; bottom: number } | null {
  const a = yOf(priceFrom), b = yOf(priceTo);
  if (a == null || b == null || !Number.isFinite(a) || !Number.isFinite(b)) return null;
  return { top: Math.min(a, b), bottom: Math.max(a, b) };
}

/**
 * Pure: the pixel column where a zone turns strong. A `fromTime` past the last bar (a forecast) turns
 * strong at the first slot after the data; otherwise at the first bar at or after it (`timeToIndex`
 * with findNearest), so a time that sits between bars still splits. Null when it cannot be placed,
 * which paints the zone strong throughout.
 */
export function zoneStartColumn(
  fromTime: number,
  lastBarTime: number | null,
  barCount: number,
  indexAtOrAfter: (time: number) => number | null,
  columnOfIndex: (index: number) => number | null
): number | null {
  if (lastBarTime == null || barCount <= 0) return null;
  if (fromTime > lastBarTime) return columnOfIndex(barCount);
  const index = indexAtOrAfter(fromTime);
  return index == null ? null : columnOfIndex(index);
}

type Draw = IPrimitivePaneRenderer['draw'];

class ZoneRenderer implements IPrimitivePaneRenderer {
  // No constructor parameter property: the tests load this file with Node's type stripping, which
  // does not support them.
  private readonly owner: ZonePrimitive;
  constructor(owner: ZonePrimitive) { this.owner = owner; }

  draw: Draw = target => {
    const { zones, style, series, chart } = this.owner.snapshot();
    if (!series || !chart || zones.length === 0) return;
    target.useMediaCoordinateSpace(({ context: ctx, mediaSize }) => {
      const timeScale = chart.timeScale();
      for (const zone of zones) {
        const rows = zoneRows(zone.priceFrom, zone.priceTo, p => series.priceToCoordinate(p));
        if (!rows) continue;
        // Keep to the visible rows; a zone wholly outside paints nothing.
        const top = Math.max(0, rows.top), bottom = Math.min(mediaSize.height, rows.bottom);
        if (bottom <= top) continue;

        // timeToCoordinate is exact-match only in v5, so a time that is not on a bar (a weekend, mid-bar,
        // a daily time on intraday bars) would lose the split. Resolve through the bar index instead.
        const data = series.data();
        const xFrom = zone.fromTime == null ? null : zoneStartColumn(
          zone.fromTime,
          data.length > 0 ? (data[data.length - 1].time as number) : null,
          data.length,
          t => timeScale.timeToIndex(t as Time, true),
          i => timeScale.logicalToCoordinate(i as Logical));
        ctx.save();
        ctx.fillStyle = zone.color;
        for (const seg of zoneSegments(mediaSize.width, xFrom)) {
          ctx.globalAlpha = seg.strong ? style.strongAlpha : style.faintAlpha;
          ctx.fillRect(seg.x0, top, seg.x1 - seg.x0, bottom - top);
        }
        // Hairline edges so a zone has a definite top and bottom even at low fill strength.
        ctx.globalAlpha = Math.min(1, style.strongAlpha * 2.2);
        ctx.strokeStyle = zone.color;
        ctx.lineWidth = 1;
        ctx.beginPath();
        if (rows.top >= 0) { ctx.moveTo(0, rows.top + 0.5); ctx.lineTo(mediaSize.width, rows.top + 0.5); }
        if (rows.bottom <= mediaSize.height) { ctx.moveTo(0, rows.bottom - 0.5); ctx.lineTo(mediaSize.width, rows.bottom - 0.5); }
        ctx.stroke();

        if (zone.label && bottom - top >= 15) {
          ctx.globalAlpha = 0.9;
          ctx.fillStyle = zone.color;
          ctx.font = '11px system-ui, sans-serif';
          ctx.textAlign = 'right';
          ctx.textBaseline = 'top';
          ctx.fillText(zone.label, mediaSize.width - 6, top + 3);
        }
        ctx.restore();
      }
    });
  };
}

class ZoneView implements IPrimitivePaneView {
  private readonly _renderer: ZoneRenderer;
  constructor(owner: ZonePrimitive) { this._renderer = new ZoneRenderer(owner); }
  zOrder() { return 'bottom' as const; }
  renderer() { return this._renderer; }
}

/** One primitive per chart, painting every annotation zone. Attach it to the main series once. */
export class ZonePrimitive implements ISeriesPrimitive<Time> {
  private zones: DrawnZone[] = [];
  private style: ZoneStyle = { strongAlpha: 0.14, faintAlpha: 0.06 };
  private series: ISeriesApi<SeriesType, Time> | null = null;
  private chart: IChartApiBase<Time> | null = null;
  private requestUpdate: (() => void) | null = null;
  private readonly views = [new ZoneView(this)];

  attached(param: SeriesAttachedParameter<Time, SeriesType>) {
    this.series = param.series;
    this.chart = param.chart;
    this.requestUpdate = param.requestUpdate;
  }

  detached() {
    this.series = null;
    this.chart = null;
    this.requestUpdate = null;
  }

  paneViews() { return this.views; }

  updateAllViews() { /* geometry is resolved at draw time from the live scales */ }

  /** Replaces every zone and repaints. An empty list clears the chart of zones. */
  setZones(zones: DrawnZone[], style?: ZoneStyle) {
    this.zones = zones;
    if (style) this.style = style;
    this.requestUpdate?.();
  }

  /** Read by the renderer. */
  snapshot() {
    return { zones: this.zones, style: this.style, series: this.series, chart: this.chart };
  }
}
