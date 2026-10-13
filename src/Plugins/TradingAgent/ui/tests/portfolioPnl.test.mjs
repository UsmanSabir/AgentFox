import test from 'node:test';
import assert from 'node:assert/strict';
import { summarizePortfolioPnl } from '../src/portfolioPnl.ts';

const holding = (symbol, values = {}) => ({
  instrumentId: symbol,
  symbol,
  quantity: 10,
  averageCost: 100,
  costValue: 1000,
  unrealizedProfitLoss: 100,
  currency: 'PKR',
  attributes: {},
  ...values
});
const view = current => ({
  quote: current == null ? null : { current },
  freshness: current == null ? 'unknown' : 'live',
  phase: 'Trading'
});

test('totals broker unrealized P/L and calculates the portfolio return', () => {
  const result = summarizePortfolioPnl([
    holding('GAIN', { unrealizedProfitLoss: 200 }),
    holding('LOSS', { unrealizedProfitLoss: -50 })
  ]);

  assert.equal(result.amount, 150);
  assert.equal(result.percent, 7.5);
  assert.equal(result.currency, 'PKR');
  assert.equal(result.tone, 'gain');
  assert.equal(result.liveMarkCount, 0);
});

test('uses the same live-mark arithmetic as the holding row', () => {
  const result = summarizePortfolioPnl([
    holding('LIVE', { unrealizedProfitLoss: -999 }),
    holding('SNAPSHOT', { unrealizedProfitLoss: -25 })
  ], [view(112), view(null)]);

  assert.equal(result.amount, 95);
  assert.equal(result.percent, 4.75);
  assert.equal(result.liveMarkCount, 1);
  assert.equal(result.tone, 'gain');
});

test('never treats missing P/L or mixed currencies as a combinable zero', () => {
  const missing = summarizePortfolioPnl([
    holding('KNOWN'),
    holding('UNKNOWN', { unrealizedProfitLoss: null, quantity: null, averageCost: null })
  ]);
  assert.equal(missing.amount, null);
  assert.equal(missing.unavailableCount, 1);
  assert.equal(missing.tone, 'unknown');

  const mixed = summarizePortfolioPnl([
    holding('PKR'),
    holding('USD', { currency: 'USD' })
  ]);
  assert.equal(mixed.amount, null);
  assert.equal(mixed.currency, null);
  assert.equal(mixed.mixedCurrencies, true);
});

test('keeps a known amount when only its percentage basis is unavailable', () => {
  const result = summarizePortfolioPnl([
    holding('NO-COST', { costValue: null, quantity: null, averageCost: null, unrealizedProfitLoss: -20 })
  ]);

  assert.equal(result.amount, -20);
  assert.equal(result.percent, null);
  assert.equal(result.tone, 'loss');
});
