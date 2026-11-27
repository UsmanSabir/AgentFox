<script lang="ts">
  import { createEventDispatcher } from 'svelte';
  import type { CleanNewOrderPrefill } from './newOrderPrefill';
  import type { ArmedOrder } from './api';
  import OrderComposer from './OrderComposer.svelte';
  interface Props {
    selectedSymbol?: string | null;
    selectedAction?: 'BUY' | 'SELL' | null;
    quick?: boolean;
    /** Starting form from `requestNewOrder` (newOrderPrefill.ts); pre-fills only, never submits. */
    prefill?: CleanNewOrderPrefill | null;
    /** Opens the composer straight into the edition editor that owns this armed order (orderTypeExtensions.ts). */
    editArmedOrder?: ArmedOrder | null;
  }
  let { selectedSymbol = null, selectedAction = null, quick = false, prefill = null, editArmedOrder = null }: Props = $props();
  // Re-dispatched rather than forwarded bare: editions listen for `close` and `changed` on this component.
  const dispatch = createEventDispatcher<{ close: void; changed: void }>();
</script>

<!-- Classic compatibility wrapper; validation and execution have exactly one owner. `order-detail` stays a slot (see
     TradingDashboard.svelte). -->
<OrderComposer {selectedSymbol} {selectedAction} {quick} {prefill} {editArmedOrder}
  on:close={() => dispatch('close')} on:changed={() => dispatch('changed')}>
  <svelte:fragment slot="order-detail" let:symbol let:action let:orderType let:quantity let:price let:value let:conditional let:triggerLevel let:trailing let:triggerPercent>
    <!-- svelte-ignore slot_element_deprecated -->
    <slot name="order-detail" {symbol} {action} {orderType} {quantity} {price} {value} {conditional} {triggerLevel} {trailing} {triggerPercent}/>
  </svelte:fragment>
</OrderComposer>
