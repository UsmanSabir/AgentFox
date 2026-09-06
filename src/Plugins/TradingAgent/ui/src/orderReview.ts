/** Transport failure is not proof an order was refused. Never offer an automatic retry. */
export function uncertainOrderFailure(status: number | null, code: string | null): boolean {
  return status == null || status >= 500 || status === 408 || /unknown|uncertain/i.test(code ?? '');
}

/** A review key can only open review, never acknowledge it or repeat a submission. */
export function isOrderReviewKey(event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey' | 'shiftKey' | 'repeat' | 'isComposing'>) {
  return event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.altKey
    && !event.shiftKey && !event.repeat && !event.isComposing;
}
