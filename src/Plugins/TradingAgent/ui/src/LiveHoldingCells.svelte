<script lang="ts">
  import type { BrokerAccountHolding } from './api';
  import { livePriceLabel, useLivePrices } from './livePrices';

  export let holding: BrokerAccountHolding;
  export let showValues = false;

  const hidden = '••••••';
  const livePrices = useLivePrices();
  $: symbol = holding.symbol ?? holding.instrumentId;
  let priceStore = livePrices.quote(symbol);
  $: priceStore = livePrices.quote(symbol);
  $: view = $priceStore;
  $: hasLiveMark = view.quote?.current != null;
  $: mark = view.quote?.current ?? holding.marketPrice;
  $: marketValue = hasLiveMark && mark != null && holding.quantity != null
    ? mark * holding.quantity : holding.marketValue;
  $: profitLoss = hasLiveMark && mark != null && holding.quantity != null && holding.averageCost != null
    ? (mark - holding.averageCost) * holding.quantity : holding.unrealizedProfitLoss;
  $: profitLossPercent = hasLiveMark && mark != null && holding.averageCost != null && holding.averageCost > 0
    ? (mark - holding.averageCost) / holding.averageCost * 100 : holding.unrealizedProfitLossPercent;
  $: tone = !showValues || profitLoss == null ? '' : profitLoss > 0 ? 'positive' : profitLoss < 0 ? 'negative' : '';

  const money = (value: number | null | undefined) => {
    if (!showValues) return hidden;
    if (value == null) return 'Unknown';
    try {
      return new Intl.NumberFormat('en-PK', {
        style: 'currency', currency: holding.currency || 'PKR', maximumFractionDigits: 2
      }).format(value);
    } catch {
      return `${new Intl.NumberFormat('en-PK', { maximumFractionDigits: 2 }).format(value)} ${holding.currency ?? ''}`.trim();
    }
  };
  const percent = (value: number | null | undefined) => !showValues
    ? hidden : value == null ? 'Unknown' : `${value >= 0 ? '+' : ''}${value.toFixed(2)}%`;
</script>

<td title={hasLiveMark ? livePriceLabel(view) : 'Broker account snapshot'}>
  {money(mark)}
  {#if hasLiveMark}<small>{view.freshness === 'live' ? 'live mark' : view.freshness}</small>{/if}
</td>
<td title={hasLiveMark ? 'Estimated from live mark' : 'Broker account snapshot'}>
  {money(marketValue)}
  {#if hasLiveMark}<small>estimate</small>{/if}
</td>
<td class={tone} title={hasLiveMark ? 'Estimated from live mark and broker average cost' : 'Broker account snapshot'}>
  {money(profitLoss)}<small>{percent(profitLossPercent)}{hasLiveMark ? ' · estimate' : ''}</small>
</td>

<style>
  td { padding:.62rem .75rem; border-bottom:1px solid var(--border); vertical-align:top; font-size:.77rem; }
  td small { display:block; margin-top:.14rem; color:var(--text-3); font-size:.62rem; font-weight:500; }
  td.positive { color:var(--success); }
  td.negative { color:var(--danger); }
</style>
