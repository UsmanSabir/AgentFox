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
  /** Resize a panel's group/side drawer; null restores its prior width. Geometry uses the existing layout record. */
  setPanelWidth?: (id: string, width: number | null) => void;
}
