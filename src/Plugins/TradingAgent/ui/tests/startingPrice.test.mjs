import test from 'node:test';
import assert from 'node:assert/strict';
import { startingPriceDrift } from '../src/startingPrice.ts';

test('a stale start above the market that already meets a drop trigger is reported as triggering now', () => {
  // Ticket opened at 100, 3% drop -> trigger 97; the stock now trades at 96.
  assert.deepEqual(startingPriceDrift('PercentDrop', false, 100, 96, 3, 97),
    { reference: 100, live: 96, triggersNow: true });
});

test('a trailing sell whose start is above the market is reported, and one below it is not', () => {
  // 3% trail from 100 -> 97; live 98.5 is a quarter of the move away (0.75) or more.
  assert.equal(startingPriceDrift('PercentDrop', true, 100, 98.5, 3, 97).triggersNow, false);
  // Started below the market: the server adopts 104 on its first check, so nothing to say.
  assert.equal(startingPriceDrift('PercentDrop', true, 100, 104, 3, 97), null);
});

test('a trailing buy mirrors it: only a start below the market is reported', () => {
  assert.equal(startingPriceDrift('PercentRise', true, 100, 103.5, 3, 103).triggersNow, true);
  assert.equal(startingPriceDrift('PercentRise', true, 100, 96, 3, 103), null);
});

test('a fixed percent order is reported in either direction', () => {
  assert.notEqual(startingPriceDrift('PercentDrop', false, 100, 104, 3, 97), null);
  assert.notEqual(startingPriceDrift('PercentRise', false, 100, 96, 3, 103), null);
});

test('a difference under a quarter of the move is a moving tape, not a stale ticket', () => {
  // A quarter of a 3% move from 100 is 0.75.
  assert.equal(startingPriceDrift('PercentDrop', false, 100, 99.4, 3, 97), null);
  assert.notEqual(startingPriceDrift('PercentDrop', false, 100, 99.25, 3, 97), null);
});

test('nothing is said without a live price, a starting price, or a percent order', () => {
  assert.equal(startingPriceDrift('PercentDrop', false, 100, null, 3, 97), null);
  assert.equal(startingPriceDrift('PercentDrop', false, null, 96, 3, 97), null);
  assert.equal(startingPriceDrift('PriceBelow', false, 100, 90, 3, 97), null);
  assert.equal(startingPriceDrift('PercentDrop', false, 100, 96, 0, 97), null);
});
