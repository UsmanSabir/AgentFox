<script lang="ts">
  import type { CleanNewOrderPrefill } from './newOrderPrefill';
  import type { ArmedOrder } from './api';
  import OrderComposer from './OrderComposer.svelte';
  export let selectedSymbol: string | null = null;
  export let selectedAction: 'BUY' | 'SELL' | null = null;
  export let quick = false;
  /** Starting form from `requestNewOrder` (newOrderPrefill.ts); pre-fills only, never submits. */
  export let prefill: CleanNewOrderPrefill | null = null;
  /** Opens the composer straight into the edition editor that owns this armed order (orderTypeExtensions.ts). */
  export let editArmedOrder: ArmedOrder | null = null;
</script>

<!-- Classic compatibility wrapper; validation and execution have exactly one owner. -->
<OrderComposer {selectedSymbol} {selectedAction} {quick} {prefill} {editArmedOrder} on:close on:changed>
  <svelte:fragment slot="order-detail" let:symbol let:action let:orderType let:quantity let:price let:value let:conditional let:triggerLevel let:trailing let:triggerPercent>
    <slot name="order-detail" {symbol} {action} {orderType} {quantity} {price} {value} {conditional} {triggerLevel} {trailing} {triggerPercent}/>
  </svelte:fragment>
</OrderComposer>
