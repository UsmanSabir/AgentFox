import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AnnotationLayer } from '../src/chartAnnotationLayer.ts';

// A fake series + host that count what is currently attached, like the real chart would hold.
function rig() {
  const state = { lines: new Set(), zonePrimitives: 0, zoneSets: [], markerPlugins: 0, markerSets: [] };
  const series = {
    createPriceLine: opts => { const l = { opts }; state.lines.add(l); return l; },
    removePriceLine: l => { state.lines.delete(l); },
    priceToCoordinate: p => 1000 - p
  };
  const host = {
    attachZones: () => { state.zonePrimitives++; return { setZones: z => state.zoneSets.push(z) }; },
    createMarkers: (_s, m) => { state.markerPlugins++; state.markerSets.push(m);
      return { setMarkers: list => state.markerSets.push(list) }; },
    colorOf: kind => `c-${kind}`
  };
  return { state, series, layer: new AnnotationLayer(host) };
}

const input = (series, extra = {}) => ({
  series,
  levels: [{ price: 100, label: 'A', kind: 'target' }, { price: 200, label: 'B', kind: 'stop' }],
  zones: [{ priceFrom: 90, priceTo: 110, kind: 'support' }],
  markers: [{ time: 50, position: 'above', shape: 'arrowUp', kind: 'entry', label: 'x', detail: 'hi' }],
  firstTime: 0, lastTime: 100, light: false, ...extra
});
const lastZones = s => s.zoneSets[s.zoneSets.length - 1];
const lastMarkers = s => s.markerSets[s.markerSets.length - 1];

test('rendering twice leaves one price line per level, one zone primitive and one markers plugin', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series));
  layer.draw(input(series));
  assert.equal(state.lines.size, 2);
  assert.equal(state.zonePrimitives, 1);
  assert.equal(state.markerPlugins, 1);
  assert.equal(lastZones(state).length, 1);
  assert.equal(lastMarkers(state).length, 1);
});

test('a changed store replaces what was drawn instead of adding to it', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series));
  layer.draw(input(series));
  layer.draw(input(series, { levels: [{ price: 120, label: 'C', kind: 'target' }], zones: [], markers: [] }));
  assert.equal(state.lines.size, 1);
  assert.equal([...state.lines][0].opts.price, 120);
  assert.equal(lastZones(state).length, 0);
  assert.equal(lastMarkers(state).length, 0);
  assert.equal(state.zonePrimitives, 1);
  assert.equal(state.markerPlugins, 1);
});

test('clear removes everything attached, and a later draw does not duplicate', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series));
  layer.clear();
  assert.equal(state.lines.size, 0);
  assert.equal(lastZones(state).length, 0);
  layer.draw(input(series));
  assert.equal(state.lines.size, 2);
  assert.equal(state.zonePrimitives, 1);
});

test('forget drops handles without touching the chart; the next series gets fresh ones', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series));
  layer.forget();
  assert.equal(state.lines.size, 2, 'the old chart is gone, so nothing is removed from it');
  const other = rig();
  layer.draw(input(other.series));
  assert.equal(other.state.lines.size, 2);
  assert.equal(other.state.zonePrimitives, 0, 'the fake host of the first rig counted it, not this one');
  assert.equal(state.zonePrimitives, 2);
});

test('drawing on a different series forgets the old handles by itself', () => {
  const a = rig(), b = rig();
  a.layer.draw(input(a.series));
  a.layer.draw(input(b.series));
  assert.equal(b.state.lines.size, 2);
  assert.equal(a.state.lines.size, 2, 'the first series is not touched with the second one\'s handles');
  assert.equal(a.state.zonePrimitives, 2);
});

test('markers outside the loaded bars are not drawn, and marker detail follows the drawn ones', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series, { markers: [
    { time: 50, position: 'below', shape: 'circle', kind: 'entry', detail: 'in' },
    { time: 500, position: 'above', shape: 'circle', kind: 'entry', detail: 'out' }] }));
  assert.equal(lastMarkers(state).length, 1);
  assert.equal(lastMarkers(state)[0].position, 'belowBar');
  assert.deepEqual([...layer.markerDetails.keys()], [50]);
});

test('an atPrice marker sits at its price: a down arrow above it, an up arrow below it, a circle on it', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series, { markers: [
    { time: 10, position: 'atPrice', price: 712.55, shape: 'arrowDown', kind: 'sold' },
    { time: 20, position: 'atPrice', price: 650, shape: 'arrowUp', kind: 'buy' },
    { time: 30, position: 'atPrice', price: 700, shape: 'circle', kind: 'buy' }] }));
  const m = lastMarkers(state);
  assert.deepEqual(m.map(x => [x.position, x.price]),
    [['atPriceTop', 712.55], ['atPriceBottom', 650], ['atPriceMiddle', 700]]);
});

test('an atPrice marker without a usable price falls back to above the bar, and others are unchanged', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series, { markers: [
    { time: 10, position: 'atPrice', shape: 'arrowDown', kind: 'sold' },
    { time: 20, position: 'atPrice', price: NaN, shape: 'arrowDown', kind: 'sold' },
    { time: 30, position: 'atPrice', price: -1, shape: 'arrowDown', kind: 'sold' },
    { time: 40, position: 'below', price: 5, shape: 'arrowUp', kind: 'buy' },
    { time: 50, position: 'above', shape: 'arrowUp', kind: 'buy' }] }));
  const m = lastMarkers(state);
  assert.deepEqual(m.map(x => x.position), ['aboveBar', 'aboveBar', 'aboveBar', 'belowBar', 'aboveBar']);
  assert.ok(m.every(x => !('price' in x)));
});

test('a zone label alignment reaches the zone primitive', () => {
  const { state, series, layer } = rig();
  layer.draw(input(series, { zones: [{ priceFrom: 90, priceTo: 110, kind: 'support', label: 'Z', labelAlign: 'left' }] }));
  assert.equal(lastZones(state)[0].labelAlign, 'left');
});
