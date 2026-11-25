/**
 * Extra actions an EDITION adds to the New Order split button's drop-down (the "V" beside New Order), after the
 * community's own Quick Buy and Quick Sell.
 *
 * WHY THIS EXISTS. Some editions open their own order window: a sibling of New Order rather than a new order type
 * inside it. Its natural place is the same drop-down, and adding a second toolbar button beside New Order would make
 * the toolbar wider for every edition. Like `orderTypeExtensions.ts`, this is a narrow seam: the community toolbar
 * learns nothing about what the action does.
 *
 * THE CONTRACT. `registerNewOrderMenuItem` adds one entry; pressing it closes the drop-down and calls `run` with the
 * stock selected on the page (or null). `detail` is a short second line, as the quick orders show the stock. An edition
 * that should not offer the action right now simply unregisters it. A community build registers nothing, so the
 * drop-down shows only the quick orders. Memory only: a list of the registered items for the page's lifetime.
 */
import { writable, type Readable } from 'svelte/store';

export interface NewOrderMenuItem {
  /** Stable id, unique per item. */
  id: string;
  /** What the entry says. */
  label: string;
  /** A short second line, shown muted on the right. */
  detail?: string | null;
  /** A tooltip. */
  title?: string | null;
  run: (context: { selectedSymbol: string | null }) => void;
}

const registered = writable<NewOrderMenuItem[]>([]);

/** Every registered item, in registration order. */
export const newOrderMenuItems: Readable<NewOrderMenuItem[]> = { subscribe: registered.subscribe };

/** Adds (or replaces, by id) an item. Returns the function that removes it again. */
export function registerNewOrderMenuItem(item: NewOrderMenuItem): () => void {
  registered.update(list => [...list.filter(existing => existing.id !== item.id), item]);
  return () => registered.update(list => list.filter(existing => existing !== item));
}
