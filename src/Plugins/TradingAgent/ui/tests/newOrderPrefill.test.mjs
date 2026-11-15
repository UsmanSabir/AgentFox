import { test } from 'node:test';
import assert from 'node:assert/strict';
import { canKeepWorking, cleanNewOrderPrefill, composerStateFor, intentFor } from '../src/newOrderPrefill.ts';

// The shapes the server's registry reports (OrderIntentRegistry.cs), reduced to what matching reads.
const intent = (id, action, o) => ({
  id, label: id, description: '', category: '', action, submission: 'immediate', orderType: 'LIMIT',
  triggerKind: null, priceField: 'limit', defaultPercent: null, trailing: false, ...o
});
const registry = [
  intent('limit-buy', 'BUY', {}),
  intent('limit-sell', 'SELL', {}),
  intent('market-buy', 'BUY', { orderType: 'MARKET', priceField: 'none' }),
  intent('scheduled-buy', 'BUY', { submission: 'conditional', triggerKind: 'Scheduled', requiresActivationDate: true }),
  intent('profit-book', 'SELL', { priceField: 'target' }),
  intent('wait-buy-drops', 'BUY', { submission: 'conditional', triggerKind: 'PriceBelow', priceField: 'trigger-and-limit' }),
  intent('wait-buy-rises', 'BUY', { submission: 'conditional', triggerKind: 'PriceAbove', priceField: 'trigger-and-limit' }),
  intent('wait-sell-drops', 'SELL', { submission: 'conditional', triggerKind: 'PriceBelow', priceField: 'trigger-and-limit' }),
  intent('wait-sell-rises', 'SELL', { submission: 'conditional', triggerKind: 'PriceAbove', priceField: 'trigger-and-limit' }),
  intent('trailing-buy', 'BUY', { submission: 'conditional', triggerKind: 'PercentRise', priceField: 'trail-bound', trailing: true, keepWorking: true, defaultPercent: 3 }),
  intent('trailing-sell', 'SELL', { submission: 'conditional', triggerKind: 'PercentDrop', priceField: 'trail-bound', trailing: true, keepWorking: true, defaultPercent: 3 }),
  intent('buy-after-drop', 'BUY', { submission: 'conditional', triggerKind: 'PercentDrop', priceField: 'limit-at-trigger', defaultWindowMinutes: 15 })
];

test('validation: symbol is trimmed and upper-cased; side and kind are required', () => {
  assert.equal(cleanNewOrderPrefill({ symbol: ' luck ', action: 'BUY', kind: 'limit' }).symbol, 'LUCK');
  for (const bad of [
    { symbol: ' ', action: 'BUY', kind: 'limit' }, { action: 'BUY', kind: 'limit' },
    { symbol: 'X', action: 'sell', kind: 'limit' }, { symbol: 'X', kind: 'limit' },
    { symbol: 'X', action: 'BUY', kind: 'market' }, { symbol: 'X', action: 'BUY' }, null, undefined, 'LUCK'
  ]) assert.equal(cleanNewOrderPrefill(bad), null);
});

test('validation: unusable numbers are dropped, not guessed', () => {
  const clean = cleanNewOrderPrefill({
    symbol: 'X', action: 'BUY', kind: 'limit', quantity: 2.5, price: -3, triggerPrice: 5, triggerPercent: 4, priceBound: 9, expiresInDays: 10
  });
  assert.deepEqual(clean, { symbol: 'X', action: 'BUY', kind: 'limit' });
  for (const bad of [0, -1, NaN, Infinity, '10', null]) {
    assert.equal(cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'limit', quantity: bad }).quantity, undefined);
    assert.equal(cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'limit', price: bad }).price, undefined);
  }
});

test('validation: a price trigger needs a direction; each kind keeps only the fields it reads', () => {
  assert.equal(cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'priceTrigger', triggerPrice: 10 }), null);
  assert.deepEqual(
    cleanNewOrderPrefill({ symbol: 'X', action: 'SELL', kind: 'priceTrigger', triggerDirection: 'above', triggerPrice: 110, price: 109, triggerPercent: 3, priceBound: 5 }),
    { symbol: 'X', action: 'SELL', kind: 'priceTrigger', triggerDirection: 'above', triggerPrice: 110, price: 109 });
  assert.deepEqual(
    cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'trailing', triggerPercent: 4, priceBound: 90, price: 80, triggerPrice: 70 }),
    { symbol: 'X', action: 'BUY', kind: 'trailing', triggerPercent: 4, priceBound: 90 });
});

test('validation: a trail percent is 0.1 to 50 and an expiry is 1 to 365 whole days', () => {
  const t = p => cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'trailing', triggerPercent: p }).triggerPercent;
  assert.equal(t(0.1), 0.1); assert.equal(t(50), 50);
  assert.equal(t(0.05), undefined); assert.equal(t(51), undefined); assert.equal(t(NaN), undefined);
  const e = d => cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'trailing', expiresInDays: d }).expiresInDays;
  assert.equal(e(1), 1); assert.equal(e(365), 365);
  assert.equal(e(0), undefined); assert.equal(e(366), undefined); assert.equal(e(2.5), undefined);
});

test('validation: expiry on an immediate limit survives only with keep-working', () => {
  const base = { symbol: 'X', action: 'BUY', kind: 'limit', expiresInDays: 20 };
  assert.equal(cleanNewOrderPrefill(base).expiresInDays, undefined);
  assert.equal(cleanNewOrderPrefill({ ...base, keepWorking: true }).expiresInDays, 20);
  assert.equal(cleanNewOrderPrefill({ ...base, keepWorking: 'yes' }).keepWorking, undefined);
});

test('mapping: limit -> the immediate limit intent, price and keep-working', () => {
  const clean = cleanNewOrderPrefill({ symbol: 'luck', action: 'SELL', kind: 'limit', quantity: 40, price: 612.5, keepWorking: true });
  assert.deepEqual(composerStateFor(clean, registry), {
    side: 'SELL', intent: registry[1], quantity: 40, price: 612.5, triggerPrice: null,
    triggerPercent: null, priceBound: null, persistentUntilFilled: true, expiresInDays: null
  });
});

test('mapping: a price trigger picks the at-or-below / at-or-above variant by side and direction', () => {
  const pick = (action, triggerDirection) =>
    intentFor(cleanNewOrderPrefill({ symbol: 'X', action, kind: 'priceTrigger', triggerDirection }), registry).id;
  assert.equal(pick('BUY', 'below'), 'wait-buy-drops');
  assert.equal(pick('BUY', 'above'), 'wait-buy-rises');
  assert.equal(pick('SELL', 'below'), 'wait-sell-drops');
  assert.equal(pick('SELL', 'above'), 'wait-sell-rises');
});

test('mapping: a price trigger carries trigger and limit, and limits at its trigger when no price is given', () => {
  const both = composerStateFor(cleanNewOrderPrefill({
    symbol: 'X', action: 'BUY', kind: 'priceTrigger', triggerDirection: 'below', triggerPrice: 95, price: 96, quantity: 10, expiresInDays: 14
  }), registry);
  assert.equal(both.triggerPrice, 95); assert.equal(both.price, 96); assert.equal(both.expiresInDays, 14);
  assert.equal(both.persistentUntilFilled, false);
  const onlyTrigger = composerStateFor(cleanNewOrderPrefill({
    symbol: 'X', action: 'BUY', kind: 'priceTrigger', triggerDirection: 'below', triggerPrice: 95
  }), registry);
  assert.equal(onlyTrigger.price, 95);
});

test('mapping: trailing picks the trailing intent for the side, carries trail and bound, and keeps working', () => {
  const buy = composerStateFor(cleanNewOrderPrefill({
    symbol: 'X', action: 'BUY', kind: 'trailing', quantity: 5, triggerPercent: 2.5, priceBound: 101
  }), registry);
  assert.equal(buy.intent.id, 'trailing-buy');
  assert.equal(buy.triggerPercent, 2.5); assert.equal(buy.priceBound, 101);
  assert.equal(buy.price, null); assert.equal(buy.persistentUntilFilled, true);
  assert.equal(composerStateFor(cleanNewOrderPrefill({ symbol: 'X', action: 'SELL', kind: 'trailing' }), registry).intent.id, 'trailing-sell');
});

test('mapping: a server that does not offer the shape leaves the intent null, with the side still set', () => {
  const state = composerStateFor(cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'trailing' }), registry.filter(i => !i.trailing));
  assert.equal(state.intent, null);
  assert.equal(state.side, 'BUY');
});

test('mapping never selects a scheduled, percent-window or target intent for the three kinds', () => {
  for (const kind of ['limit', 'priceTrigger', 'trailing']) {
    const found = intentFor(cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind, triggerDirection: 'below' }), registry);
    assert.ok(found && !found.requiresActivationDate && found.defaultWindowMinutes == null && found.priceField !== 'target');
  }
  assert.equal(intentFor(cleanNewOrderPrefill({ symbol: 'X', action: 'SELL', kind: 'limit' }), registry).id, 'limit-sell');
});

test('mapping: keep-working is decided from the intent, and never set for a market order or no intent', () => {
  const wait = composerStateFor(cleanNewOrderPrefill({
    symbol: 'X', action: 'BUY', kind: 'priceTrigger', triggerDirection: 'below', keepWorking: true
  }), registry);
  assert.equal(wait.persistentUntilFilled, true);
  const limit = composerStateFor(cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'limit', keepWorking: true }), registry);
  assert.equal(limit.persistentUntilFilled, true);
  assert.equal(canKeepWorking(registry.find(i => i.id === 'market-buy')), false);
  assert.equal(canKeepWorking(null), false);
  const none = composerStateFor(cleanNewOrderPrefill({ symbol: 'X', action: 'BUY', kind: 'trailing', keepWorking: true }), []);
  assert.equal(none.persistentUntilFilled, false);
});
