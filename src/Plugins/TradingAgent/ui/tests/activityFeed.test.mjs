import test from 'node:test';
import assert from 'node:assert/strict';
import { audienceOf, displayMessage, filterActivity, inView, kindOf, mergeActivity, routineCandleUpdateCount, viewCounts }
  from '../src/activityFeed.ts';

const item = (seq, changes = {}) => ({
  seq, utc: '2026-11-28T05:00:00Z', lastUtc: '2026-11-28T05:00:00Z', repeats: 0,
  source: 'Orders', level: 'info', message: 'Order checked', detail: null, symbol: null, audience: 'system', ...changes
});
const all = { view: 'system', kind: 'all', stock: '', showRoutineCandles: true };

test('routine candle updates are hidden without hiding candle warnings', () => {
  const entries = [
    item(3, { source: 'Candles', message: 'Daily historical candle source: local archive' }),
    item(2, { source: 'Candles', level: 'warn', message: 'Candle archive unavailable', audience: 'trades' }),
    item(1)
  ];

  assert.deepEqual(filterActivity(entries, { ...all, showRoutineCandles: false }).map(entry => entry.seq), [2, 1]);
  assert.deepEqual(filterActivity(entries, all).map(entry => entry.seq), [3, 2, 1]);
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

test('Trades and System split by the server audience, and a warning shows in both', () => {
  const entries = [
    item(4, { source: 'Stops', message: 'THCCL: stop placed', symbol: 'THCCL', audience: 'trades' }),
    item(3, { source: 'Stops', message: 'Checking 3 protective stop(s)' }),
    item(2, { source: 'Broker', level: 'warn', message: 'Account read failed', audience: 'trades' }),
    item(1, { source: 'Notified', message: 'BUY 57 THCCL filled', audience: 'trades' })
  ];
  assert.deepEqual(filterActivity(entries, { ...all, view: 'trades' }).map(e => e.seq), [4, 2, 1]);
  assert.deepEqual(filterActivity(entries, all).map(e => e.seq), [3, 2]);
  assert.deepEqual(viewCounts(entries), { trades: 3, system: 2 });
});

test('a host that sends no audience still never hides a warning or a notification from Trades', () => {
  const legacy = (changes) => { const e = item(1, changes); delete e.audience; return e; };
  assert.equal(audienceOf(legacy({ level: 'warn', source: 'Feed' })), 'trades');
  assert.equal(audienceOf(legacy({ source: 'Notified' })), 'trades');
  assert.equal(audienceOf(legacy({ source: 'Stops', symbol: 'MARI' })), 'trades');
  assert.equal(audienceOf(legacy({ source: 'Broker', symbol: 'MARI' })), 'system');
  assert.equal(audienceOf(legacy({ source: 'Stops' })), 'system');
  assert.equal(inView(legacy({ level: 'error', source: 'Broker' }), 'system'), true);
});

test('kinds are plain words, and the kind and stock filters narrow Trades', () => {
  assert.equal(kindOf({ source: 'Armed' }), 'trigger');
  assert.equal(kindOf({ source: 'Notified' }), 'notified');
  assert.equal(kindOf({ source: 'Feed' }), 'system');
  const entries = [
    item(3, { source: 'Stops', symbol: 'MARI', audience: 'trades' }),
    item(2, { source: 'Orders', symbol: 'MEBL', audience: 'trades' }),
    item(1, { source: 'Orders', symbol: 'OGDC', level: 'warn', audience: 'trades' })
  ];
  const trades = { ...all, view: 'trades' };
  assert.deepEqual(filterActivity(entries, { ...trades, kind: 'order' }).map(e => e.seq), [2, 1]);
  assert.deepEqual(filterActivity(entries, { ...trades, kind: 'problems' }).map(e => e.seq), [1]);
  assert.deepEqual(filterActivity(entries, { ...trades, stock: 'me' }).map(e => e.seq), [2]);
});

test('the stock column takes the "SYMBOL: " off the message', () => {
  assert.equal(displayMessage({ symbol: 'THCCL', message: 'THCCL: stop placed' }), 'stop placed');
  assert.equal(displayMessage({ symbol: 'THCCL', message: 'Manual holding taken over: THCCL' }), 'Manual holding taken over: THCCL');
  assert.equal(displayMessage({ symbol: null, message: 'MARI: x' }), 'MARI: x');
});

test('a changed page merges by seq: a repeat replaces its row, a new row joins, older than kept drops', () => {
  const held = [item(3), item(2, { repeats: 0 }), item(1)];
  const merged = mergeActivity(held, [item(4), item(2, { repeats: 5 })], 2);
  assert.deepEqual(merged.map(e => [e.seq, e.repeats]), [[4, 0], [3, 0], [2, 5]]);
  assert.deepEqual(held.map(e => e.seq), [3, 2, 1], 'merging must not mutate what is held');
});

test('an empty day empties the panel; an older host that does not say keeps what it has', () => {
  assert.deepEqual(mergeActivity([item(1)], [], null), []);
  assert.deepEqual(mergeActivity([item(1)], [item(2)], undefined).map(e => e.seq), [2, 1]);
});
