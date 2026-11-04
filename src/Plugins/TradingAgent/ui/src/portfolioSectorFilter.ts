import type { BrokerAccountHolding } from './api';
import type { PortfolioAllocation } from './portfolioAllocation';

export const OTHER_SECTORS = 'group:other';
export const holdingSectorLabel = (holding: Pick<BrokerAccountHolding, 'sector'>) =>
  holding.sector?.trim() || 'Unclassified';
export const sectorFilterKey = (label: string) => `sector:${label}`;

/** Only the actual collapsed chart rows belong to Other, never unpriced sectors by exclusion. */
export function otherSectorLabels(allocation: PortfolioAllocation): string[] {
  return allocation.rows.length > allocation.slices.length
    ? allocation.rows.slice(allocation.slices.length - 1).map(row => row.label) : [];
}

export function holdingSectorOptions(holdings: BrokerAccountHolding[]) {
  return [...new Set(holdings.map(holdingSectorLabel))].sort((a, b) => a.localeCompare(b));
}

export function filterPortfolioHoldings(
  holdings: BrokerAccountHolding[], query: string, sector: string, other: string[]
) {
  const search = query.trim().toLowerCase();
  return holdings.filter(holding => {
    const label = holdingSectorLabel(holding);
    return (!sector || (sector === OTHER_SECTORS ? other.includes(label) : sector === sectorFilterKey(label)))
      && `${holding.symbol ?? ''} ${holding.instrumentId}`.toLowerCase().includes(search);
  });
}
