import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { get } from 'svelte/store';
import { LivePriceBook } from '../src/livePrices.ts';

const T0 = Date.parse('2026-09-07T10:00:00Z');

const quote = (over = {}) => ({
  symbol: 'FCEPL', market: 'REG', current: 149.5, previousClose: 149.92,
  open: 150, high: 151, low: 149, changePercent: -0.28, volume: 1000, tradeCount: 5,
  lastTradeTime: null, boardState: 'OPN', source: 'ahl',
  receivedAtUtc: new Date(Date.now()).toISOString(), ...over
});

const envelope = (over = {}) => ({
  type: 'snapshot', sequence: 1, serverTimeUtc: new Date(Date.now()).toISOString(),
  marketOpen: true, feedState: 'healthy', feedReason: '', staleAfterSeconds: 120,
  venuePhase: 'Trading', venueState: 'OPN', quotes: [quote()], ...over
});

/** A book fed one snapshot, with its sweep running under mocked timers. */
function fedBook() {
  mock.timers.enable({ apis: ['setInterval', 'setTimeout', 'Date'], now: T0 });
  const book = new LivePriceBook();
  const stop = book.start();
  book.ingest(envelope());
  return { book, stop, connection: () => get(book.connection), view: () => get(book.quote('FCEPL')) };
}

test('a delivering stream is not stale, and its quote reads live', () => {
  const { stop, connection, view } = fedBook();
  try {
    assert.equal(connection().stale, false);
    assert.equal(connection().silentForSeconds, 0);
    assert.equal(view().freshness, 'live');
    assert.equal(view().quote.current, 149.5);

    mock.timers.tick(30_000);
    assert.equal(connection().stale, false, 'under the budget the stream is merely quiet');
    assert.equal(view().freshness, 'live');
  } finally { stop(); mock.timers.reset(); }
});

test('silence past the budget is reported even though the transport still says live', () => {
  const { stop, connection, view } = fedBook();
  try {
    mock.timers.tick(46_000);

    // The regression this pins: `state` is latched by ingest and nothing un-sets it, so a page whose
    // stream died silently kept reporting live prices. The badge's own condition is
    // `state === 'live' && feedState === 'healthy' && !stale`, so `stale` is the only term that can
    // tell the truth here — assert the other two are still saying the wrong thing.
    assert.equal(connection().state, 'live');
    assert.equal(connection().feedState, 'healthy');
    assert.equal(connection().stale, true);
    assert.equal(connection().silentForSeconds, 46);

    // The quote is 46s old against a 120s staleAfterSeconds, so only the stall can demote it.
    assert.equal(view().freshness, 'stale');
    assert.equal(view().quote.current, 149.5, 'a stalled stream keeps the last price, it does not discard it');
  } finally { stop(); mock.timers.reset(); }
});

test('a heartbeat carrying no quotes clears the stall: liveness is about delivery, not ticks', () => {
  const { book, stop, connection, view } = fedBook();
  try {
    mock.timers.tick(46_000);
    assert.equal(connection().stale, true);

    book.ingest(envelope({ type: 'status', sequence: 2, quotes: [] }));
    assert.equal(connection().stale, false);
    assert.equal(connection().silentForSeconds, 0);
    assert.equal(view().quote.current, 149.5, 'a status envelope must not empty the book');
    assert.equal(view().freshness, 'live');
  } finally { stop(); mock.timers.reset(); }
});

test('a transport reporting its own state suppresses the derived flag, and reconnecting clears it', () => {
  const { book, stop, connection } = fedBook();
  try {
    mock.timers.tick(46_000);
    assert.equal(connection().stale, true);

    book.setConnection('reconnecting', 'stream ended');
    assert.equal(connection().stale, false, 'the transport is now saying it itself');

    // And it must not re-arm while the transport is down: only an envelope may clear the silence,
    // but only a `live` transport may claim it.
    mock.timers.tick(120_000);
    assert.equal(connection().stale, false);
    assert.equal(connection().state, 'reconnecting');
  } finally { stop(); mock.timers.reset(); }
});

test('a book that has never received anything never claims to be stale', () => {
  mock.timers.enable({ apis: ['setInterval', 'setTimeout', 'Date'], now: T0 });
  const book = new LivePriceBook();
  const stop = book.start();
  try {
    mock.timers.tick(600_000);
    assert.equal(get(book.connection).stale, false);
    assert.equal(get(book.connection).silentForSeconds, null);
    assert.equal(get(book.connection).state, 'unavailable');
  } finally { stop(); mock.timers.reset(); }
});

test('retention still empties the book, and the stall explains why', () => {
  const { stop, connection, view } = fedBook();
  try {
    mock.timers.tick(10 * 60 * 1000 + 15_000);
    assert.equal(view().quote, null, 'the ten-minute UI retention still applies');
    assert.equal(view().freshness, 'unknown');
    assert.equal(connection().stale, true, 'and the connection says why the row went blank');
  } finally { stop(); mock.timers.reset(); }
});

test('a healthy stream publishes nothing on its quiet sweeps', () => {
  // The silence check runs on the retention sweep, which wakes every 15s for the life of the page.
  // Reporting a duration that ticked each time would re-render every subscriber of this store
  // forever to say "still fine", so a duration is only published once silence becomes a fault.
  const { book, stop } = fedBook();
  let writes = 0;
  const unsubscribe = book.connection.subscribe(() => writes++);
  try {
    const baseline = writes;
    mock.timers.tick(30_000);
    assert.equal(writes, baseline, 'two healthy sweeps must be silent');

    mock.timers.tick(20_000);
    assert.equal(writes, baseline + 1, 'crossing the budget publishes exactly once');
    assert.equal(get(book.connection).stale, true);
  } finally { unsubscribe(); stop(); mock.timers.reset(); }
});
