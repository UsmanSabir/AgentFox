import test from 'node:test';
import assert from 'node:assert/strict';
import { filterActivity, routineCandleUpdateCount } from '../src/activityFeed.ts';

const item = (seq, changes = {}) => ({
  seq, utc: '2026-09-10T10:00:00Z', lastUtc: '2026-09-10T10:00:00Z', repeats: 0,
  source: 'Orders', level: 'info', message: 'Order checked', detail: null, ...changes
});

test('routine candle updates are hidden without hiding candle warnings', () => {
  const entries = [
    item(3, { source: 'Candles', message: 'Daily historical candle source: local archive' }),
    item(2, { source: 'Candles', level: 'warn', message: 'Candle archive unavailable' }),
    item(1)
  ];

  assert.deepEqual(filterActivity(entries, false).map(entry => entry.seq), [2, 1]);
  assert.deepEqual(filterActivity(entries, true).map(entry => entry.seq), [3, 2, 1]);
  assert.deepEqual(entries.map(entry => entry.seq), [3, 2, 1], 'filtering must not mutate the feed');
});

test('the hidden count includes occurrences collapsed into a routine row', () => {
  const entries = [
    item(3, { source: 'Candles', repeats: 4 }),
    item(2, { source: 'candles', repeats: 0 }),
    item(1, { source: 'Candles', level: 'error', repeats: 8 })
  ];

  assert.equal(routineCandleUpdateCount(entries), 6);
});
