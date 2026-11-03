<script lang="ts">
  import type { BrokerAccountHolding } from './api';
  import { useLivePrices } from './livePrices';
  import { holdingValuation, psxHoldingUrl } from './holdingValuation';
  export let holding: BrokerAccountHolding;
  export let showValues = false;
  const livePrices = useLivePrices();
  $: symbol = holding.symbol?.trim() || holding.instrumentId?.trim() || '—';
  let priceStore = livePrices.quote(symbol);
  $: priceStore = livePrices.quote(symbol);
  $: valuation = holdingValuation(holding, $priceStore.quote?.current);
  $: percent = valuation.profitLossPercent;
  $: known = showValues && percent != null && Number.isFinite(percent);
  $: tone = !known ? '' : percent! > 0 ? 'gain' : percent! < 0 ? 'loss' : 'flat';
  $: url = psxHoldingUrl(holding);
  $: description = known ? `Unrealized ${percent! > 0 ? 'gain' : percent! < 0 ? 'loss' : 'P/L'} ${Math.abs(percent!).toFixed(2)}% · ${valuation.hasLiveMark ? 'estimated from live mark and broker average cost' : 'broker account snapshot'}` : '';
</script>

<span class="holding-name {tone}">
  {#if url}
    <a href={url} target="_blank" rel="noopener noreferrer" aria-label={`${symbol} on PSX (opens in a new tab)`}>{symbol}</a>
  {:else}<b>{symbol}</b>{/if}
  {#if known}
    <span class="indicator" role="img" title={description} aria-label={description}>
      {#if percent !== 0}<svg width="10" height="8" viewBox="0 0 10 8" aria-hidden="true"><path d={percent! > 0 ? 'M0 8 L5 0 L10 8 Z' : 'M0 0 L10 0 L5 8 Z'}/></svg>{/if}
      <span aria-hidden="true">({Math.abs(percent!).toFixed(2)}%)</span>
    </span>
  {/if}
</span>

<style>
  .holding-name { display:inline-flex; flex-wrap:wrap; align-items:center; gap:5px; font-weight:700; }
  a { color:inherit; text-decoration:none; text-underline-offset:3px; }
  a:hover { text-decoration:underline; } a:focus-visible { outline:2px solid var(--primary); outline-offset:3px; border-radius:2px; }
  .gain { color:var(--success); } .loss { color:var(--danger); } .flat { color:var(--text-2); }
  .indicator { display:inline-flex; align-items:center; gap:3px; white-space:nowrap; font-variant-numeric:tabular-nums; }
  svg { fill:currentColor; }
</style>
