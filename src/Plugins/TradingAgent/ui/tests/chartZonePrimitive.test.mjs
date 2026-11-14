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
