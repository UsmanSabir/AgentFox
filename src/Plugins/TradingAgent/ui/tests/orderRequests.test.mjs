import { test } from 'node:test';
import assert from 'node:assert/strict';
import { get } from 'svelte/store';
import {
  REQUEST_TTL_MS, isFresh, validArmQuantity, orderRequests, requestArmOrder, requestNewOrder, requestPriceAlert, takeOrderRequest
} from '../src/orderRequests.ts';

const drain = () => takeOrderRequest(Date.now());

test('an arm request is held once, symbol cleaned, and consumed exactly once', () => {
  drain();
  assert.equal(requestArmOrder({ symbol: ' luck ', triggerKind: 'PriceBelow', triggerPrice: 100, action: 'BUY' }, 1000), true);
  assert.equal(get(orderRequests).request.kind, 'arm');
  const taken = takeOrderRequest(1500);
  assert.equal(taken.kind, 'arm');
  assert.equal(taken.prefill.symbol, 'LUCK');
  assert.equal(taken.prefill.triggerPrice, 100);
  assert.equal(takeOrderRequest(1600), null);
  assert.equal(get(orderRequests), null);
});

test('an arm request carries an optional quantity through unchanged', () => {
  drain();
  assert.equal(requestArmOrder({ symbol: 'luck', action: 'BUY', quantity: 25 }, 1000), true);
  assert.equal(takeOrderRequest(1100).prefill.quantity, 25);
  requestArmOrder({ symbol: 'luck' }, 1000);
  assert.equal(takeOrderRequest(1100).prefill.quantity, undefined);
});

test('a request without a symbol is refused and leaves nothing pending', () => {
  drain();
  assert.equal(requestArmOrder({ symbol: '  ' }), false);
  assert.equal(get(orderRequests), null);
});

test('a price alert request carries symbol, target and direction', () => {
  drain();
  assert.equal(requestPriceAlert('engro', 310.5, 'below', 0), true);
  assert.deepEqual(takeOrderRequest(10), { kind: 'priceAlert', symbol: 'ENGRO', target: 310.5, direction: 'below' });
});

test('a price alert needs a positive finite target, and an odd direction means above', () => {
  drain();
  assert.equal(requestPriceAlert('ENGRO', 0, 'above'), false);
  assert.equal(requestPriceAlert('ENGRO', NaN, 'above'), false);
  assert.equal(requestPriceAlert('', 10, 'above'), false);
  assert.equal(get(orderRequests), null);
  requestPriceAlert('ENGRO', 10, 'sideways', 0);
  assert.equal(takeOrderRequest(1).direction, 'above');
});

test('a stale request is cleared and ignored', () => {
  drain();
  requestArmOrder({ symbol: 'LUCK' }, 0);
  assert.equal(takeOrderRequest(REQUEST_TTL_MS + 1), null);
  assert.equal(get(orderRequests), null);
});

test('freshness is bounded on both sides', () => {
  const p = { seq: 1, atMs: 1000, request: { kind: 'priceAlert', symbol: 'X', target: 1, direction: 'above' } };
  assert.equal(isFresh(p, 1000), true);
  assert.equal(isFresh(p, 1000 + REQUEST_TTL_MS), true);
  assert.equal(isFresh(p, 1001 + REQUEST_TTL_MS), false);
  assert.equal(isFresh(p, 999), false);
});

test('two identical requests are two events', () => {
  drain();
  requestArmOrder({ symbol: 'LUCK' }, 0);
  const first = get(orderRequests).seq;
  requestArmOrder({ symbol: 'LUCK' }, 0);
  assert.ok(get(orderRequests).seq > first);
  drain();
});

test('a starting quantity is whole shares above zero, else ignored', () => {
  assert.equal(validArmQuantity(25), 25);
  for (const bad of [0, -3, 2.5, NaN, Infinity, '10', null, undefined]) assert.equal(validArmQuantity(bad), null);
});

test('a new-order request is held once, cleaned, and consumed exactly once', () => {
  drain();
  assert.equal(requestNewOrder({
    symbol: ' luck ', action: 'BUY', kind: 'priceTrigger', quantity: 20, triggerDirection: 'below', triggerPrice: 95, price: 96
  }, 1000), true);
  assert.equal(get(orderRequests).request.kind, 'newOrder');
  const taken = takeOrderRequest(1500);
  assert.deepEqual(taken, { kind: 'newOrder', prefill: {
    symbol: 'LUCK', action: 'BUY', kind: 'priceTrigger', quantity: 20, triggerDirection: 'below', triggerPrice: 95, price: 96
  } });
  assert.equal(takeOrderRequest(1600), null);
});

test('a new-order request that cannot be placed is a no-op that leaves nothing pending', () => {
  drain();
  assert.equal(requestNewOrder({ symbol: '', action: 'BUY', kind: 'limit' }), false);
  assert.equal(requestNewOrder({ symbol: 'LUCK', action: 'HOLD', kind: 'limit' }), false);
  assert.equal(requestNewOrder({ symbol: 'LUCK', action: 'buy', kind: 'limit' }), false);
  assert.equal(requestNewOrder({ symbol: 'LUCK', action: 'BUY', kind: 'market' }), false);
  assert.equal(requestNewOrder({ symbol: 'LUCK', action: 'BUY', kind: 'priceTrigger', triggerPrice: 90 }), false);
  assert.equal(requestNewOrder({ symbol: 'LUCK', action: 'BUY', kind: 'priceTrigger', triggerDirection: 'sideways' }), false);
  assert.equal(requestNewOrder(null), false);
  assert.equal(get(orderRequests), null);
});

test('a stale new-order request is cleared and ignored', () => {
  drain();
  requestNewOrder({ symbol: 'LUCK', action: 'SELL', kind: 'limit', price: 100 }, 0);
  assert.equal(takeOrderRequest(REQUEST_TTL_MS + 1), null);
  assert.equal(get(orderRequests), null);
});

test('the three request kinds share one slot: the latest wins', () => {
  drain();
  requestArmOrder({ symbol: 'LUCK' }, 0);
  requestNewOrder({ symbol: 'ENGRO', action: 'SELL', kind: 'limit', price: 300 }, 0);
  assert.equal(takeOrderRequest(1).kind, 'newOrder');
  requestNewOrder({ symbol: 'ENGRO', action: 'SELL', kind: 'limit', price: 300 }, 0);
  requestPriceAlert('LUCK', 10, 'above', 0);
  assert.equal(takeOrderRequest(1).kind, 'priceAlert');
  requestPriceAlert('LUCK', 10, 'above', 0);
  requestNewOrder({ symbol: 'ENGRO', action: 'BUY', kind: 'trailing' }, 0);
  assert.equal(takeOrderRequest(1).prefill.symbol, 'ENGRO');
  assert.equal(takeOrderRequest(1), null);
});

test('a refused new-order request does not displace the pending one', () => {
  drain();
  requestArmOrder({ symbol: 'LUCK' }, 0);
  assert.equal(requestNewOrder({ symbol: '', action: 'BUY', kind: 'limit' }), false);
  assert.equal(takeOrderRequest(1).kind, 'arm');
});
