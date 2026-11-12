export const SIDEBAR_COLLAPSED_KEY = 'agentfox.sidebarCollapsed';

/** Invalid or absent values leave the navigation rail at its compact default. */
export function parseSidebarCollapsed(raw: string | null): boolean | null {
  if (raw === 'true') return true;
  if (raw === 'false') return false;
  return null;
}

export function serializeSidebarCollapsed(collapsed: boolean): string {
  return collapsed ? 'true' : 'false';
}
