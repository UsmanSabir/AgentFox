import test from 'node:test';
import assert from 'node:assert/strict';
import { buildPortfolioAllocation } from '../src/portfolioAllocation.ts';

const holding = (symbol, marketValue, sector = null, currency = 'PKR') => ({
  instrumentId: symbol,
  symbol,
  sector,
  marketValue,
  currency,
  attributes: {}
});

test('stock allocation uses current market value and orders largest first', () => {
  const result = buildPortfolioAllocation([
    holding('FCCL', 2500, 'Cement'),
    holding('OGDC', 5000, 'Oil & Gas'),
    holding('FCCL', 2500, 'Cement')
  ], 'stock');

  assert.equal(result.total, 10000);
  assert.deepEqual(result.rows.map(row => [row.label, row.value, row.percent]), [
    ['FCCL', 5000, 50],
    ['OGDC', 5000, 50]
  ]);
});

test('sector allocation keeps missing classifications explicit', () => {
  const result = buildPortfolioAllocation([
    holding('FCCL', 6000, 'Cement'),
    holding('MYST', 4000)
  ], 'sector');

  assert.deepEqual(result.rows.map(row => [row.label, row.percent]), [
    ['Cement', 60],
    ['Unclassified', 40]
  ]);
});

test('chart view groups the long tail without losing the exact breakdown', () => {
  const result = buildPortfolioAllocation([
    holding('A', 60), holding('B', 10), holding('C', 9), holding('D', 8),
    holding('E', 7), holding('F', 4), holding('G', 2)
  ], 'stock', 6);

  assert.equal(result.rows.length, 7);
  assert.equal(result.slices.length, 6);
  assert.deepEqual(result.slices.at(-1), { label: 'Other', value: 6, percent: 6 });
});

test('missing values and mixed currencies are never folded into a false total', () => {
  const missing = buildPortfolioAllocation([
    holding('KNOWN', 100), holding('UNKNOWN', null)
  ], 'stock');
  assert.equal(missing.total, 100);
  assert.equal(missing.unavailableCount, 1);

  const mixed = buildPortfolioAllocation([
    holding('PK', 100, null, 'PKR'), holding('US', 10, null, 'USD')
  ], 'stock');
  assert.equal(mixed.mixedCurrencies, true);
  assert.equal(mixed.total, 0);
  assert.deepEqual(mixed.rows, []);
});
