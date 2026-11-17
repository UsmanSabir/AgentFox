import type { SerializedDockview } from 'dockview';

export const WORKSPACE_RETENTION_MS = 180 * 24 * 60 * 60 * 1000;
export const MAX_LAYOUT_LENGTH = 100_000;
/** Where a workstation keeps the layout saved as "My view": beside its working layout, never in place of it. */
export const myViewStorageKey = (storageKey: string) => `${storageKey}.my-view`;
/** An explicitly saved preset, separate from the automatically remembered working layout. */
export const presetViewStorageKey = (storageKey: string, preset: string) => `${storageKey}.preset.${preset}`;

/** Aliases rename view metadata only; an unknown id or a retired target remains invalid. */
export function resolveWorkspacePreset(
  id: string | null, presets: readonly string[], aliases: Readonly<Record<string, string>> = {}
): string | null {
  if (id === null) return null;
  if (presets.includes(id)) return id;
  const renamed = Object.hasOwn(aliases, id) ? aliases[id] : undefined;
  return renamed && presets.includes(renamed) ? renamed : null;
}
type GridNode = { type?: unknown; data?: unknown; size?: unknown };

/**
 * Rewrites retired panel ids in a saved record, before it is validated, so a layout the operator
 * arranged survives a panel being renamed or merged. Without this, readWorkspaceLayout refuses any
 * record naming an id that has left the catalogue and the whole arrangement falls back to the preset.
 *
 * Several retired ids may map to ONE panel (and a retired id may map onto a panel already placed): the first
 * place met keeps it, grid before trays, and every later one is dropped. A group emptied by that loses
 * its space to a neighbour so the rest of the grid keeps its proportions. Only view metadata is touched.
 */
export function migrateWorkspacePanels(value: unknown, aliases: Readonly<Record<string, string>>): void {
  if (!value || typeof value !== 'object' || !Object.keys(aliases).length) return;
  const record = value as { layout?: { grid?: { root?: GridNode }; panels?: Record<string, Record<string, unknown>> };
    autoHide?: Record<string, { ids?: unknown; active?: unknown } | undefined>;
    bottomTray?: { ids?: unknown; active?: unknown }; maximized?: unknown;
    known?: unknown; panelWidths?: Record<string, unknown> };
  const retired = (id: unknown): id is string => typeof id === 'string' && Object.hasOwn(aliases, id);
  const target = (id: string) => retired(id) ? aliases[id] : id;
  const layout = record.layout;
  const root = layout?.grid?.root;
  if (!layout?.panels || typeof layout.panels !== 'object' || !root) return;

  const leaves: { views: unknown[] }[] = [];
  const collect = (node: GridNode) => {
    if (node?.type === 'leaf' && node.data && Array.isArray((node.data as { views?: unknown }).views)) leaves.push(node.data as { views: unknown[] });
    else if (node?.type === 'branch' && Array.isArray(node.data)) (node.data as GridNode[]).forEach(collect);
  };
  collect(root);
  const placed = new Set<string>(leaves.flatMap(leaf => leaf.views.filter((v): v is string => typeof v === 'string' && !retired(v))));
  const source = new Map<string, string>();

  // Returns whether the node still holds a panel.
  const walk = (node: GridNode): boolean => {
    if (node?.type === 'leaf') {
      const data = node.data as { views?: unknown[]; activeView?: unknown };
      if (!Array.isArray(data?.views)) return true;
      data.views = data.views.flatMap(view => {
        if (!retired(view)) return [view];
        const to = target(view);
        if (placed.has(to)) return [];
        placed.add(to); source.set(to, view);
        return [to];
      });
      if (retired(data.activeView)) data.activeView = target(data.activeView);
      if (data.activeView !== undefined && !data.views.includes(data.activeView)) data.activeView = data.views[0];
      return data.views.length > 0;
    }
    if (node?.type !== 'branch' || !Array.isArray(node.data)) return true;
    const children = node.data as GridNode[];
    const kept: GridNode[] = [];
    let freed = 0;
    for (const child of children) {
      if (walk(child)) {
        if (freed && typeof child.size === 'number') { child.size += freed; freed = 0; }
        kept.push(child);
      } else if (typeof child.size === 'number') {
        const previous = kept[kept.length - 1];
        if (previous && typeof previous.size === 'number') previous.size += child.size; else freed += child.size;
      }
    }
    node.data = kept;
    return kept.length > 0;
  };
  walk(root);

  const panels: Record<string, Record<string, unknown>> = {};
  for (const [id, state] of Object.entries(layout.panels)) {
    if (!retired(id)) { panels[id] = state; continue; }
    const to = target(id);
    if (source.get(to) !== id) continue;
    panels[to] = { ...state, id: to, contentComponent: to, ...(state?.tabComponent === id ? { tabComponent: to } : {}) };
  }
  layout.panels = panels;

  const parked = new Set<string>();
  const migrateTray = (tray: { ids?: unknown; active?: unknown } | undefined) => {
    if (!tray || !Array.isArray(tray.ids)) return true;
    tray.ids = tray.ids.flatMap(id => {
      const to = typeof id === 'string' ? target(id) : id;
      if (typeof to !== 'string') return [to];
      if ((retired(id) && placed.has(to)) || parked.has(to)) return [];
      parked.add(to);
      return [to];
    });
    if (retired(tray.active)) tray.active = target(tray.active);
    if (!(tray.ids as unknown[]).includes(tray.active)) tray.active = (tray.ids as unknown[])[0];
    return (tray.ids as unknown[]).length > 0;
  };
  if (record.autoHide && typeof record.autoHide === 'object') {
    for (const edge of Object.keys(record.autoHide)) if (!migrateTray(record.autoHide[edge])) delete record.autoHide[edge];
    if (!Object.keys(record.autoHide).length) delete record.autoHide;
  }
  if (record.bottomTray && !migrateTray(record.bottomTray)) delete record.bottomTray;

  if (retired(record.maximized)) {
    const to = target(record.maximized);
    if (Object.hasOwn(panels, to)) record.maximized = to; else delete record.maximized;
  }
  if (Array.isArray(record.known)) record.known = [...new Set(record.known.map(id => typeof id === 'string' ? target(id) : id))];
  if (record.panelWidths && typeof record.panelWidths === 'object') {
    for (const id of Object.keys(record.panelWidths)) {
      if (!retired(id)) continue;
      const width = record.panelWidths[id];
      delete record.panelWidths[id];
      if (!Object.hasOwn(record.panelWidths, target(id))) record.panelWidths[target(id)] = width;
    }
  }
}

export type AutoHideEdge = 'left' | 'right' | 'bottom';
export interface AutoHideTray { ids: string[]; active: string; size: number }
export type AutoHideTrays = Partial<Record<AutoHideEdge, AutoHideTray>>;
export interface WorkspaceLayout {
  version: 1;
  edition: string;
  savedAt: number;
  preset: string;
  layout: SerializedDockview;
  /** View metadata only. No peek state or panel contents. Missing means every edge is pinned. */
  autoHide?: AutoHideTrays;
  /** Retired bottom-only shape, accepted on read so existing operator layouts migrate in place. */
  bottomTray?: { ids: string[]; active: string; height: number };
  maximized?: string;
  /**
   * Panel ids the catalogue offered when this was saved — view metadata, like everything else here.
   *
   * <p>It exists to tell "the operator hid this panel" apart from "this panel did not exist yet",
   * which a layout alone cannot express: both look like an id that is simply absent. Without it a
   * panel added to either edition is invisible for ever to everyone who has already arranged their
   * view, and visible only to a first-time user — the reverse of who goes looking for a new feature.
   * Absent on records written before this field shipped; see the one-time adoption in
   * DockWorkspace.</p>
   */
  known?: string[];
  /** Explicit panel widths (for example the watchlist's compact toggle), retained across presets. */
  panelWidths?: Record<string, number>;
}

/** Validate before handing browser preferences to Dockview; never accept components/params as code. */
export function readWorkspaceLayout(
  raw: string | null, edition: string, allowedIds: readonly string[], presets: readonly string[],
  now = Date.now(), presetAliases: Readonly<Record<string, string>> = {},
  panelAliases: Readonly<Record<string, string>> = {}
): WorkspaceLayout | null {
  if (!raw || raw.length > MAX_LAYOUT_LENGTH) return null;
  try {
    const value = JSON.parse(raw) as WorkspaceLayout;
    migrateWorkspacePanels(value, panelAliases);
    const preset = resolveWorkspacePreset(value.preset, presets, presetAliases);
    if (value.version !== 1 || value.edition !== edition || !preset
      || !Number.isFinite(value.savedAt) || value.savedAt > now
      || now - value.savedAt > WORKSPACE_RETENTION_MS) return null;
    value.preset = preset;
    if (value.panelWidths !== undefined) {
      if (!value.panelWidths || typeof value.panelWidths !== 'object' || Array.isArray(value.panelWidths)
        || Object.entries(value.panelWidths).some(([id, width]) => !allowedIds.includes(id)
          || !Number.isFinite(width) || width < 180 || width > 100_000)) return null;
    }
    const layout = value.layout;
    if (!layout || !layout.grid || !layout.panels || Array.isArray(layout.panels)) return null;
    // Floating/pop-out windows are not part of this first full-page contract.
    if (layout.floatingGroups?.length || layout.popoutGroups?.length) return null;
    for (const [id, panel] of Object.entries(layout.panels)) {
      if (!allowedIds.includes(id) || panel.id !== id || panel.contentComponent !== id
        || (panel.params && Object.keys(panel.params).length)) return null;
    }
    // Sanitised, not rejected, and deliberately so: this list is a HINT about which panels are new,
    // never anything handed to Dockview as code. Rejecting the whole record because one id has since
    // left the catalogue would throw away a layout the operator arranged over something that cannot
    // affect what is rendered.
    if (value.known !== undefined) {
      if (!Array.isArray(value.known) || value.known.length > allowedIds.length) return null;
      value.known = value.known.filter(
        (id): id is string => typeof id === 'string' && allowedIds.includes(id));
    }
    if (value.maximized !== undefined && (typeof value.maximized !== 'string' || !Object.hasOwn(layout.panels,value.maximized))) return null;
    if (value.autoHide !== undefined && value.bottomTray !== undefined) return null;
    if (value.bottomTray !== undefined) {
      const tray = value.bottomTray;
      if (!validTray(tray, 'bottom', allowedIds, layout.panels, new Set(), 'height')) return null;
      value.autoHide = { bottom:{ ids:[...tray.ids], active:tray.active, size:tray.height } };
      delete value.bottomTray;
    }
    if (value.autoHide !== undefined) {
      if (!value.autoHide || typeof value.autoHide !== 'object' || Array.isArray(value.autoHide)) return null;
      const keys = Object.keys(value.autoHide);
      if (keys.some(key => !['left','right','bottom'].includes(key))) return null;
      const parked = new Set<string>();
      for (const edge of keys as AutoHideEdge[]) {
        if (!validTray(value.autoHide[edge], edge, allowedIds, layout.panels, parked, 'size')) return null;
      }
    }
    return value;
  } catch { return null; }
}

function validTray(
  candidate: unknown, edge: AutoHideEdge, allowedIds: readonly string[],
  placed: Record<string, unknown>, parked: Set<string>, sizeKey: 'size' | 'height'
) {
  if (!candidate || typeof candidate !== 'object') return false;
  const tray = candidate as { ids?: unknown; active?: unknown; size?: unknown; height?: unknown };
  if (!Array.isArray(tray.ids) || !tray.ids.length || tray.ids.length > allowedIds.length
    || new Set(tray.ids).size !== tray.ids.length || typeof tray.active !== 'string'
    || !tray.ids.includes(tray.active)) return false;
  for (const id of tray.ids) {
    if (typeof id !== 'string' || !allowedIds.includes(id) || id in placed || parked.has(id)) return false;
    parked.add(id);
  }
  const size = tray[sizeKey];
  const minimum = edge === 'bottom' ? 130 : 180;
  return typeof size === 'number' && Number.isFinite(size) && size >= minimum && size <= 900;
}

/** Only view geometry and stable metadata are persisted, never order drafts or component parameters. */
export function saveWorkspaceLayout(
  edition: string, preset: string, layout: SerializedDockview, now = Date.now(), autoHide?: AutoHideTrays, maximized?: string,
  known?: readonly string[], panelWidths?: Readonly<Record<string, number>>
): WorkspaceLayout {
  const copy = JSON.parse(JSON.stringify(layout)) as SerializedDockview;
  for (const panel of Object.values(copy.panels)) delete panel.params;
  const savedTrays = Object.fromEntries(
    (['left','right','bottom'] as const).flatMap(edge => {
      const tray = autoHide?.[edge];
      return tray ? [[edge,{ ids:[...tray.ids], active:tray.active, size:tray.size }]] : [];
    })) as AutoHideTrays;
  return { version: 1, edition, savedAt: now, preset, layout: copy,
    ...(known?.length ? { known: [...known] } : {}),
    ...(panelWidths && Object.keys(panelWidths).length ? { panelWidths: { ...panelWidths } } : {}),
    ...(maximized && Object.hasOwn(copy.panels,maximized) ? {maximized} : {}),
    ...(Object.keys(savedTrays).length ? { autoHide:savedTrays } : {}) };
}
