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
}

export function workspacePresetPanels(panels: WorkspacePanel[], preset?: WorkspacePreset): WorkspacePanel[] {
  if (!preset?.columns) return panels;
  const ids = new Set(preset.columns.flatMap(column => column.panels));
  return panels.filter(panel => ids.has(panel.id));
}
export interface WorkspaceCommand {
  id: string;
  label: string;
  run: () => void | Promise<void>;
  disabled?: () => string | null;
}
export interface WorkspaceComposition {
  attachPanel: (id: string, element: HTMLElement) => () => void;
  registerCommand: (command: WorkspaceCommand) => () => void;
  focusPanel: (id: string) => void;
}
