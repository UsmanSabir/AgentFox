<script lang="ts">
  // Classic composition only. The shared surface owns the readers, panels and order handlers.
  import TradingSurface from './TradingSurface.svelte';
  import type { SymbolExtension } from './symbolExtensions';
  import type { SectionNavigationItem } from './sectionNavigation';
  interface Props {
    symbolExtension?: SymbolExtension | null;
    portfolioExtraBalance?: any | null;
    portfolioHeaderAction?: any | null;
    showSideNavigation?: boolean;
    navigationItems?: SectionNavigationItem[] | undefined;
    archiveAvailable?: boolean;
    selectedSymbol?: string | null;
  }
  let {
    symbolExtension = null,
    portfolioExtraBalance = null,
    portfolioHeaderAction = null,
    showSideNavigation = true,
    navigationItems = undefined,
    archiveAvailable = $bindable(false),
    selectedSymbol = $bindable(null)
  }: Props = $props();
</script>

<!-- `order-detail` stays a slot: its name is the editions' contract (a snippet name cannot contain a hyphen), and a
     component cannot offer the same content as both a slot and a snippet. -->
<TradingSurface {symbolExtension} {portfolioExtraBalance} {portfolioHeaderAction} {showSideNavigation} {navigationItems}
  bind:archiveAvailable bind:selectedSymbol>
  <svelte:fragment slot="order-detail" let:symbol let:action let:orderType let:quantity let:price let:value let:conditional let:triggerLevel let:trailing let:triggerPercent>
    <!-- svelte-ignore slot_element_deprecated -->
    <slot name="order-detail" {symbol} {action} {orderType} {quantity} {price} {value} {conditional} {triggerLevel} {trailing} {triggerPercent}/>
  </svelte:fragment>
</TradingSurface>
