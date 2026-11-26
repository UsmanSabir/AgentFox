import type { WatchlistEntry } from './api';

export const watchlistListsKey = 'trading.watchlist.lists.v1';
export const listNameLimit = 40;
const listLimit = 30;
const symbolsPerList = 250;
const retentionMs = 180 * 24 * 60 * 60 * 1000;

export type WatchlistList = { name: string; symbols: string[] };
export type WatchlistFilter = { kind: 'sector' | 'list'; name: string; symbols: ReadonlySet<string> };

export function watchlistSectors(entries: readonly WatchlistEntry[]): WatchlistFilter[] {
  const groups = new Map<string, { name: string; symbols: Set<string> }>();
  for (const { symbol, sectorName } of entries) {
    const name = sectorName?.trim();
    if (!name) continue;
    const key = name.toLocaleLowerCase();
    if (!groups.has(key)) groups.set(key, { name, symbols: new Set() });
    groups.get(key)!.symbols.add(symbol);
  }
  return [...groups.values()].sort((a, b) => a.name.localeCompare(b.name))
    .map(group => ({ kind: 'sector', ...group }));
}

/** Groups whose name contains the query, ignoring case and surrounding spaces; an empty query keeps every group. */
export function matchingFilters<T extends { name: string }>(groups: readonly T[], query: string): T[] {
  const needle = query.trim().toLocaleLowerCase();
  return needle ? groups.filter(group => group.name.toLocaleLowerCase().includes(needle)) : [...groups];
}

function uniqueListName(lists: WatchlistList[], rawName: string): string {
  const name = rawName.trim();
  if (!name || name.length > listNameLimit) throw new Error(`Enter a name up to ${listNameLimit} characters.`);
  if (lists.some(list => list.name.toLocaleLowerCase() === name.toLocaleLowerCase())) {
    throw new Error('You already have a list with that name.');
  }
  return name;
}

export function addWatchlistList(lists: WatchlistList[], rawName: string): WatchlistList[] {
  if (lists.length >= listLimit) throw new Error(`You can keep up to ${listLimit} lists.`);
  const name = uniqueListName(lists, rawName);
  return [...lists, { name, symbols: [] }];
}

/** Apply only the memberships edited in this dialog, leaving changes from other tabs intact. */
export function changeWatchlistMembership(
  lists: WatchlistList[], symbol: string, before: readonly string[], after: readonly string[]
): WatchlistList[] {
  return lists.map(list => {
    const wasChecked = before.includes(list.name);
    const checked = after.includes(list.name);
    if (wasChecked === checked) return list;
    const symbols = new Set(list.symbols);
    if (checked) symbols.add(symbol);
    else symbols.delete(symbol);
    if (symbols.size > symbolsPerList) throw new Error(`A list can hold up to ${symbolsPerList} stocks.`);
    return { ...list, symbols: [...symbols] };
  });
}

/** One browser record expires 180 days after the last edit. Read sweeps expired and invalid data. */
export function readWatchlistLists(storage: Storage, now = Date.now()): WatchlistList[] {
  const raw = storage.getItem(watchlistListsKey);
  if (!raw) return [];
  try {
    const record = raw.length <= 256_000 ? JSON.parse(raw) : null;
    if (record && Number.isFinite(record.expiresAt) && record.expiresAt > now &&
        record.expiresAt <= now + retentionMs && Array.isArray(record.lists) && record.lists.length <= listLimit) {
      const lists: WatchlistList[] = [];
      for (const list of record.lists) {
        if (typeof list?.name !== 'string' || !Array.isArray(list.symbols) ||
            list.symbols.length > symbolsPerList ||
            list.symbols.some((symbol: unknown) => typeof symbol !== 'string' || !symbol || symbol.length > 24)) {
          throw new Error('Invalid saved list.');
        }
        lists.push({ name: uniqueListName(lists, list.name), symbols: [...new Set<string>(list.symbols)] });
      }
      return lists;
    }
  } catch { /* The record can only contain view preferences. Recover with All stocks. */ }
  storage.removeItem(watchlistListsKey);
  return [];
}

export function writeWatchlistLists(storage: Storage, lists: WatchlistList[], now = Date.now()) {
  if (lists.length) storage.setItem(watchlistListsKey, JSON.stringify({ expiresAt: now + retentionMs, lists }));
  else storage.removeItem(watchlistListsKey);
}
