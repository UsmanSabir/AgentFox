<script lang="ts">
  import { onMount } from 'svelte';
  import { RefreshCw, Repeat2, Trash2, AlertTriangle, CheckCircle2, RotateCcw } from 'lucide-svelte';
  import { trading, type PersistentOrder } from './api';

  export let refreshTick = 0;

  let orders: PersistentOrder[] = [];
  let loading = true;
  let busy: string | null = null;
  let error: string | null = null;
  let notice: string | null = null;
  let showHistory = false;
  let lastTick = 0;

  export async function load() {
    try {
      orders = (await trading.persistentOrders.list(showHistory)).orders;
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  async function cancel(order: PersistentOrder) {
    if (busy) return;
    if (!confirm(
      `Stop this persistent order?\n\n${order.action} ${order.remainingQuantity} remaining `
      + `${order.symbol} ${order.orderType} @ ${order.price ?? '—'}.\n\n`
      + `Any exact broker order still resting will be cancelled and verified before this is marked complete.`
    )) return;

    busy = order.intentId;
    notice = null;
    try {
      const result = await trading.persistentOrders.cancel(order.intentId);
      notice = result.message;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = null;
    }
  }

  async function retry(order: PersistentOrder) {
    if (busy || !order.canRetry) return;
    if (!confirm(
      `Check the broker and retry this order?\n\n${order.action} ${order.remainingQuantity} `
      + `${order.symbol} ${order.orderType} @ ${order.price ?? '—'}.\n\n`
      + `The retry is sent only if today's outstanding orders and activity show no matching order or fill.`
    )) return;

    busy = order.intentId;
    notice = null;
    error = null;
    try {
      const result = await trading.persistentOrders.retry(order.intentId);
      notice = result.message;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = null;
    }
  }

  const num = (value: number) => value.toLocaleString(undefined, { maximumFractionDigits: 2 });
  const when = (iso: string) => new Date(iso).toLocaleString();
  const terminal = (state: string) => ['fulfilled', 'expired', 'cancelled'].includes(state);
  const danger = (state: string) => ['attention', 'expiring', 'cancelling'].includes(state);

  $: if (refreshTick !== lastTick) { lastTick = refreshTick; if (!loading) load(); }
  onMount(load);
</script>

<section class="persistent">
  <header>
    <div class="title">
      <Repeat2 size={15} />
      <div><b>Keep-working orders</b><span>DAY orders re-placed until filled or expired</span></div>
    </div>
    <div class="actions">
      <label><input type="checkbox" bind:checked={showHistory} on:change={load} /> show history</label>
      <button on:click={load} disabled={busy != null}><RefreshCw size={12} /> Refresh</button>
    </div>
  </header>

  {#if notice}<p class="notice">{notice}</p>{/if}
  {#if error}<p class="notice bad"><AlertTriangle size={12} /> {error}</p>{/if}

  {#if loading}
    <p class="empty">Loading…</p>
  {:else if !orders.length}
    <p class="empty">No keep-working orders. Enable “Keep the unfilled remainder working” when placing or arming a LIMIT or STOPLOSS.</p>
  {:else}
    <div class="grid">
      {#each orders as order (order.intentId)}
        <article class:danger={danger(order.state)} class:done={terminal(order.state)}>
          <div class="top">
            <span class="symbol">{order.symbol}</span>
            <span class="side {order.action.toLowerCase()}">{order.action}</span>
            <span class="state">{order.state}</span>
            {#if terminal(order.state)}<CheckCircle2 size={13} />{/if}
          </div>
          <div class="order">
            <b>{order.remainingQuantity}</b> remaining of {order.quantity}
            · {order.orderType} @ {order.price == null ? '—' : num(order.price)}
            {#if order.limitPrice != null} · stop limit {num(order.limitPrice)}{/if}
          </div>
          <div class="progress"><span style={`width:${Math.min(100, order.quantity ? order.filledQuantity / order.quantity * 100 : 0)}%`}></span></div>
          <div class="meta">
            filled {order.filledQuantity} · attempts {order.attemptCount}
            {#if order.lastAttemptSessionDate} · last {order.lastAttemptSessionDate}{/if}
            {#if order.lastOrderNo} · #{order.lastOrderNo}{/if}
            · expires {when(order.expiresUtc)}
          </div>
          {#if order.stateReason}<p class="reason">{order.stateReason}</p>{/if}
          {#if !terminal(order.state)}
            <div class="order-actions">
              {#if order.canRetry}
                <button class="retry" on:click={() => retry(order)} disabled={busy != null}
                        title={order.retryReason}>
                  <RotateCcw size={12} />
                  {busy === order.intentId ? 'Checking broker…' : 'Check broker & retry'}
                </button>
              {/if}
              <button class="cancel" on:click={() => cancel(order)} disabled={busy != null}>
                <Trash2 size={12} /> {busy === order.intentId ? 'Working…' : 'Stop & cancel remainder'}
              </button>
            </div>
          {/if}
        </article>
      {/each}
    </div>
  {/if}
</section>

<style>
  .persistent { margin:1rem 0; border:1px solid var(--border); border-radius:var(--radius);
                background:var(--surface); overflow:hidden; }
  header { display:flex; justify-content:space-between; align-items:center; gap:1rem; padding:.75rem .9rem;
           border-bottom:1px solid var(--border); }
  .title { display:flex; align-items:center; gap:.5rem; color:var(--primary); }
  .title div { display:flex; flex-direction:column; gap:.12rem; }.title b { color:var(--text); font-size:.84rem; }
  .title span,.meta { color:var(--text-3); font-size:.65rem; }
  .actions { display:flex; align-items:center; gap:.55rem; }.actions label { color:var(--text-3); font-size:.65rem; }
  button { display:inline-flex; align-items:center; gap:.3rem; border:1px solid var(--border-md);
           border-radius:var(--radius-sm); background:var(--surface-2); color:var(--text-2);
           padding:.35rem .55rem; cursor:pointer; font-size:.67rem; }
  button:disabled { opacity:.5; cursor:wait; }
  .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(310px,1fr)); gap:.65rem; padding:.75rem; }
  article { border:1px solid var(--border); border-radius:var(--radius-sm); padding:.7rem; background:var(--surface-2); }
  article.danger { border-color:var(--warning); } article.done { opacity:.72; }
  .top { display:flex; align-items:center; gap:.4rem; }.symbol { font-weight:800; color:var(--text); }
  .side,.state { padding:.12rem .34rem; border-radius:999px; font-size:.6rem; font-weight:700; }
  .side.buy { color:var(--success); background:color-mix(in srgb,var(--success) 12%,transparent); }
  .side.sell { color:var(--danger); background:color-mix(in srgb,var(--danger) 12%,transparent); }
  .state { margin-left:auto; color:var(--primary); background:color-mix(in srgb,var(--primary) 12%,transparent); }
  .order { margin-top:.5rem; color:var(--text-2); font-size:.73rem; }.order b { color:var(--text); }
  .progress { height:4px; margin:.55rem 0; background:var(--surface-3); border-radius:3px; overflow:hidden; }
  .progress span { display:block; height:100%; background:var(--success); }
  .reason { margin:.45rem 0 0; color:var(--text-2); font-size:.67rem; line-height:1.35; }
  .order-actions { display:flex; flex-wrap:wrap; gap:.4rem; margin-top:.55rem; }
  .retry { color:var(--primary); }.cancel { color:var(--danger); }.notice,.empty { margin:0; padding:.65rem .9rem; color:var(--text-3); font-size:.69rem; }
  .notice { display:flex; align-items:center; gap:.35rem; border-bottom:1px solid var(--border); }.notice.bad { color:var(--danger); }
</style>
