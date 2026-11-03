import { test } from 'node:test';
import assert from 'node:assert/strict';
import { holdingValuation, psxHoldingUrl } from '../src/holdingValuation.ts';

test('holding headline and numeric cells share live gains, losses and break-even', () => {
  const holding = { quantity:20, averageCost:100, marketPrice:90, marketValue:1800,
    unrealizedProfitLoss:-200, unrealizedProfitLossPercent:-10 };
  for (const [price, percent] of [[102,2],[98,-2],[100,0]]) {
    const value = holdingValuation(holding, price);
    assert.equal(value.profitLossPercent, percent);
    assert.equal(value.profitLoss, (price - 100) * 20);
    assert.equal(value.marketValue, price * 20);
  }
  assert.equal(holdingValuation(holding).profitLossPercent, -10);
});
test('missing costs or percentage stay unknown instead of inventing a return', () => {
  assert.equal(holdingValuation({ averageCost:null, unrealizedProfitLossPercent:null }, 100).profitLossPercent, null);
  assert.equal(holdingValuation({ averageCost:0, unrealizedProfitLossPercent:null }, 100).profitLossPercent, null);
});
test('PSX links normalize valid symbols and omit missing, unsafe or other-exchange identifiers', () => {
  assert.equal(psxHoldingUrl({symbol:' ffc ',exchange:'psx'}), 'https://dps.psx.com.pk/company/FFC');
  for (const holding of [{symbol:null,exchange:'PSX'},{symbol:'../FFC',exchange:'PSX'}, {symbol:'FFC',exchange:'OTHER'}])
    assert.equal(psxHoldingUrl(holding), null);
});
