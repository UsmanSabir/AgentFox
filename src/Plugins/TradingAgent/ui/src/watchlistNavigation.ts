/** View-only keyboard helpers: identities survive quote refreshes and reordering. */
export type WatchlistAction = 'order' | 'pin' | 'alerts' | 'automation' | 'history' | 'remove' | 'up' | 'down';

export function retainedWatchlistFocus(symbols: readonly string[], focused: string | null, selected: string | null) {
  return (focused && symbols.includes(focused) ? focused : null)
    ?? (selected && symbols.includes(selected) ? selected : null) ?? symbols[0] ?? null;
}

export function watchlistGridTarget(key: string, row: number, column: number, count: number, page: number, control = false) {
  if (!count) return null;
  let nextRow = row, nextColumn = column;
  switch (key) {
    case 'ArrowDown': nextRow++; break;
    case 'ArrowUp': nextRow--; break;
    case 'ArrowRight': nextColumn++; break;
    case 'ArrowLeft': nextColumn--; break;
    case 'PageDown': nextRow += Math.max(1, page); break;
    case 'PageUp': nextRow -= Math.max(1, page); break;
    case 'Home': nextColumn = 0; if (control) nextRow = 0; break;
    case 'End': nextColumn = 2; if (control) nextRow = count - 1; break;
    default: return null;
  }
  return { row:Math.max(0, Math.min(count - 1, nextRow)), column:Math.max(0, Math.min(2, nextColumn)) };
}

/** Reuse the same server reorder endpoint as drag/drop, without crossing the pin boundary. */
export function moveWatchlistRow<T extends { symbol:string; pinned:boolean }>(entries: readonly T[], symbol: string, delta: -1 | 1): T[] | null {
  const from = entries.findIndex(e => e.symbol === symbol);
  const to = from + delta;
  if (from < 0 || to < 0 || to >= entries.length || entries[from].pinned !== entries[to].pinned) return null;
  const result = [...entries];
  [result[from], result[to]] = [result[to], result[from]];
  return result;
}
