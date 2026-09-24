<script lang="ts">
  import { derived, readable, type Readable } from 'svelte/store';
  import type { BrokerAccountHolding } from './api';
  import { useLivePrices, type LivePriceView } from './livePrices';
  import { summarizePortfolioPnl } from './portfolioPnl';

  export let holdings: BrokerAccountHolding[] = [];
  export let showValues = false;

  const hidden = '••••••';
  const livePrices = useLivePrices();
  let quoteViews: Readable<LivePriceView[]> = readable([]);
  $: quoteViews = derived(
    holdings.map(holding => livePrices.quote(holding.symbol ?? holding.instrumentId)),
    views => views
  );
  $: summary = summarizePortfolioPnl(holdings, $quoteViews);

  const money = (value: number | null, currency: string | null, visible: boolean) => {
    if (!visible) return hidden;
    if (value == null) return 'Unknown';
    try {
      return new Intl.NumberFormat('en-PK', {
        style: 'currency',
        currency: currency || 'PKR',
        maximumFractionDigits: 2,
        signDisplay: 'exceptZero'
      }).format(value);
    } catch {
      const sign = value > 0 ? '+' : '';
      return `${sign}${new Intl.NumberFormat('en-PK', { maximumFractionDigits: 2 }).format(value)} ${currency ?? ''}`.trim();
    }
  };
  const percent = (value: number | null) => value == null
    ? null
    : `${value > 0 ? '+' : ''}${value.toFixed(2)}%`;
  $: detail = !showValues
    ? 'Choose Show values to view this total'
    : summary.mixedCurrencies
      ? 'Cannot combine holdings in different currencies'
      : summary.unavailableCount > 0
        ? `${summary.unavailableCount} of ${holdings.length} holdings missing P/L`
        : summary.liveMarkCount > 0
          ? `Using live marks for ${summary.liveMarkCount} of ${holdings.length} holdings`
          : 'Broker account snapshot';
  $: resultLabel = summary.tone === 'gain'
    ? 'Gain'
    : summary.tone === 'loss'
      ? 'Loss'
      : summary.tone === 'flat'
        ? 'Flat'
        : null;
</script>

<div
  class="pnl-summary"
  class:positive={showValues && summary.tone === 'gain'}
  class:negative={showValues && summary.tone === 'loss'}
>
  <span>Total unrealized P/L</span>
  <b>{money(summary.amount, summary.currency, showValues)}</b>
  <small>
    {#if showValues && resultLabel}
      <strong>{resultLabel}{#if percent(summary.percent)} · {percent(summary.percent)}{/if}</strong>
      <i aria-hidden="true">·</i>
    {/if}
    {detail}
  </small>
</div>

<style>
  .pnl-summary {
    border:1px solid var(--border);
    border-radius:var(--radius-sm);
    background:var(--surface-2);
    padding:.7rem .8rem;
    display:flex;
    flex-direction:column;
    gap:.25rem;
  }
  .pnl-summary > span { color:var(--text-3); font-size:.68rem; }
  .pnl-summary > b { color:var(--text); font-size:1rem; font-variant-numeric:tabular-nums; }
  .pnl-summary small { display:flex; flex-wrap:wrap; gap:.25rem; color:var(--text-3); font-size:.62rem; font-style:normal; }
  .pnl-summary small strong { color:var(--text-2); font-weight:650; }
  .pnl-summary small i { color:var(--text-3); font-style:normal; }
  .pnl-summary.positive { border-color:color-mix(in srgb,var(--success) 32%,var(--border)); background:color-mix(in srgb,var(--success) 5%,var(--surface-2)); }
  .pnl-summary.positive > b,.pnl-summary.positive small strong { color:var(--success); }
  .pnl-summary.negative { border-color:color-mix(in srgb,var(--danger) 32%,var(--border)); background:color-mix(in srgb,var(--danger) 5%,var(--surface-2)); }
  .pnl-summary.negative > b,.pnl-summary.negative small strong { color:var(--danger); }
</style>
