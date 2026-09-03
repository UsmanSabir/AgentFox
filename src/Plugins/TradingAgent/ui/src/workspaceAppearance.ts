export type WorkspaceTheme = 'light' | 'dark';
export const APPEARANCE_RETENTION_MS = 180 * 24 * 60 * 60 * 1000;
/** One tiny browser preference; caller sweeps invalid/expired values at workspace entry. */
export function readWorkspaceAppearance(raw: string | null, now = Date.now()): WorkspaceTheme | null {
  if (!raw || raw.length > 256) return null;
  try {
    const value = JSON.parse(raw);
    return value.version === 1 && ['light','dark'].includes(value.theme) && Number.isFinite(value.savedAt)
      && value.savedAt <= now && now - value.savedAt <= APPEARANCE_RETENTION_MS ? value.theme : null;
  } catch { return null; }
}
export function saveWorkspaceAppearance(theme: WorkspaceTheme, now = Date.now()): string {
  return JSON.stringify({version:1,theme,savedAt:now});
}
