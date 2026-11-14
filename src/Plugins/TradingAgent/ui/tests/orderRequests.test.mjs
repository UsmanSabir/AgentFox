import { test } from 'node:test';
import assert from 'node:assert/strict';
import { get } from 'svelte/store';
import {
  REQUEST_TTL_MS, isFresh, orderRequests, requestArmOrder, requestPriceAlert, takeOrderRequest
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
