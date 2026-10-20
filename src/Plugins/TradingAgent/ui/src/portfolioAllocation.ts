import type { BrokerAccountHolding } from './api';

export type AllocationDimension = 'stock' | 'sector';
export type AllocationBasis = 'market' | 'invested';

/** Optional purchase costs supplied by the containing edition, keyed by upper-case symbol. */
export interface HoldingCost {
  quantity: number;
  value: number;
  currency: string;
  estimated: boolean;
}
export type PortfolioCosts = Record<string, HoldingCost>;

export interface AllocationRow {
  label: string;
  value: number;
  percent: number;
  estimated?: true;
}

export interface PortfolioAllocation {
  currency: string | null;
  mixedCurrencies: boolean;
  unavailableCount: number;
  estimatedCount: number;
  total: number;
  rows: AllocationRow[];
  slices: AllocationRow[];
}

const clean = (value: string | null | undefined) => value?.trim() ?? '';
const nonnegative = (value: number | null | undefined): value is number =>
  typeof value === 'number' && Number.isFinite(value) && value >= 0;

/** Cost of the shares still held. An unmatched quantity/currency must never reuse another position's cost. */
export function holdingInvestment(holding: BrokerAccountHolding, costs: PortfolioCosts = {}):
  { value: number; estimated: boolean } | null {
  if (!nonnegative(holding.quantity) || holding.quantity <= 0) return null;
  const symbol = (clean(holding.symbol) || clean(holding.instrumentId)).toUpperCase();
  const cost = costs[symbol];
  const currency = clean(holding.currency).toUpperCase();
  if (cost && cost.quantity === holding.quantity && currency
    && clean(cost.currency).toUpperCase() === currency && nonnegative(cost.value))
    return { value: cost.value, estimated: cost.estimated };
  if (!nonnegative(holding.averageCost)) return null;
  const value = holding.averageCost * holding.quantity;
  return nonnegative(value) ? { value, estimated: true } : null;
}

/**
 * Build a portfolio breakdown plus a chart-sized view of it. Missing values stay
 * missing, rather than becoming zero, and a multi-currency account is never folded into one false
 * total. The chart is capped at six slices because smaller wedges stop being meaningfully readable.
 */
export function buildPortfolioAllocation(
  holdings: BrokerAccountHolding[],
  dimension: AllocationDimension,
  maxSlices = 6,
  valuation: { basis?: AllocationBasis; costs?: PortfolioCosts } = {}
): PortfolioAllocation {
  const priced = holdings.flatMap(holding => {
    const investment = valuation.basis === 'invested' ? holdingInvestment(holding, valuation.costs) : null;
    const value = valuation.basis === 'invested' ? investment?.value : holding.marketValue;
    return nonnegative(value) ? [{ holding, value, estimated: investment?.estimated ?? false }] : [];
  });
  const currencies = [...new Set(priced
    .filter(row => row.value > 0)
    .map(row => clean(row.holding.currency).toUpperCase() || 'UNKNOWN'))];
  const mixedCurrencies = currencies.length > 1;
  const currency = mixedCurrencies || currencies[0] === 'UNKNOWN' ? null : currencies[0] ?? null;
  const unavailableCount = holdings.length - priced.length;
  const estimatedCount = priced.filter(row => row.estimated).length;

  if (mixedCurrencies) {
    return { currency, mixedCurrencies, unavailableCount, estimatedCount, total: 0, rows: [], slices: [] };
  }

  const totals = new Map<string, number>();
  const estimates = new Set<string>();
  for (const { holding, value, estimated } of priced) {
    if (value <= 0) continue;
    const label = dimension === 'sector'
      ? clean(holding.sector) || 'Unclassified'
      : (clean(holding.symbol) || clean(holding.instrumentId) || 'Unknown instrument').toUpperCase();
    totals.set(label, (totals.get(label) ?? 0) + value);
    if (estimated) estimates.add(label);
  }

  const total = [...totals.values()].reduce((sum, value) => sum + value, 0);
  const rows = [...totals.entries()]
    .map(([label, value]): AllocationRow => ({ label, value, percent: total > 0 ? value / total * 100 : 0,
      ...(estimates.has(label) ? { estimated: true } : {}) }))
    .sort((a, b) => b.value - a.value || a.label.localeCompare(b.label));

  const sliceLimit = Math.max(2, Math.floor(maxSlices));
  const slices = rows.length <= sliceLimit
    ? rows
    : [
        ...rows.slice(0, sliceLimit - 1),
        {
          label: 'Other',
          value: rows.slice(sliceLimit - 1).reduce((sum, row) => sum + row.value, 0),
          percent: rows.slice(sliceLimit - 1).reduce((sum, row) => sum + row.percent, 0),
          ...(rows.slice(sliceLimit - 1).some(row => row.estimated) ? { estimated: true as const } : {})
        }
      ];

  return { currency, mixedCurrencies, unavailableCount, estimatedCount, total, rows, slices };
}

export function allocationGradient(
  slices: AllocationRow[],
  colors: string[],
  gapColor = 'transparent'
): string {
  if (!slices.length) return 'transparent';
  let cursor = 0;
  const stops = slices.flatMap((slice, index) => {
    const start = cursor;
    cursor += slice.percent;
    const end = index === slices.length - 1 ? 100 : cursor;
    // A narrow separator makes neighbouring colors legible without materially changing their area.
    // Clamp it for very small slices so the separator can never consume the wedge.
    const gap = Math.min(.22, Math.max(0, slice.percent / 5));
    return [
      `${gapColor} ${start.toFixed(3)}% ${(start + gap).toFixed(3)}%`,
      `${colors[index % colors.length]} ${(start + gap).toFixed(3)}% ${(end - gap).toFixed(3)}%`,
      `${gapColor} ${(end - gap).toFixed(3)}% ${end.toFixed(3)}%`
    ];
  });
  return `conic-gradient(from -90deg, ${stops.join(', ')})`;
}
