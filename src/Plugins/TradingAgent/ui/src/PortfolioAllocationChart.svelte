<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import type { BrokerAccountHolding } from './api';
  import { usePortfolioCosts } from './portfolioCosts';
  import {
    allocationGradient,
    buildPortfolioAllocation,
    type AllocationDimension,
    type AllocationBasis,
    type PortfolioCosts
  } from './portfolioAllocation';

  export let holdings: BrokerAccountHolding[] = [];
  export let showValues = false;

  let dimension: AllocationDimension = 'stock';
  let basis: AllocationBasis = 'market';
  let costs: PortfolioCosts = {};
  let loadingCosts = false;
  let mounted = false;
  let request = 0;
  let helpOpen = false;
  let helpPinned = false;
  let helpHovering = false;
  let helpFocused = false;
  function closeHelp() { helpOpen = false; helpPinned = false; }
  const readCosts = usePortfolioCosts();
  onMount(() => { mounted = true; });
  onDestroy(() => { request++; });
  // A refreshed account starts a new read. Late responses cannot overwrite a newer snapshot or
  // reveal an old position's cost after values have been hidden. Reuse the provider's own cache.
  async function refreshCosts(snapshot: BrokerAccountHolding[], visible: boolean, ready: boolean) {
    const current = ++request;
    costs = {};
    loadingCosts = false;
    if (!ready || !visible || !snapshot.length || !readCosts) return;
    loadingCosts = true;
    try {
      const answer = await readCosts();
      if (current === request) costs = answer;
    } catch { /* The broker-average fallback remains explicitly estimated. */ }
    finally { if (current === request) loadingCosts = false; }
  }
  $: void refreshCosts(holdings, showValues, mounted);
  const colors = [
    'var(--allocation-1)', 'var(--allocation-2)', 'var(--allocation-3)',
    'var(--allocation-4)', 'var(--allocation-5)', 'var(--allocation-other)'
  ];
  $: market = buildPortfolioAllocation(holdings, dimension);
  $: invested = buildPortfolioAllocation(holdings, dimension, 6, { basis: 'invested', costs });
  $: allocation = basis === 'market' ? market : invested;
  $: valueLabel = basis === 'market' ? 'Market value' : 'Invested amount';
  $: totalLabel = allocation.unavailableCount > 0
    ? basis === 'market' ? 'Known market value' : 'Known invested'
    : basis === 'market' ? 'Market value' : 'Total invested';
  $: investmentLabel = invested.unavailableCount > 0 ? 'Known invested' : 'Invested';
  $: investmentTotal = invested.mixedCurrencies || invested.unavailableCount === holdings.length
    ? 'Unavailable' : money(invested.total, invested.currency);
  $: chartStyle = `background:${allocationGradient(allocation.slices, colors, 'var(--allocation-gap)')}`;
  $: largest = allocation.rows[0] ?? null;
  $: chartSummary = largest
    ? `${dimension === 'stock' ? 'Stock' : 'Sector'} allocation by ${valueLabel.toLowerCase()}. ${totalLabel}: ${money(allocation.total, allocation.currency)}.${basis === 'invested' && allocation.estimatedCount ? ' Estimated.' : ''} ${basis === 'market' ? `${investmentLabel}: ${loadingCosts ? 'Reading costs' : investmentTotal}.${invested.estimatedCount && !loadingCosts ? ' Estimated.' : ''}` : ''} ${largest.label} is the largest allocation at ${largest.percent.toFixed(1)} percent. ${allocation.rows.length} categories total.`
    : `No ${dimension} allocation is available.`;

  const money = (value: number, currency: string | null) => {
    if (!currency)
      return `${new Intl.NumberFormat('en-PK', { maximumFractionDigits: 0 }).format(value)} (currency unknown)`;
    try {
      return new Intl.NumberFormat('en-PK', {
        style: 'currency', currency, maximumFractionDigits: 0
      }).format(value);
    } catch {
      return `${new Intl.NumberFormat('en-PK', { maximumFractionDigits: 0 }).format(value)} ${currency ?? ''}`.trim();
    }
  };
  const percent = (value: number) => `${value.toFixed(1)}%`;
</script>

<section class="allocation" aria-labelledby="allocation-heading">
  <div class="allocation-head">
    <div>
      <div class="heading"><h4 id="allocation-heading">Holding allocation</h4>
        <div class="help" class:help-open={helpOpen} role="group"
          on:pointerenter={() => { helpHovering = true; helpOpen = true; }}
          on:pointerleave={() => { helpHovering = false; if (!helpPinned && !helpFocused) closeHelp(); }}>
          <button type="button" class="help-button" aria-label="Help about holding allocation and invested amount" aria-expanded={helpOpen} aria-controls="allocation-help" aria-describedby={helpOpen ? 'allocation-help' : undefined}
            on:focus={() => { helpFocused = true; helpOpen = true; }}
            on:blur={() => { helpFocused = false; helpPinned = false; if (!helpHovering) closeHelp(); }}
            on:click={() => { if (helpPinned) closeHelp(); else { helpPinned = true; helpOpen = true; } }}
            on:keydown={(event) => { if (event.key === 'Escape' && helpOpen) { event.preventDefault(); event.stopPropagation(); closeHelp(); } }}>?</button>
          <div id="allocation-help" class="help-popup" role="tooltip">Market value shows what your holdings are worth in the broker account snapshot. Invested shows the purchase cost of the shares you still hold, including known purchase charges. Where supplied, costs use the remaining purchase lots after selling the oldest shares first. Estimated marks costs with incomplete records, pending charges, or a broker-average fallback. Missing amounts are excluded; “Known” totals and percentages cover only holdings with an available amount.</div>
        </div>
      </div>
      <p>{basis === 'market' ? 'Market value' : 'Invested amount'} · {dimension === 'stock' ? 'Stocks' : 'Sectors'}</p>
    </div>
    <div class="allocation-controls">
    <div class="dimension" role="group" aria-label="Value holding allocation by">
      <button type="button" class:active={basis === 'market'} aria-pressed={basis === 'market'} on:click={() => basis = 'market'}>Market value</button>
      <button type="button" class:active={basis === 'invested'} aria-pressed={basis === 'invested'} on:click={() => basis = 'invested'}>Invested</button>
    </div>
    <div class="dimension" role="group" aria-label="Group holding allocation">
      <button type="button" class:active={dimension === 'stock'} aria-pressed={dimension === 'stock'} on:click={() => dimension = 'stock'}>Stocks</button>
      <button type="button" class:active={dimension === 'sector'} aria-pressed={dimension === 'sector'} on:click={() => dimension = 'sector'}>Sectors</button>
    </div>
    </div>
  </div>

  {#if !showValues}
    <div class="private-message">Choose <b>Show values</b> to view portfolio allocation.</div>
  {:else if basis === 'invested' && loadingCosts}
    <div class="allocation-message" role="status">Reading invested amounts…</div>
  {:else if allocation.mixedCurrencies}
    <div class="allocation-message">Allocation is unavailable because holdings use multiple currencies that cannot be combined safely.</div>
  {:else if !allocation.rows.length}
    <div class="allocation-message">No positive {basis === 'market' ? 'market values' : 'invested amounts'} are available for this allocation.</div>
  {:else}
    <div class="allocation-body">
      <div class="chart-column">
        <div class="donut" style={chartStyle} role="img" aria-label={chartSummary}>
          <div class="donut-center" aria-hidden="true">
            <span>{totalLabel}</span>
            <b>{money(allocation.total, allocation.currency)}</b>
            {#if basis === 'invested' && allocation.estimatedCount}<span class="estimate">Estimated</span>{/if}
            {#if basis === 'market'}
              <span class="secondary-label">{investmentLabel}</span>
              <b class="secondary-value">{loadingCosts ? 'Reading costs…' : investmentTotal}</b>
              {#if !loadingCosts && invested.estimatedCount}<span class="estimate">Estimated</span>{/if}
            {/if}
          </div>
        </div>
        <p class="summary">Largest: <b>{largest?.label}</b> at {percent(largest?.percent ?? 0)}</p>
      </div>

      <table class="legend">
        <caption class="sr-only">Allocation chart legend with percentages and {valueLabel.toLowerCase()}</caption>
        <thead><tr><th scope="col">Color / allocation</th><th scope="col">Share</th><th scope="col">{valueLabel}</th></tr></thead>
        <tbody>
        {#each allocation.slices as slice, index}
          <tr style={`--row-color:${colors[index]}`}>
            <th scope="row" class="legend-label">
              <span class="legend-key"><span class="swatch" aria-hidden="true"></span><span>{slice.label}</span></span>
            </th>
            <td><b class="percent-pill">{percent(slice.percent)}</b></td>
            <td>{money(slice.value, allocation.currency)}{#if slice.estimated}<small class="estimate">Estimated</small>{/if}</td>
          </tr>
        {/each}
        </tbody>
      </table>
    </div>

    {#if allocation.rows.length > allocation.slices.length}
      <details class="full-breakdown">
        <summary>View all {allocation.rows.length} {dimension === 'stock' ? 'stocks' : 'sectors'}</summary>
        <table>
          <thead><tr><th>{dimension === 'stock' ? 'Stock' : 'Sector'}</th><th>Share</th><th>{valueLabel}</th></tr></thead>
          <tbody>
            {#each allocation.rows as row}
              <tr><td>{row.label}</td><td>{percent(row.percent)}</td><td>{money(row.value, allocation.currency)}{#if row.estimated}<small class="estimate">Estimated</small>{/if}</td></tr>
            {/each}
          </tbody>
        </table>
      </details>
    {/if}

    {#if allocation.unavailableCount > 0}
      <p class="note">{allocation.unavailableCount} {allocation.unavailableCount === 1 ? 'holding is' : 'holdings are'} excluded because {valueLabel.toLowerCase()} is unavailable.</p>
    {/if}
    {#if basis === 'market' && !loadingCosts && invested.unavailableCount > 0 && !invested.mixedCurrencies}
      <p class="note">Invested amount unavailable for {invested.unavailableCount} of {holdings.length} holdings.</p>
    {/if}
    {#if dimension === 'sector' && allocation.rows.some(row => row.label === 'Unclassified')}
      <p class="note">Holdings without reliable sector metadata remain grouped as Unclassified.</p>
    {/if}
  {/if}
</section>

<style>
  .allocation {
    --allocation-1:#3b82f6;
    --allocation-2:#a855f7;
    --allocation-3:#f59e0b;
    --allocation-4:#06b6d4;
    --allocation-5:#f43f5e;
    --allocation-other:#94a3b8;
    --allocation-gap:color-mix(in srgb,var(--surface-2) 88%,black);
    border:1px solid color-mix(in srgb,var(--primary) 16%,var(--border)); border-radius:var(--radius-sm);
    background:linear-gradient(135deg,color-mix(in srgb,var(--primary) 4%,var(--surface-2)),var(--surface-2) 42%);
    padding:.8rem; display:flex; flex-direction:column; gap:.75rem;
    container-type:inline-size;
    box-shadow:inset 0 1px 0 color-mix(in srgb,var(--text) 4%,transparent);
  }
  .allocation-head { display:flex; flex-wrap:wrap; align-items:flex-start; justify-content:space-between; gap:.8rem; }
  .heading { display:flex; align-items:center; gap:.35rem; }
  .allocation-controls { display:flex; flex-wrap:wrap; gap:.5rem; }
  .help { position:relative; }
  .help-button { min-width:24px; min-height:24px; padding:0; border:1px solid var(--border-md); border-radius:50%; background:var(--surface); color:var(--text-2); font:inherit; font-size:.68rem; cursor:pointer; }
  .help-button:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .help-popup { display:none; position:absolute; top:100%; left:-8rem; z-index:5; width:min(290px,calc(100vw - 5rem)); box-sizing:border-box; padding:.7rem; border:1px solid var(--border-md); border-radius:var(--radius-sm); background:var(--surface); color:var(--text-2); font-size:.72rem; line-height:1.5; box-shadow:0 8px 28px #0005; }
  .help.help-open .help-popup { display:block; }
  h4 { margin:0; color:var(--text); font-size:.76rem; }
  .allocation-head p { margin:.18rem 0 0; color:var(--text-3); font-size:.64rem; }
  .dimension { display:flex; border:1px solid var(--border-md); border-radius:var(--radius-sm); overflow:hidden; flex:none; }
  .dimension button { min-height:34px; padding:.35rem .65rem; border:0; border-right:1px solid var(--border-md); background:var(--surface); color:var(--text-2); font:inherit; font-size:.67rem; white-space:nowrap; cursor:pointer; }
  .dimension button:last-child { border-right:0; }
  .dimension button:hover { color:var(--text); }
  .dimension button:focus-visible { outline:2px solid var(--primary); outline-offset:-2px; }
  .dimension button.active { background:color-mix(in srgb,var(--primary) 14%,var(--surface)); color:var(--primary); font-weight:700; }
  .private-message,.allocation-message { padding:.85rem; border:1px dashed var(--border-md); border-radius:var(--radius-sm); color:var(--text-3); font-size:.7rem; text-align:center; }
  .private-message b { color:var(--text-2); }
  .allocation-body { display:grid; grid-template-columns:minmax(190px,.75fr) minmax(250px,1.25fr); align-items:center; gap:1.2rem; }
  .chart-column { display:flex; flex-direction:column; align-items:center; gap:.5rem; min-width:0; }
  .donut { position:relative; isolation:isolate; width:min(210px,100%); aspect-ratio:1; border-radius:50%; display:grid; place-items:center; box-shadow:0 18px 36px rgba(0,0,0,.25),0 0 24px color-mix(in srgb,var(--allocation-1) 13%,transparent),inset 0 0 0 1px color-mix(in srgb,var(--text) 16%,transparent); }
  .donut::before { content:""; position:absolute; inset:0; z-index:0; border-radius:inherit; pointer-events:none; background:radial-gradient(circle at 32% 22%,rgba(255,255,255,.24),transparent 36%); opacity:.7; }
  .donut::after { content:""; position:absolute; inset:4px; z-index:0; border:1px solid color-mix(in srgb,var(--text) 14%,transparent); border-radius:inherit; pointer-events:none; }
  .donut-center { position:relative; z-index:1; width:60%; aspect-ratio:1; border-radius:50%; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:.18rem; background:linear-gradient(145deg,color-mix(in srgb,var(--surface) 88%,var(--primary)),var(--surface-2) 56%); color:var(--text-3); text-align:center; box-shadow:0 0 0 2px color-mix(in srgb,var(--text) 12%,transparent),inset 0 1px 12px color-mix(in srgb,var(--text) 5%,transparent),0 8px 18px rgba(0,0,0,.24); }
  .donut-center span { font-size:.59rem; }
  .donut-center b { width:88%; color:var(--text); font-size:.76rem; letter-spacing:.01em; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .donut-center .secondary-label { margin-top:.25rem; }
  .donut-center .secondary-value { font-size:.67rem; font-weight:550; }
  .estimate { display:block; color:var(--text-2); font-size:.62rem; font-weight:500; }
  .summary { margin:.15rem 0 0; padding:.28rem .55rem; border:1px solid var(--border); border-radius:999px; background:color-mix(in srgb,var(--surface) 64%,transparent); color:var(--text-2); font-size:.65rem; text-align:center; }
  .summary b { color:var(--text); }
  .legend { width:100%; min-width:0; border:1px solid color-mix(in srgb,var(--text) 10%,var(--border)); border-radius:var(--radius-sm); overflow:hidden; border-collapse:separate; border-spacing:0; background:color-mix(in srgb,var(--surface) 70%,transparent); font-size:.65rem; }
  .legend thead th { padding:.42rem .55rem; background:color-mix(in srgb,var(--surface-2) 74%,var(--primary) 4%); color:var(--text-3); font-size:.58rem; font-weight:650; letter-spacing:.035em; text-transform:uppercase; }
  .legend thead th:not(:first-child) { text-align:right; }
  .legend tbody th,.legend tbody td { padding:.48rem .55rem; border:0; border-bottom:1px solid var(--border); color:var(--text-2); text-align:right; white-space:nowrap; transition:background-color 140ms ease; }
  .legend tbody tr:hover th,.legend tbody tr:hover td { background:color-mix(in srgb,var(--row-color) 9%,transparent); }
  .legend tr:last-child th,.legend tr:last-child td { border-bottom:0; }
  .legend tbody th { text-align:left; font-weight:600; }
  .legend td { font-variant-numeric:tabular-nums; }
  .legend-key { display:flex; align-items:center; gap:.55rem; min-width:0; }
  .legend-key > span:last-child { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .swatch { display:inline-block; flex:0 0 14px; width:14px; height:14px; border-radius:4px; background:var(--row-color); border:1px solid color-mix(in srgb,var(--row-color) 72%,white); box-shadow:0 0 10px color-mix(in srgb,var(--row-color) 42%,transparent),inset 0 1px 1px rgba(255,255,255,.28); }
  .percent-pill { display:inline-block; min-width:3.3rem; padding:.18rem .4rem; border:1px solid color-mix(in srgb,var(--row-color) 42%,var(--border)); border-radius:999px; background:color-mix(in srgb,var(--row-color) 14%,transparent); color:var(--text); text-align:center; font-variant-numeric:tabular-nums; }
  .full-breakdown { color:var(--text-3); font-size:.66rem; }
  .full-breakdown summary { cursor:pointer; color:var(--text-2); }
  .full-breakdown summary:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .full-breakdown table { width:100%; margin-top:.5rem; border-collapse:collapse; font-size:.65rem; }
  .full-breakdown th,.full-breakdown td { padding:.4rem .45rem; border-bottom:1px solid var(--border); text-align:left; }
  .full-breakdown th { color:var(--text-3); font-weight:600; }
  .full-breakdown td { color:var(--text-2); }
  .full-breakdown th:nth-child(n+2),.full-breakdown td:nth-child(n+2) { text-align:right; font-variant-numeric:tabular-nums; }
  .sr-only { position:absolute; width:1px; height:1px; padding:0; margin:-1px; overflow:hidden; clip:rect(0,0,0,0); white-space:nowrap; border:0; }
  .note { margin:0; color:var(--text-3); font-size:.62rem; }
  @container (max-width:580px) {
    .allocation-body { grid-template-columns:minmax(0,1fr); }
    .allocation-controls { width:100%; }
    .dimension { flex:1; }
    .dimension button { flex:1; }
    .legend tbody th,.legend tbody td { white-space:normal; }
  }
  @media (max-width:720px) {
    .allocation-head { align-items:stretch; flex-direction:column; }
    .dimension button { flex:1; min-height:40px; }
    .allocation-body { grid-template-columns:1fr; }
    .legend tbody th,.legend tbody td { white-space:normal; }
  }
  @media (prefers-reduced-motion:reduce) {
    .legend tbody th,.legend tbody td { transition:none; }
  }
</style>
