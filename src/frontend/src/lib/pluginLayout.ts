/** Only the currently mounted, same-origin plugin frame may request temporary host layout changes. */
export function pluginSidebarRequest(
  origin: string, hostOrigin: string, source: unknown, frameWindow: unknown, data: unknown
): 'collapsed' | 'default' | null {
  if (origin !== hostOrigin || !frameWindow || source !== frameWindow || !data || typeof data !== 'object') return null;
  const message = data as { type?: unknown; sidebar?: unknown };
  return message.type === 'agentfox:layout' && (message.sidebar === 'collapsed' || message.sidebar === 'default')
    ? message.sidebar : null;
}
