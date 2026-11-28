import type { TradingActivity } from './api';

// What the Activity panel shows, as pure functions. "Today" for the trader: two audiences the server assigns (trades,
// system), a stock per row when there is one, and a kind in plain words.

export type ActivityView = 'trades' | 'system';
export type ActivityKind = 'order' | 'stop' | 'trigger' | 'strategy' | 'copy' | 'notified' | 'market' | 'system';
export type ActivityKindFilter = 'all' | 'problems' | ActivityKind;

const kindBySource: Record<string, ActivityKind> = {
  orders: 'order', stops: 'stop', armed: 'trigger', strategy: 'strategy', copy: 'copy', notified: 'notified', market: 'market'
};

export const kindLabel: Record<ActivityKind, string> = {
  order: 'Order', stop: 'Stop', trigger: 'Trigger', strategy: 'Strategy', copy: 'Copy', notified: 'Notified',
  market: 'Market', system: 'System'
};

/** The kinds the Trades view offers as a filter, in the order a trader thinks of them. */
export const tradeKindFilters: { id: ActivityKindFilter; label: string }[] = [
  { id: 'all', label: 'All' }, { id: 'problems', label: 'Problems' }, { id: 'order', label: 'Orders' },
  { id: 'stop', label: 'Stops' }, { id: 'trigger', label: 'Triggers' }, { id: 'strategy', label: 'Strategy' },
  { id: 'copy', label: 'Copy' }, { id: 'notified', label: 'Notified' }
];

export function kindOf(item: Pick<TradingActivity, 'source'>): ActivityKind {
  return kindBySource[item.source.trim().toLowerCase()] ?? 'system';
}

/**
 * Which view a row belongs to. The server decides (`audience`); a host too old to send it gets the server's own rule
 * applied here, so nothing actionable can disappear from Trades: a warning or an error is always a trade.
 */
export function audienceOf(item: TradingActivity): ActivityView {
  if (item.audience === 'trades' || item.audience === 'system') return item.audience;
  if (item.level !== 'info' || kindOf(item) === 'notified') return 'trades';
  return item.symbol && kindOf(item) !== 'system' && kindOf(item) !== 'market' ? 'trades' : 'system';
}

/** Warnings and errors appear in BOTH views: a filter must never hide something actionable. */
export const inView = (item: TradingActivity, view: ActivityView) =>
  item.level !== 'info' || audienceOf(item) === view;

/** The message without a leading "SYMBOL: ", since the stock has its own column. */
export function displayMessage(item: Pick<TradingActivity, 'message' | 'symbol'>): string {
  const symbol = item.symbol;
  if (!symbol) return item.message;
  const prefix = symbol + ':';
  return item.message.toUpperCase().startsWith(prefix) ? item.message.slice(prefix.length).trimStart() : item.message;
}

export interface ActivityFilters { view: ActivityView; kind: ActivityKindFilter; stock: string; showRoutineCandles: boolean; }

export function filterActivity(entries: readonly TradingActivity[], filters: ActivityFilters): TradingActivity[] {
  const stock = filters.stock.trim().toUpperCase();
  return entries.filter(item =>
    inView(item, filters.view)
    && (filters.view !== 'system' || filters.showRoutineCandles || !isRoutineCandleUpdate(item))
    && (filters.kind === 'all' || (filters.kind === 'problems' ? item.level !== 'info' : kindOf(item) === filters.kind))
    && (!stock || (item.symbol ?? '').startsWith(stock)));
}

/**
 * Candle-source announcements are useful diagnostics but routine progress. Warnings and errors from
 * the same subsystem always remain visible: a noise filter must never hide something actionable.
 */
export function isRoutineCandleUpdate(item: TradingActivity): boolean {
  return item.level === 'info' && item.source.trim().toLowerCase() === 'candles';
}

/** Counts occurrences, not rows, because the activity log folds exact repeats into one row. */
export function routineCandleUpdateCount(entries: readonly TradingActivity[]): number {
  return entries
    .filter(isRoutineCandleUpdate)
    .reduce((total, item) => total + item.repeats + 1, 0);
}

/**
 * Folds a page of changed rows (added, or repeated in place) into what the panel holds: by `seq`, newest first, and
 * nothing older than the server still keeps. Never mutates its inputs.
 */
export function mergeActivity(held: readonly TradingActivity[], changed: readonly TradingActivity[],
  oldestSeq: number | null | undefined): TradingActivity[] {
  // null: the server holds nothing today (a new PKT day, or a restart). undefined: an older host that does not say.
  if (oldestSeq === null) return [];
  const bySeq = new Map(held.map(item => [item.seq, item]));
  for (const item of changed) bySeq.set(item.seq, item);
  return [...bySeq.values()]
    .filter(item => oldestSeq === undefined || item.seq >= oldestSeq)
    .sort((a, b) => b.seq - a.seq);
}

/** How many of the held rows each view would show — the counts on the view switch. */
export function viewCounts(entries: readonly TradingActivity[]): Record<ActivityView, number> {
  return { trades: entries.filter(e => inView(e, 'trades')).length, system: entries.filter(e => inView(e, 'system')).length };
}
