<script lang="ts">
  import OrderComposer from './OrderComposer.svelte';
  export let selectedSymbol: string | null = null;
  export let selectedAction: 'BUY' | 'SELL' | null = null;
</script>

<!-- Classic compatibility wrapper; validation and execution have exactly one owner. -->
<OrderComposer {selectedSymbol} {selectedAction} on:close on:changed>
  <svelte:fragment slot="order-detail" let:symbol let:action let:orderType let:quantity let:price let:value let:conditional let:triggerLevel>
    <slot name="order-detail" {symbol} {action} {orderType} {quantity} {price} {value} {conditional} {triggerLevel}/>
  </svelte:fragment>
</OrderComposer>
