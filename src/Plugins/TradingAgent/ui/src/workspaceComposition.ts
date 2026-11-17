/**
 * Neutral view-placement contract. The renderer owns DOM placement only; the producer retains
 * Svelte instances, state, dialogs and API lifecycles. No trading payload is part of this contract.
 */
export type WorkspaceRegion = 'left' | 'center' | 'right' | 'bottom';
export interface WorkspacePanel {
  id: string;
  title: string;
  region: WorkspaceRegion;
  description: string;
}
/** Optional focused layouts reuse producer instances while limiting the panels they expose. */
export interface WorkspacePreset {
  id: string;
  label: string;
  title?: string;
  showToolbar?: boolean;
  active: Partial<Record<WorkspaceRegion, string>>;
  columns?: { panels: string[]; active: string; weight?: number }[];
  /** Panels the layout starts with auto-hidden on an edge (the edge strip opens them), like Unpin does. */
  autoHide?: Partial<Record<'left' | 'right' | 'bottom', { panels: string[]; active: string; size?: number }>>;
  /** A columns layout offers only the panels it places by default. True keeps every panel in Find panel. */
  offersUnplaced?: boolean;
}

/** The panels a preset places: its columns, then its auto-hidden edges. */
export function presetPlacedPanels(preset?: WorkspacePreset): string[] {
  return [...new Set([...(preset?.columns ?? []).flatMap(column => column.panels),
    ...Object.values(preset?.autoHide ?? {}).flatMap(tray => tray?.panels ?? [])])];
}

export function workspacePresetPanels(panels: WorkspacePanel[], preset?: WorkspacePreset): WorkspacePanel[] {
  if (!preset?.columns || preset.offersUnplaced) return panels;
  const ids = new Set(presetPlacedPanels(preset));
  return panels.filter(panel => ids.has(panel.id));
}
export interface WorkspaceCommand {
  id: string;
  label: string;
  /** Optional search terms supplied by the producer, such as a panel's description. */
  keywords?: string;
  run: () => void | Promise<void>;
  disabled?: () => string | null;
}
export interface WorkspaceComposition {
  attachPanel: (id: string, element: HTMLElement, onVisibility?: (visible: boolean) => void) => () => void;
  registerCommand: (command: WorkspaceCommand) => () => void;
  focusPanel: (id: string) => void;
  /** Resize a panel's group/side drawer; null restores its prior width. The choice survives presets and reloads. */
  setPanelWidth?: (id: string, width: number | null) => void;
}

// ── Hidden panels ──────────────────────────────────────────────────────────────────────────────
// A producer may hide a catalogued panel (an account that has no use for it) WITHOUT removing it from the catalogue. The panel then stays a
// valid id, so a saved layout, view or My view that holds it is not reset, but it is not offered, not placed by a default build, not
// adopted and not recorded as known: when it stops being hidden it is adopted like any panel the catalogue has gained.

/** The catalogue without the hidden panels: what is offered and what a default build places. */
export function visiblePanels(panels: WorkspacePanel[], hidden: ReadonlySet<string>): WorkspacePanel[] {
  return hidden.size ? panels.filter(panel => !hidden.has(panel.id)) : panels;
}

/** The ids a saved layout records as known: every catalogued panel the workspace is not hiding. */
export function knownPanelIds(panels: readonly WorkspacePanel[], hidden: ReadonlySet<string>): string[] {
  return panels.filter(panel => !hidden.has(panel.id)).map(panel => panel.id);
}

/**
 * A preset as a default build places it, minus the hidden panels: a column left empty is dropped, a column or tray whose active panel
 * went starts on its first remaining panel, and a tray left empty is dropped. The preset itself is not changed.
 */
export function presetWithoutHidden(preset: WorkspacePreset, hidden: ReadonlySet<string>): WorkspacePreset {
  if (!hidden.size) return preset;
  const trim = <T extends { panels: string[]; active: string }>(group: T): T | null => {
    const kept = group.panels.filter(id => !hidden.has(id));
    return kept.length ? { ...group, panels: kept, active: kept.includes(group.active) ? group.active : kept[0] } : null;
  };
  const columns = preset.columns?.map(trim).filter((c): c is NonNullable<typeof c> => c !== null);
  const autoHide = preset.autoHide && Object.fromEntries(Object.entries(preset.autoHide)
    .flatMap(([edge, tray]) => { const kept = tray && trim(tray); return kept ? [[edge, kept]] : []; }));
  return {
    ...preset,
    ...(columns ? { columns } : {}),
    ...(autoHide ? { autoHide: autoHide as WorkspacePreset['autoHide'] } : {}),
    active: Object.fromEntries(Object.entries(preset.active).filter(([, id]) => !hidden.has(id as string))) as WorkspacePreset['active']
  };
}

/**
 * The offered panels to place that a saved layout has not seen: not in `seen` and not already placed (docked or in a tray). A hidden
 * panel is never adopted, and `seen` need not name it.
 */
export function adoptablePanelIds(offered: readonly string[], seen: ReadonlySet<string>, hidden: ReadonlySet<string>,
  isPlaced: (id: string) => boolean): string[] {
  return offered.filter(id => !hidden.has(id) && !seen.has(id) && !isPlaced(id));
}
