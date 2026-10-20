import type { WorkspaceCommand } from './workspaceComposition';

/** Match every word across the title and description, keeping the catalogue's stable order. */
export function searchWorkspaceCommands(commands: WorkspaceCommand[], query: string): WorkspaceCommand[] {
  const words = query.toLowerCase().trim().split(/\s+/).filter(Boolean);
  return commands.filter(command => {
    const text = `${command.label} ${command.keywords ?? ''}`.toLowerCase();
    return words.every(word => text.includes(word));
  });
}
