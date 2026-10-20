import test from 'node:test';
import assert from 'node:assert/strict';
import { allocationGradient, buildPortfolioAllocation, holdingInvestment } from '../src/portfolioAllocation.ts';

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

test('doughnut gradient separates slices while preserving their assigned colors', () => {
  const gradient = allocationGradient([
    { label: 'A', value: 60, percent: 60 },
    { label: 'B', value: 40, percent: 40 }
  ], ['blue', 'gold'], 'gap');

  assert.match(gradient, /^conic-gradient\(from -90deg,/);
  assert.match(gradient, /blue 0\.220% 59\.780%/);
  assert.match(gradient, /gold 60\.220% 99\.780%/);
  assert.match(gradient, /gap 59\.780% 60\.000%/);
});

test('invested allocation uses remaining purchase costs, independent of market prices', () => {
  const holdings = [
    { ...holding('A', 9000, 'Cement'), quantity: 100, averageCost: 70 },
    { ...holding('B', null, 'Energy'), quantity: 50, averageCost: 100 }
  ];
  const costs = { A: { quantity: 100, value: 6000, currency: 'PKR', estimated: false } };
  const result = buildPortfolioAllocation(holdings, 'stock', 6, { basis: 'invested', costs });
  assert.equal(result.total, 11000, 'FIFO cost plus explicitly estimated broker-average fallback');
  assert.equal(result.unavailableCount, 0, 'purchase cost does not depend on an available market value');
  assert.equal(result.estimatedCount, 1);
  assert.deepEqual(result.rows.map(row => [row.label, row.value, !!row.estimated]), [
    ['A', 6000, false], ['B', 5000, true]
  ]);
  assert.equal(result.rows[0].percent, 6000 / 11000 * 100);
  const sector = buildPortfolioAllocation(holdings, 'sector', 6, { basis: 'invested', costs });
  assert.deepEqual(sector.rows.map(row => row.label), ['Cement', 'Energy']);
  assert.equal(buildPortfolioAllocation(holdings, 'stock').total, 9000, 'market mode is unchanged');
});

test('stale quantities and other currencies never substitute their costs for this holding', () => {
  const stock = { ...holding(' a ', 9000), quantity: 100, averageCost: 70 };
  for (const override of [{ quantity: 90 }, { currency: 'USD' }, { value: NaN }, { value: -1 }]) {
    assert.deepEqual(holdingInvestment(stock, {
      A: { quantity: 100, value: 6000, currency: 'PKR', estimated: false, ...override }
    }), { value: 7000, estimated: true });
  }
  assert.deepEqual(holdingInvestment(stock, {
    A: { quantity: 100, value: 6000, currency: 'PKR', estimated: false }
  }), { value: 6000, estimated: false });
});

test('unknown costs remain unknown and investment currencies cannot be combined', () => {
  const stocks = [
    { ...holding('A', 9000), quantity: 100, averageCost: 70 },
    { ...holding('B', 5000), quantity: 50, averageCost: null },
    { ...holding('C', 100), quantity: null, averageCost: 5 },
    { ...holding('D', 100), quantity: 5, averageCost: Infinity }
  ];
  const result = buildPortfolioAllocation(stocks, 'stock', 6, { basis: 'invested' });
  assert.equal(result.total, 7000);
  assert.equal(result.unavailableCount, 3);
  assert.equal(result.rows[0].percent, 100);
  assert.equal(holdingInvestment({ ...stocks[0], quantity: -100 }), null);
  assert.deepEqual(holdingInvestment({ ...stocks[0], averageCost: 0 }), { value: 0, estimated: true });
  const mixed = buildPortfolioAllocation([
    stocks[0], { ...stocks[0], symbol: 'US', currency: 'USD' }
  ], 'stock', 6, { basis: 'invested' });
  assert.equal(mixed.mixedCurrencies, true);
  assert.equal(mixed.total, 0);
  assert.deepEqual(mixed.rows, []);
});

test('sector and Other aggregates retain an estimate from any contributing holding', () => {
  const stocks = ['A', 'B', 'C', 'D'].map((symbol, index) => ({
    ...holding(symbol, 100, index < 2 ? 'Cement' : 'Energy'), quantity: 1, averageCost: 100
  }));
  const costs = { A: { quantity: 1, value: 300, currency: 'PKR', estimated: false } };
  const sector = buildPortfolioAllocation(stocks, 'sector', 6, { basis: 'invested', costs });
  assert.deepEqual(sector.rows.map(row => [row.label, row.value, row.estimated]), [
    ['Cement', 400, true], ['Energy', 200, true]
  ]);
  const limited = buildPortfolioAllocation(stocks, 'stock', 2, { basis: 'invested', costs });
  assert.deepEqual(limited.slices.map(row => [row.label, row.value, !!row.estimated]), [
    ['A', 300, false], ['Other', 300, true]
  ]);
});
