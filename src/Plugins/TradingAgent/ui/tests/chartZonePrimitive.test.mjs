import { test } from 'node:test';
import assert from 'node:assert/strict';
import { zoneRows, zoneSegments } from '../src/chartZonePrimitive.ts';

test('a zone with no split is one strong band across the plot', () => {
  assert.deepEqual(zoneSegments(400, null), [{ x0: 0, x1: 400, strong: true }]);
});

test('a split zone is faint before the column and strong from it', () => {
  assert.deepEqual(zoneSegments(400, 300), [
    { x0: 0, x1: 300, strong: false },
    { x0: 300, x1: 400, strong: true }
  ]);
});

test('a split at or past the edges clamps to a single band', () => {
  assert.deepEqual(zoneSegments(400, 0), [{ x0: 0, x1: 400, strong: true }]);
  assert.deepEqual(zoneSegments(400, -20), [{ x0: 0, x1: 400, strong: true }]);
  assert.deepEqual(zoneSegments(400, 400), [{ x0: 0, x1: 400, strong: false }]);
  assert.deepEqual(zoneSegments(400, 999), [{ x0: 0, x1: 400, strong: false }]);
});

test('an empty plot has nothing to paint', () => {
  assert.deepEqual(zoneSegments(0, 10), []);
  assert.deepEqual(zoneSegments(-5, null), []);
});

test('zone rows are ordered top to bottom whichever price is given first', () => {
  const y = price => 500 - price;
  assert.deepEqual(zoneRows(100, 120, y), { top: 380, bottom: 400 });
  assert.deepEqual(zoneRows(120, 100, y), { top: 380, bottom: 400 });
});

test('a price the scale cannot place gives no rows', () => {
  assert.equal(zoneRows(100, 120, () => null), null);
  assert.equal(zoneRows(100, 120, p => (p === 100 ? NaN : 10)), null);
});

test('a split time that is not on a bar resolves to the first bar at or after it', async () => {
  const { zoneStartColumn } = await import('../src/chartZonePrimitive.ts');
  const times = [100, 200, 300, 400];
  const indexAtOrAfter = t => { const i = times.findIndex(x => x >= t); return i < 0 ? null : i; };
  const column = i => i * 10;
  assert.equal(zoneStartColumn(250, 400, 4, indexAtOrAfter, column), 20, 'between bars: the next one');
  assert.equal(zoneStartColumn(300, 400, 4, indexAtOrAfter, column), 20, 'on a bar: that bar');
  assert.equal(zoneStartColumn(50, 400, 4, indexAtOrAfter, column), 0, 'before the first bar');
  assert.equal(zoneStartColumn(500, 400, 4, indexAtOrAfter, column), 40, 'past the last bar: the slot after the data');
  assert.equal(zoneStartColumn(250, null, 0, indexAtOrAfter, column), null, 'no data: unresolvable');
  assert.equal(zoneStartColumn(250, 400, 4, () => null, column), null);
});
