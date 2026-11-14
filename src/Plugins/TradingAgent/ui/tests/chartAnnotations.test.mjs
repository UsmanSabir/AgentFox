import { test } from 'node:test';
import assert from 'node:assert/strict';
import { get, writable } from 'svelte/store';
import {
  MAX_LEVELS, annotationOffscreen, annotationsForSymbol, chartAnnotationOwners, chartAnnotationsFor,
  countOffscreen, levelLineWidth, planLevelLabels, reportAnnotationOffscreen, sanitiseAnnotations,
  setChartAnnotations
} from '../src/chartAnnotations.ts';

const level = (price, label = `L${price}`, kind = 'target') => ({ price, label, kind });

function reset() {
  for (const owner of Object.keys(get(chartAnnotationOwners))) setChartAnnotations(owner, 'X', null);
}

test('each owner keeps its own set and the symbol is stored upper-cased', () => {
  reset();
  setChartAnnotations('a', ' lucK ', { levels: [level(100)] });
  setChartAnnotations('b', 'LUCK', { levels: [level(110)] });
  const all = get(chartAnnotationOwners);
  assert.deepEqual(Object.keys(all).sort(), ['a', 'b']);
  assert.equal(all.a.symbol, 'LUCK');
  reset();
});

test('a later write replaces that owner\'s set whole and leaves other owners alone', () => {
  reset();
  setChartAnnotations('a', 'LUCK', { levels: [level(100), level(101)] });
  setChartAnnotations('b', 'LUCK', { levels: [level(110)] });
  setChartAnnotations('a', 'LUCK', { levels: [level(105)] });
  const all = get(chartAnnotationOwners);
  assert.deepEqual(all.a.annotations.levels.map(l => l.price), [105]);
  assert.deepEqual(all.b.annotations.levels.map(l => l.price), [110]);
  reset();
});

test('null clears an owner, and so does an empty set or a blank symbol', () => {
  reset();
  setChartAnnotations('a', 'LUCK', { levels: [level(100)] });
  setChartAnnotations('a', 'LUCK', null);
  assert.deepEqual(Object.keys(get(chartAnnotationOwners)), []);
  setChartAnnotations('a', 'LUCK', { levels: [level(100)] });
  setChartAnnotations('a', 'LUCK', {});
  assert.deepEqual(Object.keys(get(chartAnnotationOwners)), []);
  setChartAnnotations('a', '  ', { levels: [level(100)] });
  assert.deepEqual(Object.keys(get(chartAnnotationOwners)), []);
  setChartAnnotations('  ', 'LUCK', { levels: [level(100)] });
  assert.deepEqual(Object.keys(get(chartAnnotationOwners)), []);
});

test('only the charted symbol\'s annotations are returned, in owner order', () => {
  reset();
  setChartAnnotations('b', 'LUCK', { levels: [level(110)] });
  setChartAnnotations('a', 'LUCK', { levels: [level(100)] });
  setChartAnnotations('c', 'ENGRO', { levels: [level(500)] });
  const all = get(chartAnnotationOwners);
  assert.deepEqual(annotationsForSymbol(all, 'luck').map(s => s.owner), ['a', 'b']);
  assert.deepEqual(annotationsForSymbol(all, 'ENGRO').map(s => s.owner), ['c']);
  assert.deepEqual(annotationsForSymbol(all, 'OGDC'), []);
  assert.deepEqual(annotationsForSymbol(all, null), []);
  reset();
});

test('the derived store follows both the symbol and the owners', () => {
  reset();
  const symbol = writable('LUCK');
  const view = chartAnnotationsFor(symbol);
  assert.deepEqual(get(view), []);
  setChartAnnotations('a', 'LUCK', { levels: [level(100)] });
  assert.equal(get(view).length, 1);
  symbol.set('ENGRO');
  assert.equal(get(view).length, 0);
  reset();
});

test('unusable entries are dropped and the bounds are enforced', () => {
  const clean = sanitiseAnnotations({
    levels: [level(NaN), level(-1), level(0), level(50), ...Array.from({ length: 30 }, (_, i) => level(10 + i))],
    zones: [
      { priceFrom: 5, priceTo: 5, kind: 'target' },
      { priceFrom: 5, priceTo: Infinity, kind: 'target' },
      { priceFrom: 5, priceTo: 6, kind: 'target' }
    ],
    markers: [{ time: NaN, position: 'above', shape: 'circle', kind: 'neutral' },
      { time: 1000, position: 'below', shape: 'arrowUp', kind: 'entry' }]
  });
  assert.equal(clean.levels.length, MAX_LEVELS);
  assert.equal(clean.zones.length, 1);
  assert.equal(clean.markers.length, 1);
});

test('line weight is clamped to 1-3 and defaults to 1', () => {
  assert.equal(levelLineWidth(undefined), 1);
  assert.equal(levelLineWidth(0), 1);
  assert.equal(levelLineWidth(2), 2);
  assert.equal(levelLineWidth(9), 3);
  assert.equal(levelLineWidth(NaN), 1);
});

test('off-screen counts split above and below, and a zone counts only when wholly outside', () => {
  const levels = [{ price: 120 }, { price: 100 }, { price: 80 }, { price: 50 }];
  const zones = [
    { priceFrom: 130, priceTo: 125 },   // wholly above
    { priceFrom: 95, priceTo: 105 },    // inside
    { priceFrom: 90, priceTo: 115 },    // crosses the top edge, still on screen
    { priceFrom: 20, priceTo: 40 }      // wholly below
  ];
  assert.deepEqual(countOffscreen(levels, zones, 70, 110), { above: 2, below: 2 });
  assert.deepEqual(countOffscreen(levels, zones, null, 110), { above: 0, below: 0 });
  assert.deepEqual(countOffscreen(levels, zones, 110, 70), { above: 0, below: 0 });
});

test('the off-screen store reports per owner and retracts on null', () => {
  reportAnnotationOffscreen('a', { above: 1, below: 0 });
  assert.deepEqual(get(annotationOffscreen).a, { above: 1, below: 0 });
  reportAnnotationOffscreen('a', null);
  assert.equal('a' in get(annotationOffscreen), false);
});

test('an unchanged off-screen report does not notify subscribers', () => {
  reportAnnotationOffscreen('a', { above: 1, below: 2 });
  let calls = 0;
  const stop = annotationOffscreen.subscribe(() => calls++);
  assert.equal(calls, 1);
  reportAnnotationOffscreen('a', { above: 1, below: 2 });
  assert.equal(calls, 1);
  reportAnnotationOffscreen('a', { above: 2, below: 2 });
  assert.equal(calls, 2);
  stop();
  reportAnnotationOffscreen('a', null);
});

test('labels of levels closer than a chip are merged onto the lowest line', () => {
  // y grows downwards: a higher price has a smaller y.
  const y = price => 1000 - price * 10;
  const plans = planLevelLabels([
    { price: 100, label: 'Sold' },
    { price: 100.5, label: 'Break-even' },
    { price: 90, label: 'Target' }
  ], y);
  assert.deepEqual(plans[0], { title: 'Break-even · Sold', axisLabelVisible: true });
  assert.deepEqual(plans[1], { title: '', axisLabelVisible: false });
  assert.deepEqual(plans[2], { title: 'Target', axisLabelVisible: true });
});

test('levels far enough apart, or with an unknown row, keep their own labels', () => {
  const y = price => (price === 77 ? null : 1000 - price * 10);
  const plans = planLevelLabels([
    { price: 100, label: 'A' }, { price: 98, label: 'B' }, { price: 77, label: 'C' }
  ], y);
  assert.deepEqual(plans.map(p => p.title), ['A', 'B', 'C']);
  assert.ok(plans.every(p => p.axisLabelVisible));
});

test('a chain of close levels merges into one chip', () => {
  const y = price => 1000 - price * 10;
  const plans = planLevelLabels([
    { price: 100, label: 'A' }, { price: 99, label: 'B' }, { price: 98, label: 'C' }
  ], y);
  assert.equal(plans[2].title, 'A · B · C');
  assert.equal(plans[0].axisLabelVisible, false);
  assert.equal(plans[1].axisLabelVisible, false);
});
