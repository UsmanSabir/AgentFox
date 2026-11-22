/**
 * Order types an EDITION adds to the order composer — the New Order dialog and the docked ticket.
 *
 * WHY THIS EXISTS. The composer's own order types come from the server's intent registry, and each one
 * is a set of fields this repo owns. An edition that adds a whole new kind of order (a rule-based order,
 * whose condition is a document only that edition can read) needs a tile in the same "Order type" row
 * and a place to edit it — without forking the composer and without adding fields to it.
 *
 * THE CONTRACT. `registerOrderTypeExtension` adds one tile after the composer's own. Pressing it hands
 * the composer's body over to `editor` — one dialog at a time, never a second modal on top — carrying
 * what was already chosen. The editor:
 *  - receives `symbol`, `action` ('BUY' | 'SELL'), `quantity` (number | null), `price` (number | null),
 *    `docked` (boolean) and `armedOrder` (the `ArmedOrder` being edited, or null for a new one);
 *  - dispatches `back` to return to the composer with those values intact (or, when editing, to close),
 *    `done` with `{ title, detail }` once it has armed something, `changed` when lists should re-read, and
 *    `side` ('BUY' | 'SELL') when the person changes side in the editor, so the dialog's header and colours follow
 *    it (the composer's own side choice resets, exactly as switching side there does).
 * It submits through the ordinary `/trading/armed-orders` route, so every server check still applies.
 *
 * `triggerKind` names the armed-order kind this extension owns. The armed-orders panel renders
 * `armedRowDetail` (receives `order`; renders nothing when it has nothing to say) under each such row,
 * and offers Edit, which opens the composer straight into `editor` with `armedOrder` set.
 *
 * A community build registers nothing, so no tile appears and nothing here runs.
 */
import { writable, type Readable } from 'svelte/store';
import type { SymbolExtensionComponent } from './symbolExtensions';

export interface OrderTypeExtension {
  /** Stable id, unique per extension. */
  id: string;
  /** Tile label in the "Order type" row. */
  label: string;
  /** The armed-order trigger kind this extension arms and can edit (e.g. 'Rule'). */
  triggerKind: string;
  editor: SymbolExtensionComponent;
  armedRowDetail?: SymbolExtensionComponent | null;
}

const registered = writable<OrderTypeExtension[]>([]);

/** Every registered extension, in registration order. */
export const orderTypeExtensions: Readable<OrderTypeExtension[]> = { subscribe: registered.subscribe };

/** Adds (or replaces, by id) an extension. Returns the function that removes it again. */
export function registerOrderTypeExtension(extension: OrderTypeExtension): () => void {
  registered.update(list => [...list.filter(item => item.id !== extension.id), extension]);
  return () => registered.update(list => list.filter(item => item !== extension));
}

/** The extension that owns `triggerKind`, if any. */
export function extensionForTriggerKind(list: OrderTypeExtension[], triggerKind: string | null | undefined) {
  return triggerKind ? list.find(item => item.triggerKind === triggerKind) ?? null : null;
}
