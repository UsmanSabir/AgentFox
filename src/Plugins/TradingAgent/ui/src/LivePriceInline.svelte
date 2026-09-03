<script lang="ts">
  import { ArrowDownRight, ArrowUpRight, Minus } from 'lucide-svelte';
  import { livePriceLabel, useLivePrices } from './livePrices';

  export let symbol: string;
  export let fallbackChange: number | null = null;
  export let fallbackPrice: number | null = null;
  export let showPrice = true;

  const livePrices = useLivePrices();
  let priceStore = livePrices.quote(symbol);
  $: priceStore = livePrices.quote(symbol);
  $: view = $priceStore;
  $: price = view.quote?.current ?? fallbackPrice;
  $: change = view.quote?.changePercent ?? fallbackChange;
  $: sourceLabel = view.quote ? livePriceLabel(view) : 'Delayed market snapshot';

  const money = (value: number) => value.toLocaleString(undefined, { maximumFractionDigits: 2 });
  const percent = (value: number) => `${value > 0 ? '+' : ''}${value.toFixed(2)}%`;
</script>

{#if price != null || change != null}
  <span class="live-quote" title={sourceLabel} aria-label={`${sourceLabel} for ${symbol}${price != null ? ` ${money(price)}` : ''}${change != null ? `, change ${percent(change)}` : ''}`}>
    {#if showPrice && price != null}<span class="price">{money(price)}</span>{/if}
    {#if change != null}
      <span class="change" class:up={change > 0} class:down={change < 0} class:flat={change === 0}>
        {#if change > 0}<ArrowUpRight size={11} aria-hidden="true" />
        {:else if change < 0}<ArrowDownRight size={11} aria-hidden="true" />
        {:else}<Minus size={11} aria-hidden="true" />{/if}
        {percent(change)}
      </span>
    {/if}
    {#if view.quote && view.freshness !== 'live'}
      <!-- Pre-open is not "close": the price is the last trade, but the venue is live and taking
           orders. Labelling it "close" reads as a market that has finished for the day. -->
      <small>{view.freshness === 'stale' ? 'stale' : view.phase === 'PreOpen' ? 'pre-open' : 'close'}</small>
    {/if}
  </span>
{/if}

<style>
  .live-quote { display:inline-flex; align-items:center; gap:.3rem; min-width:0; white-space:nowrap; }
  .price { font-size:.72rem; font-variant-numeric:tabular-nums; color:var(--text-2); }
  .change { display:inline-flex; align-items:center; gap:.08rem; padding:.08rem .28rem; border-radius:999px;
            font-size:.64rem; font-weight:750; font-variant-numeric:tabular-nums; }
  .change.up { color:var(--success); background:color-mix(in srgb,var(--success) 11%,transparent); }
  .change.down { color:var(--danger); background:color-mix(in srgb,var(--danger) 11%,transparent); }
  .change.flat { color:var(--text-3); background:color-mix(in srgb,var(--text-3) 9%,transparent); }
  small { color:var(--text-3); font-size:.58rem; text-transform:uppercase; letter-spacing:.04em; }
</style>
