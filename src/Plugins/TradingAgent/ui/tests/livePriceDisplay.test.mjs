import { test } from 'node:test';
import assert from 'node:assert/strict';
import { resolveDisplayQuote } from '../src/livePrices.ts';

const view = (over = {}) => ({ quote: null, freshness: 'unknown', phase: 'Trading', ...over });
const tick = (over = {}) => ({
  symbol: 'FCEPL', market: 'REG', current: 149.5, previousClose: 149.92, open: null, high: null,
  low: null, changePercent: -0.28, volume: null, tradeCount: null, lastTradeTime: null,
  boardState: 'OPN', source: 'ahl', receivedAtUtc: '2026-09-07T10:00:00Z', ...over
});

test('a current live tick wins over the fallbacks and needs no qualifier', () => {
  const d = resolveDisplayQuote(view({ quote: tick(), freshness: 'live' }), 140, 9);
  assert.deepEqual(d, { price: 149.5, change: -0.28, tag: null, label: 'Live price' });
});

test('with no live quote the delayed fallbacks are shown, and SAID to be delayed', () => {
  // The 2026-09-07 regression: the watchlist passed only fallbackChange, so a row rendered a
  // percentage with an empty price column while the same response carried the price. Both fields
  // must resolve, and the price must be labelled — unlabelled it reads as a live tick.
  const d = resolveDisplayQuote(view(), 149.5, -0.28);
  assert.equal(d.price, 149.5);
  assert.equal(d.change, -0.28);
  assert.equal(d.tag, 'delayed');
  assert.equal(d.label, 'Delayed market snapshot');
});

test('a live quote carrying no price of its own still labels the fallback it falls back to', () => {
  // A tick can parse with no LastPrice, and the tag describes where the PRICE came from, not where
  // the view came from. Tagging this 'live' would present a delayed snapshot as a current trade.
  const d = resolveDisplayQuote(view({ quote: tick({ current: null }), freshness: 'live' }), 140, null);
  assert.equal(d.price, 140);
  assert.equal(d.tag, 'delayed');
  assert.equal(d.change, -0.28, 'the live change is still preferred; the two fields resolve apart');
});

test('a live quote past its freshness window is tagged by phase, not by the fallback rule', () => {
  for (const [freshness, phase, tag] of [
    ['stale', 'Trading', 'stale'],
    ['closed', 'PreOpen', 'pre-open'],
    ['closed', 'Closed', 'close'],
    ['closed', 'Unknown', 'close']
  ]) assert.equal(resolveDisplayQuote(view({ quote: tick(), freshness, phase }), 1, 2).tag, tag, `${freshness}/${phase}`);
});

test('a change-only cell never repeats the delayed tag its sibling carries', () => {
  const priceCell = resolveDisplayQuote(view(), 149.5, null, true);
  const changeCell = resolveDisplayQuote(view(), null, -0.28, false);
  assert.equal(priceCell.tag, 'delayed');
  assert.equal(changeCell.tag, null);
  assert.equal(changeCell.change, -0.28, 'the change is still rendered, only the tag is suppressed');
});

test('nothing known stays nothing: no price, no change, no tag', () => {
  const d = resolveDisplayQuote(view());
  assert.deepEqual(d, { price: null, change: null, tag: null, label: 'Delayed market snapshot' });
  // A zero fallback is a real price and must not be swallowed by a nullish default.
  assert.equal(resolveDisplayQuote(view(), 0, 0).price, 0);
  assert.equal(resolveDisplayQuote(view(), 0, 0).change, 0);
  assert.equal(resolveDisplayQuote(view(), 0, 0).tag, 'delayed');
});
