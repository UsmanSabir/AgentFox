import test from 'node:test';
import assert from 'node:assert/strict';
import { buildPortfolioAllocation } from '../src/portfolioAllocation.ts';
import { OTHER_SECTORS, filterPortfolioHoldings, holdingSectorOptions, otherSectorLabels, sectorFilterKey } from '../src/portfolioSectorFilter.ts';

const holding = (symbol, sector, value) => ({ symbol, instrumentId: symbol, sector,
  marketValue: value, quantity: 1, averageCost: 100 - (value ?? 0), currency: 'PKR', attributes: {} });
const stocks = [
  holding('A', 'Energy', 70), holding('B', 'Cement', 60), holding('C', 'Banks', 50),
  holding('D', 'Power', 40), holding('E', 'Fertilizer', 30), holding('F', 'Other', 20),
  holding('G', null, 10), holding('H', ' Textiles ', null), holding('I', 'Other', null)
];

test('sector options include unpriced and unclassified holdings and trim metadata', () => {
  assert.deepEqual(holdingSectorOptions(stocks), ['Banks', 'Cement', 'Energy', 'Fertilizer', 'Other', 'Power', 'Textiles', 'Unclassified']);
  assert.deepEqual(filterPortfolioHoldings(stocks, '', sectorFilterKey('Textiles'), []).map(h => h.symbol), ['H']);
});

test('Other uses exact chart tail and includes unpriced stocks in its member sectors', () => {
  const other = otherSectorLabels(buildPortfolioAllocation(stocks, 'sector'));
  assert.deepEqual(other, ['Other', 'Unclassified']);
  assert.deepEqual(filterPortfolioHoldings(stocks, '', OTHER_SECTORS, other).map(h => h.symbol), ['F', 'G', 'I']);
  assert.deepEqual(filterPortfolioHoldings(stocks, '', sectorFilterKey('Other'), other).map(h => h.symbol), ['F', 'I']);
  assert.deepEqual(filterPortfolioHoldings(stocks, ' g ', OTHER_SECTORS, other).map(h => h.symbol), ['G']);
  assert.deepEqual(filterPortfolioHoldings(stocks, 'A', OTHER_SECTORS, other), []);
});

test('Other follows valuation basis and disappears when no grouping exists', () => {
  const invested = otherSectorLabels(buildPortfolioAllocation(stocks, 'sector', 6, { basis: 'invested' }));
  assert.notDeepEqual(invested, otherSectorLabels(buildPortfolioAllocation(stocks, 'sector')));
  assert.deepEqual(otherSectorLabels(buildPortfolioAllocation(stocks.slice(0, 6), 'sector')), []);
  assert.deepEqual(otherSectorLabels(buildPortfolioAllocation([holding('A', 'Energy', null)], 'sector')), []);
  assert.equal(filterPortfolioHoldings(stocks, '', '', []).length, stocks.length);
});
