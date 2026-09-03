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
  <svelte:fragment slot="order-detail" let:action let:quantity let:price>
    <slot name="order-detail" {action} {quantity} {price}/>
  </svelte:fragment>
</TradingSurface>
