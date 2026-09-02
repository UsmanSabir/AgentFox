<script lang="ts">
  import { createEventDispatcher, onMount } from 'svelte';
  import type { ArmOrderDialogContext, CandleArchiveStatus } from './api';
  import ChartPane from './ChartPane.svelte';
  import DockedMarketWorkspace from './DockedMarketWorkspace.svelte';
  import ResizableWorkspace from './ResizableWorkspace.svelte';
  import type { SymbolExtension } from './symbolExtensions';
  import WatchlistPanel from './WatchlistPanel.svelte';

  export let selectedSymbol: string | null = null;
  export let refreshTick = 0;
  export let historyRefreshTick = 0;
  export let archive: CandleArchiveStatus | null = null;
  export let marketOpen = false;
  export let symbolExtension: SymbolExtension | null = null;

  const dispatch = createEventDispatcher<{ arm: ArmOrderDialogContext }>();
  const DESKTOP_QUERY = '(min-width: 901px)';
  let desktop = typeof window !== 'undefined' && window.matchMedia(DESKTOP_QUERY).matches;
  let selectedCompany: string | null = null;
  let watchlistPanel: WatchlistPanel | null = null;
  let dockedWorkspace: DockedMarketWorkspace | null = null;
  let watchlistCompact = false;
  let chartExpanded = false;

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
    <svelte:fragment slot="left">
      <WatchlistPanel
        bind:this={watchlistPanel}
        bind:selected={selectedSymbol}
        bind:selectedCompany
        bind:compact={watchlistCompact}
        {refreshTick}
        {marketOpen}
        rowStatus={symbolExtension?.rowStatus ?? null}
      />
    </svelte:fragment>
    <svelte:fragment slot="right">
      <ChartPane
        symbol={selectedSymbol}
        companyName={selectedCompany}
        bind:expanded={chartExpanded}
        {refreshTick}
        {historyRefreshTick}
        {archive}
        {marketOpen}
        on:arm={(event) => dispatch('arm', event.detail)}
      />
    </svelte:fragment>
  </ResizableWorkspace>

  {#if symbolExtension?.plan && selectedSymbol}
    <div id="trading-stock-plan" class="section-anchor">
      <svelte:component
        this={symbolExtension.plan}
        symbol={selectedSymbol}
        companyName={selectedCompany}
      />
    </div>
  {/if}
{/if}
