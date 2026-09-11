import type { TradingActivity } from './api';

/**
 * Candle-source announcements are useful diagnostics but routine progress. Warnings and errors from
 * the same subsystem always remain visible: a noise filter must never hide something actionable.
 */
export function isRoutineCandleUpdate(item: TradingActivity): boolean {
  return item.level === 'info' && item.source.trim().toLowerCase() === 'candles';
}

export function filterActivity(
  entries: readonly TradingActivity[],
  showRoutineCandles: boolean
): TradingActivity[] {
  return showRoutineCandles ? [...entries] : entries.filter(item => !isRoutineCandleUpdate(item));
}

/** Counts occurrences, not rows, because the activity log folds exact repeats into one row. */
export function routineCandleUpdateCount(entries: readonly TradingActivity[]): number {
  return entries
    .filter(isRoutineCandleUpdate)
    .reduce((total, item) => total + item.repeats + 1, 0);
}
