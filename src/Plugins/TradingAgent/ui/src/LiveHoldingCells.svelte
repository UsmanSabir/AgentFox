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

  // Visibility and currency are explicit arguments rather than closures over the props, for the
  // reason PortfolioPanel's own formatters give: Svelte compiles a template expression's
  // dependencies from what the expression itself reads and runs the call untracked, so state read
  // only INSIDE one of these is invisible to the effect. As a closure over `showValues` these three
  // cells re-rendered only when a live tick moved `mark`, so before the open the privacy toggle
  // appeared to do nothing at all here while the rest of the panel unmasked.
  const money = (
    value: number | null | undefined,
    currency: string | null | undefined,
    visible: boolean
  ) => {
    if (!visible) return hidden;
    if (value == null) return 'Unknown';
    try {
      return new Intl.NumberFormat('en-PK', {
        style: 'currency', currency: currency || 'PKR', maximumFractionDigits: 2
      }).format(value);
    } catch {
      return `${new Intl.NumberFormat('en-PK', { maximumFractionDigits: 2 }).format(value)} ${currency ?? ''}`.trim();
    }
  };
  const percent = (value: number | null | undefined, visible: boolean) => !visible
    ? hidden : value == null ? 'Unknown' : `${value >= 0 ? '+' : ''}${value.toFixed(2)}%`;
</script>

<td title={hasLiveMark ? livePriceLabel(view) : 'Broker account snapshot'}>
  {money(mark, holding.currency, showValues)}
  {#if hasLiveMark}<small>{view.freshness === 'live' ? 'live mark' : view.freshness}</small>{/if}
</td>
<td title={hasLiveMark ? 'Estimated from live mark' : 'Broker account snapshot'}>
  {money(marketValue, holding.currency, showValues)}
  {#if hasLiveMark}<small>estimate</small>{/if}
</td>
<td class={tone} title={hasLiveMark ? 'Estimated from live mark and broker average cost' : 'Broker account snapshot'}>
  {money(profitLoss, holding.currency, showValues)}<small>{percent(profitLossPercent, showValues)}{hasLiveMark ? ' · estimate' : ''}</small>
</td>

<style>
  td { padding:.62rem .75rem; border-bottom:1px solid var(--border); vertical-align:top; font-size:.77rem; }
  td small { display:block; margin-top:.14rem; color:var(--text-3); font-size:.62rem; font-weight:500; }
  td.positive { color:var(--success); }
  td.negative { color:var(--danger); }
</style>
