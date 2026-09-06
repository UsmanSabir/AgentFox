import type { SerializedDockview } from 'dockview';

export const WORKSPACE_RETENTION_MS = 180 * 24 * 60 * 60 * 1000;
export const MAX_LAYOUT_LENGTH = 100_000;
export interface AutoHideTray { ids: string[]; active: string; height: number }
export interface WorkspaceLayout {
  version: 1;
  edition: string;
  savedAt: number;
  preset: string;
  layout: SerializedDockview;
  /** View metadata only. No peek state or panel contents. Missing means pinned. */
  bottomTray?: AutoHideTray;
  maximized?: string;
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
    const tray = value.bottomTray;
    if (value.maximized !== undefined && (typeof value.maximized !== 'string' || !Object.hasOwn(layout.panels,value.maximized))) return null;
    if (tray !== undefined && (!tray || typeof tray !== 'object' || !Array.isArray(tray.ids) || !tray.ids.length || tray.ids.length > allowedIds.length
      || new Set(tray.ids).size !== tray.ids.length || !tray.ids.includes(tray.active)
      || tray.ids.some(id => !allowedIds.includes(id) || id in layout.panels)
      || !Number.isFinite(tray.height) || tray.height < 130 || tray.height > 900)) return null;
    return value;
  } catch { return null; }
}

/** Only view geometry and stable metadata are persisted, never order drafts or component parameters. */
export function saveWorkspaceLayout(
  edition: string, preset: string, layout: SerializedDockview, now = Date.now(), bottomTray?: AutoHideTray, maximized?: string
): WorkspaceLayout {
  const copy = JSON.parse(JSON.stringify(layout)) as SerializedDockview;
  for (const panel of Object.values(copy.panels)) delete panel.params;
  return { version: 1, edition, savedAt: now, preset, layout: copy,
    ...(maximized && Object.hasOwn(copy.panels,maximized) ? {maximized} : {}),
    ...(bottomTray ? { bottomTray:{ ids:[...bottomTray.ids], active:bottomTray.active, height:bottomTray.height } } : {}) };
}
