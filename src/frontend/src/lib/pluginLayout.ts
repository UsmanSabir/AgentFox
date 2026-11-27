/** Only the currently mounted, same-origin plugin frame may request temporary host layout changes. */
export function pluginSidebarRequest(
  origin: string, hostOrigin: string, source: unknown, frameWindow: unknown, data: unknown
): 'collapsed' | 'default' | null {
  if (origin !== hostOrigin || !frameWindow || source !== frameWindow || !data || typeof data !== 'object') return null;
  const message = data as { type?: unknown; sidebar?: unknown };
  return message.type === 'agentfox:layout' && (message.sidebar === 'collapsed' || message.sidebar === 'default')
    ? message.sidebar : null;
}

/** Longest tab-title prefix a plugin frame may set; a longer one is refused rather than cut. */
export const PLUGIN_TITLE_PREFIX_MAX = 48;

/**
 * A plugin frame's request to lead the browser tab's title with a short live reading (premium's KSE figure).
 * Same rule as the sidebar: only the mounted, same-origin frame. Returns the prefix, `null` to clear it, or
 * `undefined` when the message is not a valid title request. Control characters and an over-long prefix are refused.
 */
export function pluginTitleRequest(
  origin: string, hostOrigin: string, source: unknown, frameWindow: unknown, data: unknown
): string | null | undefined {
  if (origin !== hostOrigin || !frameWindow || source !== frameWindow || !data || typeof data !== 'object') return undefined;
  const message = data as { type?: unknown; prefix?: unknown };
  if (message.type !== 'agentfox:title') return undefined;
  if (message.prefix === null) return null;
  if (typeof message.prefix !== 'string') return undefined;
  const prefix = message.prefix.trim();
  if (!prefix) return null;
  return prefix.length > PLUGIN_TITLE_PREFIX_MAX || /[\u0000-\u001f\u007f]/.test(prefix) ? undefined : prefix;
}

/** The host page's title for a plugin page, led by the frame's prefix when it has set one. */
export function pluginPageTitle(pageTitle: string | null | undefined, prefix: string | null): string {
  return `${prefix ? `${prefix} · ` : ''}${pageTitle ?? 'Plugin'} · AgentFox`;
}
