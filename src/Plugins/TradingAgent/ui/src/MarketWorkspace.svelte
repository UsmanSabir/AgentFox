<script lang="ts">
  import { createEventDispatcher, onMount } from 'svelte';
  import type { ArmOrderDialogContext, CandleArchiveStatus } from './api';
  import ChartPane from './ChartPane.svelte';
  import DockedMarketWorkspace from './DockedMarketWorkspace.svelte';
  import ResizableWorkspace from './ResizableWorkspace.svelte';
  import type { SymbolExtension } from './symbolExtensions';
  import WatchlistPanel from './WatchlistPanel.svelte';

  interface Props {
    selectedSymbol?: string | null;
    refreshTick?: number;
    historyRefreshTick?: number;
    archive?: CandleArchiveStatus | null;
    marketOpen?: boolean;
    symbolExtension?: SymbolExtension | null;
  }

  let {
    selectedSymbol = $bindable(null),
    refreshTick = 0,
    historyRefreshTick = 0,
    archive = null,
    marketOpen = false,
    symbolExtension = null
  }: Props = $props();

  const dispatch = createEventDispatcher<{ arm: ArmOrderDialogContext; priceAlert: {symbol: string; target?: number; direction?: 'above' | 'below'} }>();
  const DESKTOP_QUERY = '(min-width: 901px)';
  let desktop = $state(typeof window !== 'undefined' && window.matchMedia(DESKTOP_QUERY).matches);
  let selectedCompany: string | null = $state<string | null>(null);
  let watchlistPanel: WatchlistPanel | null = $state<WatchlistPanel | null>(null);
  let dockedWorkspace: DockedMarketWorkspace | null = $state<DockedMarketWorkspace | null>(null);
  let watchlistCompact = $state(false);
  let chartExpanded = $state(false);

  export async function refresh() {
    if (desktop) await dockedWorkspace?.refresh();
    else await watchlistPanel?.refresh();
  }

  onMount(() => {
    const media = window.matchMedia(DESKTOP_QUERY);
    const update = () => desktop = media.matches;
    update();
    media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  });
</script>

{#if desktop}
  <DockedMarketWorkspace
    bind:this={dockedWorkspace}
    bind:selectedSymbol
    bind:selectedCompany
    {refreshTick}
    {historyRefreshTick}
    {archive}
    {marketOpen}
    {symbolExtension}
    on:priceAlert={(event) => dispatch('priceAlert', event.detail)}
    on:arm={(event) => dispatch('arm', event.detail)}
  />
{:else}
  <!-- Touch widths deliberately remain an ordered document. Docking interactions and tiny tab bars
       are desktop affordances; the existing splitter collapses to one readable column here. -->
  <ResizableWorkspace
    label="Market workspace"
    leftLabel="Watchlist"
    rightLabel="Price chart"
    storageKey="trading.market-workspace.split.v1"
    defaultLeft={28}
    minLeft={18}
    maxLeft={48}
    compactLeft={watchlistCompact}
    expanded={chartExpanded}
  >
    {#snippet left()}
      
        <WatchlistPanel
          bind:this={watchlistPanel}
          bind:selected={selectedSymbol}
          bind:selectedCompany
          bind:compact={watchlistCompact}
          {refreshTick}
          {marketOpen}
          on:priceAlert={(event) => dispatch('priceAlert', event.detail)}
          rowStatus={symbolExtension?.rowStatus ?? null}
        />
      
      {/snippet}
    {#snippet right()}
      
        <ChartPane
          symbol={selectedSymbol}
          companyName={selectedCompany}
          bind:expanded={chartExpanded}
          quoteDetail={symbolExtension?.quoteDetail ?? null}
          instrumentLinks={symbolExtension?.instrumentLinks ?? null}
          {refreshTick}
          {historyRefreshTick}
          {archive}
          {marketOpen}
          on:priceAlert={(event) => dispatch('priceAlert', event.detail)}
          on:arm={(event) => dispatch('arm', event.detail)}
        />
      
      {/snippet}
  </ResizableWorkspace>

  {#if symbolExtension?.plan && selectedSymbol}
    <div id="trading-stock-plan" class="section-anchor">
      <symbolExtension.plan
        symbol={selectedSymbol}
        companyName={selectedCompany}
      />
    </div>
  {/if}
{/if}
