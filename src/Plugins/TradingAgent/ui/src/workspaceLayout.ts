import type { SerializedDockview } from 'dockview';

export const WORKSPACE_RETENTION_MS = 180 * 24 * 60 * 60 * 1000;
export const MAX_LAYOUT_LENGTH = 100_000;
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
}

/** Validate before handing browser preferences to Dockview; never accept components/params as code. */
export function readWorkspaceLayout(
  raw: string | null, edition: string, allowedIds: readonly string[], presets: readonly string[],
  now = Date.now()
): WorkspaceLayout | null {
  if (!raw || raw.length > MAX_LAYOUT_LENGTH) return null;
  try {
    const value = JSON.parse(raw) as WorkspaceLayout;
    if (value.version !== 1 || value.edition !== edition || !presets.includes(value.preset)
      || !Number.isFinite(value.savedAt) || value.savedAt > now
      || now - value.savedAt > WORKSPACE_RETENTION_MS) return null;
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
  known?: readonly string[]
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
    ...(maximized && Object.hasOwn(copy.panels,maximized) ? {maximized} : {}),
    ...(Object.keys(savedTrays).length ? { autoHide:savedTrays } : {}) };
}
