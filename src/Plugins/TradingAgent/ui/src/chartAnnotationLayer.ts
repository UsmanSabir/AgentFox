/**
 * The chart's client-side annotation drawing, as a small controller that knows nothing about
 * lightweight-charts' concrete classes: it talks to a series-like interface and to two injected
 * factories. ChartPane wires the real ones; tests wire fakes and count what is attached.
 *
 * THE LIFECYCLE RULE (the defect this file exists to prevent). `draw` is IDEMPOTENT: it always
 * removes what the previous `draw` attached before attaching anew, so a render that happens twice
 * leaves exactly one price line per level, one zone primitive and one markers plugin. `forget` is
 * the opposite operation and is for REAL teardown only (the chart was removed or rebuilt): it drops
 * the handles without touching the chart, because the chart they belong to is gone. Calling it on a
 * routine redraw would orphan handles that are still attached to the live series, which is exactly
 * how annotations used to pile up. A `draw` against a different series than the last one forgets
 * the old handles by itself, as a backstop for a teardown site added later.
 */
import type { AnnotationLevel, AnnotationMarker, AnnotationZone } from './chartAnnotations.ts';
import { levelLineWidth, planLevelLabels } from './chartAnnotations.ts';
import { markerDetailsByTime, type MarkerDetailLine } from './chartOverlayMarkers.ts';

export interface LayerSeries {
  createPriceLine(options: {
    price: number; color: string; lineWidth: 1 | 2 | 3; lineStyle: number;
    axisLabelVisible: boolean; title: string;
  }): unknown;
  removePriceLine(line: unknown): void;
  priceToCoordinate(price: number): number | null;
}

export interface LayerZone {
  priceFrom: number; priceTo: number; label?: string; color: string; fromTime?: number;
}
export interface LayerZoneStyle { strongAlpha: number; faintAlpha: number; }

/** The zone primitive, attached to the series by whoever made it. */
export interface ZonesHandle { setZones(zones: LayerZone[], style?: LayerZoneStyle): void; }

export interface LayerMarker {
  time: number;
  position: 'aboveBar' | 'belowBar' | 'atPriceTop' | 'atPriceBottom' | 'atPriceMiddle';
  /** Present exactly when `position` is one of the atPrice* values. */
  price?: number;
  color: string;
  shape: 'arrowUp' | 'arrowDown' | 'circle';
  text: string;
}
export interface MarkersHandle { setMarkers(markers: LayerMarker[]): void; }

export interface LayerHost {
  /** Creates the zone primitive AND attaches it to the series. Called at most once per series. */
  attachZones(series: LayerSeries): ZonesHandle;
  /** Creates the markers plugin on the series. Called at most once per series. */
  createMarkers(series: LayerSeries, markers: LayerMarker[]): MarkersHandle;
  /** A semantic kind to a resolved colour. */
  colorOf(kind: string): string;
}

export interface LayerLine {
  line: unknown; level: AnnotationLevel; title: string; axisLabelVisible: boolean;
}

export interface LayerInput {
  series: LayerSeries;
  levels: AnnotationLevel[];
  zones: AnnotationZone[];
  markers: AnnotationMarker[];
  /** The loaded bars' first and last time; a marker outside them has no bar and is not drawn. */
  firstTime: number | null;
  lastTime: number | null;
  light: boolean;
}

export class AnnotationLayer {
  lines: LayerLine[] = [];
  markerDetails = new Map<number, MarkerDetailLine[]>();
  private series: LayerSeries | null = null;
  private zones: ZonesHandle | null = null;
  private markers: MarkersHandle | null = null;
  private readonly host: LayerHost;

  constructor(host: LayerHost) { this.host = host; }

  /** Removes everything this layer attached to the series it was last drawn on. Handles are kept. */
  clear(): void {
    try {
      if (this.series) for (const entry of this.lines) this.series.removePriceLine(entry.line);
      this.markers?.setMarkers([]);
      this.zones?.setZones([]);
    } catch {
      // handles from a disposed chart; nothing to clean up
    }
    this.lines = [];
    this.markerDetails = new Map();
  }

  /** Drops every handle without touching the chart. Real teardown only; see the file comment. */
  forget(): void {
    this.lines = [];
    this.markerDetails = new Map();
    this.markers = null;
    this.zones = null;
    this.series = null;
  }

  draw(input: LayerInput): void {
    if (this.series && this.series !== input.series) this.forget();
    else this.clear();
    this.series = input.series;

    const series = input.series;
    const host = this.host;
    const yOf = (price: number) => series.priceToCoordinate(price);
    const plans = planLevelLabels(input.levels, yOf);
    input.levels.forEach((level, i) => {
      const plan = plans[i];
      const line = series.createPriceLine({
        price: level.price,
        color: host.colorOf(level.kind),
        lineWidth: levelLineWidth(level.weight),
        lineStyle: level.dashed ? 2 : 0,
        axisLabelVisible: plan.axisLabelVisible,
        title: plan.title
      });
      this.lines.push({ line, level, title: plan.title, axisLabelVisible: plan.axisLabelVisible });
    });

    this.drawZones(series, input);
    this.drawMarkers(series, input);
  }

  private drawZones(series: LayerSeries, input: LayerInput): void {
    if (input.zones.length === 0) return;
    if (!this.zones) this.zones = this.host.attachZones(series);
    const drawn: LayerZone[] = input.zones.map(z => ({
      priceFrom: z.priceFrom, priceTo: z.priceTo, label: z.label,
      color: this.host.colorOf(z.kind), fromTime: z.fromTime
    }));
    // A touch stronger on a light background, where the same alpha reads fainter.
    this.zones.setZones(drawn, input.light
      ? { strongAlpha: 0.2, faintAlpha: 0.08 }
      : { strongAlpha: 0.15, faintAlpha: 0.06 });
  }

  private drawMarkers(series: LayerSeries, input: LayerInput): void {
    const { firstTime, lastTime } = input;
    if (firstTime == null || lastTime == null) return;
    // A time outside the loaded bars has no bar to sit on: not drawn, rather than snapped to an edge.
    const drawable = input.markers.filter(m => m.time >= firstTime && m.time <= lastTime)
      .sort((a, b) => a.time - b.time);
    const list: LayerMarker[] = drawable.map(m => {
      const shape = m.shape === 'arrowUp' || m.shape === 'arrowDown' ? m.shape : 'circle';
      const base = {
        time: m.time,
        color: this.host.colorOf(m.kind),
        shape,
        text: m.label ?? ''
      };
      if (m.position === 'atPrice' && typeof m.price === 'number' && Number.isFinite(m.price) && m.price > 0) {
        // The arrow's tip touches the price: a down arrow sits above it, an up arrow below it.
        const position = shape === 'arrowDown' ? 'atPriceTop'
          : shape === 'arrowUp' ? 'atPriceBottom' : 'atPriceMiddle';
        return { ...base, position, price: m.price } as LayerMarker;
      }
      return { ...base, position: m.position === 'below' ? 'belowBar' : 'aboveBar' } as LayerMarker;
    });
    this.markerDetails = markerDetailsByTime(drawable);
    if (this.markers) this.markers.setMarkers(list);
    else if (list.length > 0) this.markers = this.host.createMarkers(series, list);
  }
}
