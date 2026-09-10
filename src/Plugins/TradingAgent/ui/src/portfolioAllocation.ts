import type { BrokerAccountHolding } from './api';

export type AllocationDimension = 'stock' | 'sector';

export interface AllocationRow {
  label: string;
  value: number;
  percent: number;
}

export interface PortfolioAllocation {
  currency: string | null;
  mixedCurrencies: boolean;
  unavailableCount: number;
  total: number;
  rows: AllocationRow[];
  slices: AllocationRow[];
}

const clean = (value: string | null | undefined) => value?.trim() ?? '';

/**
 * Build an exact portfolio breakdown plus a chart-sized view of it. Missing market values stay
 * missing, rather than becoming zero, and a multi-currency account is never folded into one false
 * total. The chart is capped at six slices because smaller wedges stop being meaningfully readable.
 */
export function buildPortfolioAllocation(
  holdings: BrokerAccountHolding[],
  dimension: AllocationDimension,
  maxSlices = 6
): PortfolioAllocation {
  const priced = holdings.filter(holding =>
    typeof holding.marketValue === 'number'
      && Number.isFinite(holding.marketValue)
      && holding.marketValue >= 0);
  const currencies = [...new Set(priced
    .filter(holding => (holding.marketValue ?? 0) > 0)
    .map(holding => clean(holding.currency).toUpperCase() || 'UNKNOWN'))];
  const mixedCurrencies = currencies.length > 1;
  const currency = mixedCurrencies || currencies[0] === 'UNKNOWN' ? null : currencies[0] ?? null;
  const unavailableCount = holdings.length - priced.length;

  if (mixedCurrencies) {
    return { currency, mixedCurrencies, unavailableCount, total: 0, rows: [], slices: [] };
  }

  const totals = new Map<string, number>();
  for (const holding of priced) {
    const value = holding.marketValue ?? 0;
    if (value <= 0) continue;
    const label = dimension === 'sector'
      ? clean(holding.sector) || 'Unclassified'
      : (clean(holding.symbol) || clean(holding.instrumentId) || 'Unknown instrument').toUpperCase();
    totals.set(label, (totals.get(label) ?? 0) + value);
  }

  const total = [...totals.values()].reduce((sum, value) => sum + value, 0);
  const rows = [...totals.entries()]
    .map(([label, value]) => ({ label, value, percent: total > 0 ? value / total * 100 : 0 }))
    .sort((a, b) => b.value - a.value || a.label.localeCompare(b.label));

  const sliceLimit = Math.max(2, Math.floor(maxSlices));
  const slices = rows.length <= sliceLimit
    ? rows
    : [
        ...rows.slice(0, sliceLimit - 1),
        {
          label: 'Other',
          value: rows.slice(sliceLimit - 1).reduce((sum, row) => sum + row.value, 0),
          percent: rows.slice(sliceLimit - 1).reduce((sum, row) => sum + row.percent, 0)
        }
      ];

  return { currency, mixedCurrencies, unavailableCount, total, rows, slices };
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
