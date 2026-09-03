import type { PersistentOrder } from './api';

export type PersistentActionId = 'inspect' | 'retry' | 'resolve' | 'cancel';
export interface PersistentActionChoice { id: PersistentActionId; label: string; description: string; danger?: boolean }
export interface AttentionResolution { resolution: 'not_filled' | 'partial' | 'filled'; filledQuantity: number | null; note: string }

export function persistentActions(order: PersistentOrder): PersistentActionChoice[] {
  const actions: PersistentActionChoice[] = [];
  if (order.state !== 'fulfilled') actions.push({id:'inspect',label:'Check broker orders',description:'Read what is resting at the broker for this symbol.'});
  if (order.canRetry) actions.push({id:'retry',label:'Check broker and retry',description:order.retryReason || 'Retry only after the broker confirms no matching order or fill.'});
  if (order.state === 'attention') actions.push({id:'resolve',label:'Resolve from broker check',description:'Record what you verified in the broker order book, activity, or statement.'});
  if (!['fulfilled','expired','cancelled'].includes(order.state)) actions.push({id:'cancel',label:'Stop and cancel remainder',description:'Cancel a named resting order and verify the outcome before completing.',danger:true});
  return actions;
}

export function isOrderActionsKey(event: Pick<KeyboardEvent,'key'|'shiftKey'|'ctrlKey'|'metaKey'|'altKey'|'repeat'|'isComposing'>) {
  return !event.repeat && !event.isComposing && !event.ctrlKey && !event.metaKey && !event.altKey
    && (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey));
}

export function validateAttentionResolution(
  resolution: string, quantityText: string | number, noteText: string, totalQuantity: number
): { value: AttentionResolution | null; error: string | null } {
  if (!['not_filled','partial','filled'].includes(resolution)) return {value:null,error:'Choose what the broker record shows.'};
  let filledQuantity: number | null = null;
  if (resolution === 'partial') {
    filledQuantity = Number(quantityText);
    if (!Number.isInteger(filledQuantity) || filledQuantity <= 0 || filledQuantity >= totalQuantity)
      return {value:null,error:`Enter a whole number between 1 and ${totalQuantity - 1}.`};
  }
  const note = noteText.trim();
  if (!note) return {value:null,error:'Describe the broker record you checked.'};
  return {value:{resolution:resolution as AttentionResolution['resolution'],filledQuantity,note},error:null};
}
