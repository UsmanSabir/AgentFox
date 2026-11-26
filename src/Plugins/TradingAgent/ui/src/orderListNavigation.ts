/** Safe row entry: focuses a control, never activates it. */
export function focusOrderControls(event: Event) {
  const target = event.currentTarget as HTMLElement;
  target.closest('[data-order-row]')?.querySelector<HTMLElement>('button:not(:disabled):not([data-order-focus]),input:not(:disabled),summary')?.focus();
}

/**
 * A row's symbol is both the keyboard entry to the row and a link to the stock. A pointer click
 * (`detail` counts presses) selects the stock; Enter or Space reaches it as a click with `detail` 0 and
 * keeps entering the row, so keyboard navigation is unchanged.
 */
export function isPointerClick(event: Pick<MouseEvent, 'detail'>): boolean {
  return event.detail > 0;
}

/** The Buy / Sell view filter of Persistent orders. A stop-loss is a SELL. */
export type OrderSideFilter = 'all' | 'BUY' | 'SELL';
export function matchesSide(action: string | null | undefined, side: OrderSideFilter): boolean {
  return side === 'all' || (action ?? '').toUpperCase() === side;
}

export function orderRowIndex(key: string, index: number, count: number): number | null {
  if (index < 0 || count < 1) return null;
  if (key === 'ArrowDown') return Math.min(count - 1, index + 1);
  if (key === 'ArrowUp') return Math.max(0, index - 1);
  if (key === 'Home') return 0;
  if (key === 'End') return count - 1;
  return null;
}

/** List navigation only. Never dispatches a trading action or synthesizes a click. */
export function orderListNavigation(node: HTMLElement, enabled: boolean) {
  function key(event: KeyboardEvent) {
    if (!enabled || event.defaultPrevented || event.isComposing || event.repeat || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
    const target = event.target instanceof HTMLElement ? event.target : null;
    if (!target || target.closest('input:not([type="checkbox"]),select,textarea,[contenteditable="true"],dialog')) return;
    const row = target.closest<HTMLElement>('[data-order-row]');
    if (!row || !node.contains(row)) return;
    if (event.key === 'Enter' && target === row) {
      event.preventDefault();
      row.querySelector<HTMLElement>('summary,button:not(:disabled),input:not(:disabled)')?.focus();
      return;
    }
    const rows = [...node.querySelectorAll<HTMLElement>('[data-order-row]')].filter(row => row.getClientRects().length);
    const index = rows.indexOf(row);
    const nextIndex = orderRowIndex(event.key, index, rows.length);
    if (nextIndex == null) return;
    const next = rows[nextIndex].querySelector<HTMLElement>('[data-order-focus]') ?? rows[nextIndex];
    event.preventDefault();
    next?.focus(); next?.scrollIntoView({block:'nearest'});
  }
  node.addEventListener('keydown',key);
  return {update(value: boolean) {enabled = value;},destroy() {node.removeEventListener('keydown',key);}};
}
