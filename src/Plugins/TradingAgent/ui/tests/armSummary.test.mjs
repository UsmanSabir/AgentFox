import test from 'node:test';
import assert from 'node:assert/strict';
import { armOrderSummary, armLevelLine } from '../src/armSummary.ts';

const base = {
  action: 'BUY', quantity: 31, symbol: 'MARI', orderType: 'LIMIT', price: 671.68,
  isEvent: false, triggerAlertKind: null, isPercent: true, fallsToTrigger: false,
  level: 746.64, triggerPercent: 3, referencePrice: 724.89, trailing: false, priceBound: null
};

test('a plain percent rise measures from the reference and names the level', () => {
  assert.equal(armOrderSummary(base),
    'Buys 31 MARI at 671.68 if it rises 3% from 724.89 — that is 746.64.');
});

test('a plain percent drop on a SELL still reads as before', () => {
  assert.equal(armOrderSummary({ ...base, action: 'SELL', fallsToTrigger: true, level: 703.14, price: 700 }),
    'Sells 31 MARI at 700 if it falls 3% from 724.89 — that is 703.14.');
});

test('a bounded trailing BUY follows the price down and states the cap, with no fixed level (MARI)', () => {
  const s = armOrderSummary({ ...base, trailing: true, priceBound: 671.68 });
  assert.equal(s,
    'Follows MARI down from 724.89 and buys 31 MARI at 671.68 once it rises 3% from its lowest point, at no more than 671.68.');
  assert.ok(!s.includes('746.64'));
  assert.ok(!s.includes('that is'));
});

test('an unbounded trailing BUY keeps the starting level, labelled as the start', () => {
  assert.equal(armOrderSummary({ ...base, trailing: true }),
    'Follows MARI down from 724.89 and buys 31 MARI at 671.68 once it rises 3% from its lowest point — that is 746.64 to begin with.');
});

test('a trailing SELL follows the price up and measures from its highest point', () => {
  const s = armOrderSummary({ ...base, action: 'SELL', fallsToTrigger: true, level: 703.14, trailing: true });
  assert.equal(s,
    'Follows MARI up from 724.89 and sells 31 MARI at 671.68 once it falls 3% from its highest point — that is 703.14 to begin with.');
});

test('a bounded trailing SELL states its minimum', () => {
  const s = armOrderSummary({ ...base, action: 'SELL', fallsToTrigger: true, level: 703.14, trailing: true, priceBound: 690 });
  assert.match(s, /at no less than 690\.$/);
  assert.ok(!s.includes('that is'));
});

test('a market order, an exact level, an event and no size are unchanged', () => {
  assert.equal(armOrderSummary({ ...base, orderType: 'MARKET', trailing: true }),
    'Follows MARI down from 724.89 and buys 31 MARI at the best price available once it rises 3% from its lowest point — that is 746.64 to begin with.');
  assert.equal(armOrderSummary({ ...base, isPercent: false, level: 700, fallsToTrigger: true }),
    'Buys 31 MARI at 671.68 if it falls to 700.');
  assert.equal(armOrderSummary({ ...base, isEvent: true, triggerAlertKind: 'RsiOversold' }),
    'Buys 31 MARI at 671.68 when MARI raises "rsi oversold".');
  assert.equal(armOrderSummary({ ...base, quantity: null }), null);
});

test('the line above the summary: a fixed trigger still quotes its level', () => {
  assert.deepEqual(armLevelLine(base), { kind: 'fires', level: 746.64 });
  assert.equal(armLevelLine({ ...base, isEvent: true }), null);
  assert.equal(armLevelLine({ ...base, level: null }), null);
});

test('the line above a bounded trailing BUY describes the trail and never quotes 746.64 (MARI)', () => {
  const l = armLevelLine({ ...base, trailing: true, priceBound: 671.68 });
  assert.equal(l.kind, 'trails');
  assert.equal(l.text, 'Trails the lowest price from 724.89 · fires on a 3% rise from that low, at no more than 671.68');
  assert.ok(!l.text.includes('746.64'));
});

test('the line above a trailing SELL mirrors it, bounded and unbounded', () => {
  const sell = { ...base, action: 'SELL', fallsToTrigger: true, level: 703.14, trailing: true };
  assert.equal(armLevelLine({ ...sell, priceBound: 690 }).text,
    'Trails the highest price from 724.89 · fires on a 3% fall from that high, at no less than 690');
  assert.equal(armLevelLine(sell).text,
    'Trails the highest price from 724.89 · fires on a 3% fall from that high · starts at 703.14');
});
