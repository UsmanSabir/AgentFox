<script lang="ts">
  import { ArrowDownRight, ArrowUpRight, Minus } from 'lucide-svelte';
  import { resolveDisplayQuote, useLivePrices } from './livePrices';

  interface Props {
    symbol: string;
    fallbackChange?: number | null;
    fallbackPrice?: number | null;
    showPrice?: boolean;
    showUnavailable?: boolean;
  }

  let {
    symbol,
    fallbackChange = null,
    fallbackPrice = null,
    showPrice = true,
    showUnavailable = false
  }: Props = $props();

  const livePrices = useLivePrices();
  let priceStore = $derived(livePrices.quote(symbol));
  let view = $derived($priceStore);
  // Every rule about which number wins and how it must be labelled lives in one pure function, so
  // the six cells that render prices cannot drift from each other. See `resolveDisplayQuote`.
  let display = $derived(resolveDisplayQuote(view, fallbackPrice, fallbackChange, showPrice));
  let { price, change, tag } = $derived(display);
  let sourceLabel = $derived(display.label);

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
    {#if tag}<small>{tag}</small>{/if}
  </span>
{:else if showUnavailable}<span class="unavailable" aria-label={`Price unavailable for ${symbol}`}>—</span>{/if}

<style>
  .live-quote { display:inline-flex; align-items:center; gap:.3rem; min-width:0; white-space:nowrap; }
  .price { font-size:.72rem; font-variant-numeric:tabular-nums; color:var(--text-2); }
  .unavailable { color:var(--text-3); font-size:.72rem; }
  .change { display:inline-flex; align-items:center; gap:.08rem; padding:.08rem .28rem; border-radius:999px;
            font-size:.64rem; font-weight:750; font-variant-numeric:tabular-nums; }
  .change.up { color:var(--success); background:color-mix(in srgb,var(--success) 11%,transparent); }
  .change.down { color:var(--danger); background:color-mix(in srgb,var(--danger) 11%,transparent); }
  .change.flat { color:var(--text-3); background:color-mix(in srgb,var(--text-3) 9%,transparent); }
  small { color:var(--text-3); font-size:.58rem; text-transform:uppercase; letter-spacing:.04em; }
</style>
