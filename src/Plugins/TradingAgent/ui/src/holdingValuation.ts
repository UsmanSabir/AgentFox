import type { BrokerAccountHolding } from './api';

/** One valuation for both the holding's headline and its numeric cells. */
export function holdingValuation(holding: BrokerAccountHolding, current?: number | null) {
  const hasLiveMark = current != null;
  const mark = current ?? holding.marketPrice;
  return {
    hasLiveMark, mark,
    marketValue: hasLiveMark && mark != null && holding.quantity != null
      ? mark * holding.quantity : holding.marketValue,
    profitLoss: hasLiveMark && mark != null && holding.quantity != null && holding.averageCost != null
      ? (mark - holding.averageCost) * holding.quantity : holding.unrealizedProfitLoss,
    profitLossPercent: hasLiveMark && mark != null && holding.averageCost != null && holding.averageCost > 0
      ? (mark - holding.averageCost) / holding.averageCost * 100 : holding.unrealizedProfitLossPercent
  };
}

export function psxHoldingUrl(holding: Pick<BrokerAccountHolding, 'symbol' | 'exchange'>): string | null {
  const symbol = holding.symbol?.trim().toUpperCase();
  return holding.exchange?.trim().toUpperCase() === 'PSX' && symbol && /^[A-Z0-9][A-Z0-9.-]{0,15}$/.test(symbol)
    ? `https://dps.psx.com.pk/company/${encodeURIComponent(symbol)}` : null;
}
