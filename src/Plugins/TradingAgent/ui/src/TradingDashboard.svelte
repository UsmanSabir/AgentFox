<script lang="ts">
  // Classic composition only. The shared surface owns the readers, panels and order handlers.
  import TradingSurface from './TradingSurface.svelte';
  import type { SymbolExtension } from './symbolExtensions';
  import type { SectionNavigationItem } from './sectionNavigation';
  export let symbolExtension: SymbolExtension | null = null;
  export let showSideNavigation = true;
  export let navigationItems: SectionNavigationItem[] | undefined = undefined;
  export let archiveAvailable = false;
  export let selectedSymbol: string | null = null;
</script>

<TradingSurface {symbolExtension} {showSideNavigation} {navigationItems}
  bind:archiveAvailable bind:selectedSymbol>
  <svelte:fragment slot="order-detail" let:symbol let:action let:orderType let:quantity let:price let:value>
    <slot name="order-detail" {symbol} {action} {orderType} {quantity} {price} {value}/>
  </svelte:fragment>
</TradingSurface>
