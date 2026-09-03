<script lang="ts">
  import OrderComposer from './OrderComposer.svelte';
  export let selectedSymbol: string | null = null;
</script>

<!-- Classic compatibility wrapper; validation and execution have exactly one owner. -->
<OrderComposer {selectedSymbol} on:close on:changed>
  <svelte:fragment slot="order-detail" let:symbol let:action let:orderType let:quantity let:price let:value>
    <slot name="order-detail" {symbol} {action} {orderType} {quantity} {price} {value}/>
  </svelte:fragment>
</OrderComposer>
