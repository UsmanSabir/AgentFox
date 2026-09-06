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
