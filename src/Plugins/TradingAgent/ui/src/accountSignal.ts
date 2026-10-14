/**
 * "The account changed" — a page-wide signal that an order was placed, filled, cancelled or refused, so anything
 * showing holdings or working orders re-reads them instead of waiting for a manual Refresh.
 *
 * A HINT, NOT THE STATE: it carries no figures. A listener re-reads `/trading/account`, so a missed or duplicated
 * signal costs a stale screen or one extra read, never a wrong number. Any edition may send it — this one after
 * its own order and cancel screens, an edition with a live broker feed on every pushed order event — through this
 * one name, so neither has to know the other.
 */
export const ACCOUNT_CHANGED = 'agentfox:trading-account-changed';

export function announceAccountChanged(reason: string): void {
  if (typeof window === 'undefined') return;
  window.dispatchEvent(new CustomEvent(ACCOUNT_CHANGED, { detail: { reason } }));
}

/**
 * Calls `handler` once per burst: signals arriving within `settleMs` of each other become one call, so a login
 * replaying a backlog of fills, or a fill reported on two channels, costs one account read. Returns the unsubscribe.
 */
export function onAccountChanged(handler: () => void, settleMs = 1500): () => void {
  if (typeof window === 'undefined') return () => {};
  let timer: ReturnType<typeof setTimeout> | undefined;
  const listener = () => {
    clearTimeout(timer);
    timer = setTimeout(() => { try { handler(); } catch { /* the listener's own problem */ } }, settleMs);
  };
  window.addEventListener(ACCOUNT_CHANGED, listener);
  return () => { clearTimeout(timer); window.removeEventListener(ACCOUNT_CHANGED, listener); };
}
