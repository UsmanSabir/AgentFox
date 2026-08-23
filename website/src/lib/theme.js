export const THEME_STORAGE_KEY = 'agentfox.marketing.theme';

/** @param {string | null | undefined} savedTheme */
export function resolveInitialTheme(savedTheme) {
  return savedTheme === 'light' ? 'light' : 'dark';
}

/** @param {'dark' | 'light'} theme */
export function nextTheme(theme) {
  return theme === 'dark' ? 'light' : 'dark';
}
