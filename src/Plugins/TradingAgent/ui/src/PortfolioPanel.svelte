<script lang="ts">
  import {
    trading,
    type BrokerAccountSnapshot,
    type BrokerAccountHolding
  } from './api';
  import {
    WalletCards, ChevronDown, ChevronRight, RefreshCw, Eye, EyeOff,
    AlertTriangle, BriefcaseBusiness, BookOpen
  } from 'lucide-svelte';
  import type { SymbolExtensionComponent } from './symbolExtensions';

  /**
   * Optional component rendered under each holding's instrument name. Null in a community build. It
   * receives only `symbol` and must render nothing when it has nothing to say — see
   * `symbolExtensions.ts`.
   */
  export let holdingStatus: SymbolExtensionComponent | null = null;

  let open = false;
  let showValues = false;
  let loading = false;
  let loaded = false;
  let error: string | null = null;
  let account: BrokerAccountSnapshot | null = null;

  async function load() {
    if (loading) return;
    loading = true;
    error = null;
    try {
      account = await trading.account();
      loaded = true;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  function toggleOpen() {
    open = !open;
    if (open && !loaded) load();
  }

  const hidden = '••••••';
  const text = (value?: string | null) => value?.trim() || '—';
  // Visibility is an explicit argument rather than a closure over `showValues`. Svelte can then see
  // the template dependency and re-run these formatters immediately when the privacy toggle changes.
  const quantity = (value: number | null | undefined, visible: boolean) => !visible
    ? hidden
    : value == null ? 'Unknown' : new Intl.NumberFormat('en-PK', { maximumFractionDigits: 4 }).format(value);
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
    ? hidden
    : value == null ? 'Unknown' : `${value >= 0 ? '+' : ''}${value.toFixed(2)}%`;
  const pnlTone = (holding: BrokerAccountHolding, visible: boolean) => !visible || holding.unrealizedProfitLoss == null
    ? '' : holding.unrealizedProfitLoss > 0 ? 'positive' : holding.unrealizedProfitLoss < 0 ? 'negative' : '';
  const when = (value?: string | null) => value ? new Date(value).toLocaleString() : '—';
  const countLabel = (available: boolean, count: number, label: string) =>
    available ? `${count} ${label}` : `${label} unavailable`;
  const extras = (attributes?: Record<string, string | null>) => Object.entries(attributes ?? {})
    .filter(([, value]) => value != null && value.trim().length > 0);
  const attributeValue = (value: string | null, visible: boolean) => visible ? text(value) : hidden;
</script>

<section class="portfolio" class:open>
  <header>
    <button class="toggle" on:click={toggleOpen} aria-expanded={open} aria-controls="portfolio-content">
      {#if open}<ChevronDown size={15}/>{:else}<ChevronRight size={15}/>{/if}
      <WalletCards size={17}/>
      <span class="title">
        <b>Your portfolio</b>
        <small>
          {#if account}
            {account.brokerName} · {countLabel(account.holdingsAvailable, account.holdings.length, 'holding(s)')} · {countLabel(account.ordersAvailable, account.orders.length, 'working order(s)')}
          {:else}
            Current holdings, balances, and broker order book
          {/if}
        </small>
      </span>
    </button>
    <div class="header-actions">
      <button class="action" on:click={() => showValues = !showValues} aria-pressed={showValues} title={showValues ? 'Hide financial values' : 'Show financial values'}>
        {#if showValues}<EyeOff size={14}/> Hide values{:else}<Eye size={14}/> Show values{/if}
      </button>
      <button class="action" on:click={load} disabled={loading} title="Read the latest account data from the broker">
        <span class:spin={loading}><RefreshCw size={14}/></span> {loading ? 'Refreshing…' : 'Refresh'}
      </button>
    </div>
  </header>

  {#if open}
    <div id="portfolio-content" class="content">
      {#if error}<div class="message danger"><AlertTriangle size={14}/>{error}</div>{/if}
      {#if loading && !account}<div class="message">Reading your account from the broker…</div>{/if}

      {#if account}
        <div class="account-meta">
          <span>{account.brokerName}</span>
          <span>Account {showValues ? text(account.accountLabel) : hidden}</span>
          <span>Updated {when(account.retrievedAtUtc)}</span>
        </div>
        {#if extras(account.attributes).length}
          <details class="broker-details"><summary>Broker details</summary><dl>{#each extras(account.attributes) as [label, value]}<dt>{label}</dt><dd>{attributeValue(value, showValues)}</dd>{/each}</dl></details>
        {/if}

        {#if account.warnings.length}
          <div class="warnings">
            {#each account.warnings as warning}<p><AlertTriangle size={13}/>{warning}</p>{/each}
          </div>
        {/if}

        <div class="balances">
          {#if account.balancesAvailable}
            {#each account.balances as balance}
              <div class="balance"><span>{balance.label}</span><b>{money(balance.value, balance.currency, showValues)}</b>{#if extras(balance.attributes).length}<details class="broker-details"><summary>Details</summary><dl>{#each extras(balance.attributes) as [label, value]}<dt>{label}</dt><dd>{attributeValue(value, showValues)}</dd>{/each}</dl></details>{/if}</div>
            {/each}
          {:else}
            <div class="unavailable"><b>Balances unavailable</b><span>The broker did not return a reliable balance.</span></div>
          {/if}
        </div>

        <section class="account-section">
          <h3><BriefcaseBusiness size={15}/> Holdings <span>{account.holdings.length}</span></h3>
          {#if !account.holdingsAvailable}
            <div class="unavailable"><b>Holdings unavailable</b><span>This is not being treated as an empty portfolio.</span></div>
          {:else if !account.holdings.length}
            <div class="empty">The broker reports no current holdings.</div>
          {:else}
            <div class="table-wrap"><table>
              <thead><tr><th>Instrument</th><th>Quantity</th><th>Average cost</th><th>Market price</th><th>Market value</th><th>Unrealized P/L</th></tr></thead>
              <tbody>{#each account.holdings as holding}
                <tr>
                  <td><b>{text(holding.symbol ?? holding.instrumentId)}</b><small>{text(holding.exchange)} · {text(holding.assetType)}</small>{#if holdingStatus && holding.symbol}<div class="holding-extension"><svelte:component this={holdingStatus} symbol={holding.symbol} /></div>{/if}{#if extras(holding.attributes).length}<details class="broker-details"><summary>Details</summary><dl>{#each extras(holding.attributes) as [label, value]}<dt>{label}</dt><dd>{attributeValue(value, showValues)}</dd>{/each}</dl></details>{/if}</td>
                  <td>{quantity(holding.quantity, showValues)}</td>
                  <td>{money(holding.averageCost, holding.currency, showValues)}</td>
                  <td>{money(holding.marketPrice, holding.currency, showValues)}</td>
                  <td>{money(holding.marketValue, holding.currency, showValues)}</td>
                  <td class={pnlTone(holding, showValues)}>{money(holding.unrealizedProfitLoss, holding.currency, showValues)}<small>{percent(holding.unrealizedProfitLossPercent, showValues)}</small></td>
                </tr>
              {/each}</tbody>
            </table></div>
          {/if}
        </section>

        <section class="account-section">
          <h3><BookOpen size={15}/> Working orders <span>{account.orders.length}</span></h3>
          {#if !account.ordersAvailable}
            <div class="unavailable"><b>Order book unavailable</b><span>Do not assume there are no working orders; check the broker directly.</span></div>
          {:else if !account.orders.length}
            <div class="empty">The broker confirms there are no working orders.</div>
          {:else}
            <div class="table-wrap"><table>
              <thead><tr><th>Instrument</th><th>Side</th><th>Type / status</th><th>Remaining</th><th>Price</th><th>Placed</th></tr></thead>
              <tbody>{#each account.orders as order}
                <tr>
                  <td><b>{text(order.symbol ?? order.instrumentId)}</b><small>{showValues ? `#${text(order.orderId)}` : `#${hidden}`}</small>{#if extras(order.attributes).length}<details class="broker-details"><summary>Details</summary><dl>{#each extras(order.attributes) as [label, value]}<dt>{label}</dt><dd>{attributeValue(value, showValues)}</dd>{/each}</dl></details>{/if}</td>
                  <td><span class:buy={order.side === 'BUY'} class:sell={order.side === 'SELL'}>{text(order.side)}</span></td>
                  <td>{text(order.orderType)}<small>{text(order.status)}</small></td>
                  <td>{quantity(order.remainingQuantity ?? order.quantity, showValues)}</td>
                  <td>{money(order.price, order.currency, showValues)}{#if order.triggerPrice != null}<small>Trigger {money(order.triggerPrice, order.currency, showValues)}</small>{/if}</td>
                  <td>{text(order.placedAt)}</td>
                </tr>
              {/each}</tbody>
            </table></div>
          {/if}
        </section>
      {/if}
    </div>
  {/if}
</section>

<style>
  .portfolio { background:var(--surface); border:1px solid var(--border); border-radius:var(--radius); margin-bottom:1.25rem; overflow:hidden; }
  .portfolio.open { border-color:color-mix(in srgb,var(--primary) 25%,var(--border)); }
  header { display:flex; align-items:center; justify-content:space-between; gap:.7rem; padding:.15rem .45rem .15rem 0; }
  button { font-family:inherit; }
  .toggle { flex:1; min-width:0; display:flex; align-items:center; gap:.65rem; padding:.8rem 1rem; border:0; background:none; color:var(--text-2); text-align:left; cursor:pointer; }
  .title { display:flex; min-width:0; flex-direction:column; gap:.15rem; }.title b { color:var(--text); font-size:.82rem; }.title small { color:var(--text-3); font-size:.68rem; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .header-actions { display:flex; align-items:center; gap:.35rem; }
  .action { display:flex; align-items:center; gap:.35rem; border:1px solid var(--border); border-radius:var(--radius-sm); background:var(--surface-2); color:var(--text-2); padding:.4rem .55rem; font-size:.68rem; cursor:pointer; white-space:nowrap; }
  .action:hover { color:var(--text); border-color:var(--border-hover); }.action:disabled { opacity:.6; cursor:wait; }
  .spin { display:flex; animation:spin 1s linear infinite; } @keyframes spin { to { transform:rotate(360deg); } }
  .content { border-top:1px solid var(--border); padding:1rem; display:flex; flex-direction:column; gap:1rem; }
  .message,.warnings p { display:flex; align-items:flex-start; gap:.45rem; margin:0; color:var(--text-2); font-size:.72rem; }.message.danger,.warnings p { color:var(--warning); }
  .warnings { border:1px solid color-mix(in srgb,var(--warning) 25%,var(--border)); border-radius:var(--radius-sm); background:color-mix(in srgb,var(--warning) 6%,transparent); padding:.6rem; display:flex; flex-direction:column; gap:.4rem; }
  .account-meta { display:flex; flex-wrap:wrap; gap:.45rem 1rem; color:var(--text-3); font-size:.68rem; }
  .balances { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:.6rem; }
  .balance,.unavailable { border:1px solid var(--border); border-radius:var(--radius-sm); background:var(--surface-2); padding:.7rem .8rem; display:flex; flex-direction:column; gap:.25rem; }
  .balance span,.unavailable span { color:var(--text-3); font-size:.68rem; }.balance b { color:var(--text); font-size:1rem; }.unavailable b { color:var(--warning); font-size:.75rem; }
  .account-section { display:flex; flex-direction:column; gap:.55rem; }.account-section h3 { display:flex; align-items:center; gap:.4rem; margin:0; color:var(--text); font-size:.78rem; }.account-section h3 span { color:var(--text-3); font-size:.67rem; font-weight:500; }
  .table-wrap { overflow-x:auto; border:1px solid var(--border); border-radius:var(--radius-sm); }
  table { width:100%; border-collapse:collapse; min-width:760px; font-size:.7rem; } th { padding:.55rem .65rem; color:var(--text-3); text-align:left; font-weight:600; background:var(--surface-2); border-bottom:1px solid var(--border); } td { padding:.6rem .65rem; color:var(--text-2); border-bottom:1px solid var(--border); } tbody tr:last-child td { border-bottom:0; } td b { color:var(--text); } td small { display:block; margin-top:.18rem; color:var(--text-3); font-size:.62rem; }
  .positive,.buy { color:var(--success)!important; }.negative,.sell { color:var(--danger)!important; }.empty { padding:.8rem; border:1px dashed var(--border); border-radius:var(--radius-sm); color:var(--text-3); font-size:.72rem; text-align:center; }
  .holding-extension { margin-top:.3rem; }
  .broker-details { margin-top:.3rem; color:var(--text-3); font-size:.62rem; }.broker-details summary { cursor:pointer; color:var(--text-3); }.broker-details dl { display:grid; grid-template-columns:max-content 1fr; gap:.2rem .45rem; margin:.35rem 0 0; }.broker-details dt { color:var(--text-3); }.broker-details dd { margin:0; color:var(--text-2); overflow-wrap:anywhere; }
  @media (max-width:720px) { header { align-items:stretch; flex-direction:column; padding:0; }.header-actions { padding:0 .8rem .7rem; }.action { flex:1; justify-content:center; } }
</style>
