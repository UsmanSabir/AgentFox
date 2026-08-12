<script lang="ts">
  import { onMount } from 'svelte';
  import { trading, type ArmedOrdersResponse, type ArmedOrder } from './api';
  import { Crosshair, Trash2, RefreshCw, Zap, ShieldAlert, Clock } from 'lucide-svelte';

  export let refreshTick = 0;

  let data: ArmedOrdersResponse | null = null;
  let loading = true;
  let busy = false;
  let error: string | null = null;
  let notice: string | null = null;
  let showHistory = false;

  export async function load() {
    try {
      data = await trading.armed.list(showHistory);
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  async function disarm(order: ArmedOrder) {
    if (busy) return;
    if (!confirm(
      `Disarm this trigger?\n\n${order.action} ${order.quantity} ${order.symbol} on ` +
      `${describeTrigger(order)}.\n\nThe order is not placed and the trigger stops being watched.`
    )) return;

    busy = true;
    try {
      await trading.armed.disarm(order.armedId);
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function openWindow() {
    if (busy) return;
    busy = true;
    notice = null;
    try {
      const result = await trading.approval.openWindow();
      // inForce, not just "granted" — a window opened while the market is closed does nothing, and
      // saying otherwise would imply protection that is not active.
      notice = result.inForce
        ? `Confirmation suspended until ${new Date(result.armedUntilUtc!).toLocaleTimeString()}.`
        : result.note;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function closeWindow() {
    if (busy) return;
    busy = true;
    try {
      await trading.approval.closeWindow();
      notice = 'Confirmation is required again.';
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  const describeTrigger = (o: ArmedOrder) =>
    o.triggerKind === 'Event'
      ? `${(o.triggerAlertKind ?? '').replace(/([a-z])([A-Z])/g, '$1 $2')}`
      : `${o.triggerKind === 'PriceBelow' ? '≤' : '≥'} ${o.triggerPrice}`;

  const when = (iso: string | null) => iso ? new Date(iso).toLocaleString() : '—';

  let lastTick = 0;
  $: if (refreshTick !== lastTick) { lastTick = refreshTick; if (!loading) load(); }

  onMount(load);
</script>

<section class="armed">
  <header>
    <div class="head-copy">
      <Crosshair size={15} />
      <div>
        <b>Armed orders {#if data?.orders.length}<span class="count">{data.orders.filter(o => o.state === 'armed').length}</span>{/if}</b>
        <span>Orders waiting on a price level or an event</span>
      </div>
    </div>
    <div class="head-actions">
      <label class="toggle">
        <input type="checkbox" bind:checked={showHistory} on:change={load} /> show history
      </label>
      <button class="btn btn-ghost" on:click={load} disabled={busy}><RefreshCw size={13} /> Refresh</button>
    </div>
  </header>

  {#if data}
    <!-- Approval state sits with the triggers because an armed order that cannot be approved will not
         fire, and that pairing is the first thing to check when one does not. -->
    <div class="approval" class:live={data.approval.armedUntilUtc}>
      <ShieldAlert size={13} />
      <div>
        <b>Approval: {data.approval.mode}</b>
        <span>
          {#if data.approval.armedUntilUtc}
            Window open until {new Date(data.approval.armedUntilUtc).toLocaleTimeString()}
            {#if data.approval.armedBy}(by {data.approval.armedBy}){/if}
          {:else if data.approval.mode === 'Auto'}
            Auto-approved this session: {data.approval.autoApprovedThisSession} / {data.approval.maxOrdersPerSession}
          {:else}
            No confirmation-free window is open.
          {/if}
        </span>
      </div>
      {#if data.approval.armedUntilUtc}
        <button class="btn btn-danger" on:click={closeWindow} disabled={busy}>Close window</button>
      {:else}
        <button class="btn btn-ghost" on:click={openWindow} disabled={busy}>
          <Clock size={13} /> Open window
        </button>
      {/if}
    </div>
  {/if}

  {#if notice}<p class="line">{notice}</p>{/if}
  {#if error}<p class="line danger">{error}</p>{/if}

  {#if loading}
    <p class="line">Loading…</p>
  {:else if !data?.orders.length}
    <p class="empty">
      Nothing armed. Click a support or resistance level on the chart, or "arm on this event" on an
      alert, to have an order wait for it.
    </p>
  {:else}
    <ul class="list">
      {#each data.orders as order (order.armedId)}
        <li class="row {order.state}">
          <div class="body">
            <div class="row-1">
              <span class="symbol">{order.symbol}</span>
              <span class="side {order.action.toLowerCase()}">{order.action}</span>
              <span class="qty">{order.quantity}</span>
              <span class="trigger">on {describeTrigger(order)}</span>
              <span class="type">{order.orderType}</span>
              {#if order.state !== 'armed'}<span class="chip">{order.state}</span>{/if}
            </div>
            <div class="meta">
              armed {when(order.armedUtc)}
              {#if order.expiresUtc} · expires {new Date(order.expiresUtc).toLocaleDateString()}{/if}
              {#if order.executionId} · execution {order.executionId}{/if}
            </div>
            {#if order.note}<p class="note">{order.note}</p>{/if}
            {#if order.stateReason}<p class="reason">{order.stateReason}</p>{/if}
          </div>
          {#if order.state === 'armed'}
            <button class="icon danger" title="Disarm" on:click={() => disarm(order)} disabled={busy}>
              <Trash2 size={13} />
            </button>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}

  {#if data}<p class="caveat"><Zap size={12} /> {data.caveat}</p>{/if}
</section>

<style>
  .armed {
    background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
    padding: 1rem; display: flex; flex-direction: column; gap: .7rem; margin-bottom: 1.25rem;
  }
  header { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; flex-wrap: wrap; }
  .head-copy { display: flex; gap: .55rem; align-items: flex-start; color: var(--primary); }
  .head-copy div { display: flex; flex-direction: column; gap: .2rem; }
  .head-copy b { color: var(--text); font-size: .9rem; display: flex; align-items: center; gap: .4rem; }
  .head-copy span { color: var(--text-3); font-size: .72rem; }
  .count { background: var(--warning); color: #0c0d10; border-radius: 999px; font-size: .65rem;
           padding: .05rem .35rem; font-weight: 700; }
  .head-actions { display: flex; align-items: center; gap: .6rem; flex-wrap: wrap; }
  .head-actions .btn { display: flex; align-items: center; gap: .35rem; }
  .toggle { display: flex; align-items: center; gap: .3rem; color: var(--text-3); font-size: .7rem; cursor: pointer; }

  .approval {
    display: flex; align-items: center; gap: .6rem; background: var(--surface-2);
    border-radius: var(--radius-sm); padding: .5rem .65rem; color: var(--text-3);
  }
  .approval.live { color: var(--warning); background: color-mix(in srgb, var(--warning) 10%, transparent); }
  .approval div { display: flex; flex-direction: column; gap: .15rem; flex: 1; }
  .approval b { color: var(--text); font-size: .78rem; }
  .approval span { font-size: .7rem; }
  .approval .btn { white-space: nowrap; display: flex; align-items: center; gap: .3rem; }

  .line { margin: 0; color: var(--text-3); font-size: .73rem; }
  .line.danger { color: var(--danger); }
  .empty { margin: 0; color: var(--text-3); font-size: .74rem; padding: .9rem 0; text-align: center; line-height: 1.6; }

  .list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: .3rem;
          max-height: 300px; overflow-y: auto; }
  .row { display: flex; gap: .4rem; align-items: flex-start; background: var(--surface-2);
         border-radius: var(--radius-sm); border-left: 3px solid var(--warning); padding: .1rem; }
  .row.fired { border-left-color: var(--success); opacity: .7; }
  .row.cancelled, .row.expired { border-left-color: var(--text-3); opacity: .55; }
  .row.failed { border-left-color: var(--danger); }

  .body { flex: 1; padding: .45rem .55rem; display: flex; flex-direction: column; gap: .2rem; min-width: 0; }
  .row-1 { display: flex; align-items: center; gap: .4rem; flex-wrap: wrap; font-size: .76rem; }
  .symbol { font-family: ui-monospace, monospace; font-weight: 600; color: var(--text); }
  .side.sell { color: var(--danger); font-weight: 600; }
  .side.buy { color: var(--success); font-weight: 600; }
  .qty, .type { color: var(--text-3); font-size: .7rem; }
  .trigger { color: var(--text-2); }
  .chip { font-size: .6rem; padding: .05rem .35rem; border-radius: 999px;
          border: 1px solid var(--border-md); color: var(--text-3); }
  .meta { color: var(--text-3); font-size: .65rem; }
  .note { margin: 0; color: var(--text-2); font-size: .71rem; }
  .reason { margin: 0; color: var(--warning); font-size: .69rem; line-height: 1.45; }

  .icon { background: none; border: 0; cursor: pointer; color: var(--text-3);
          padding: .4rem; border-radius: var(--radius-sm); display: flex; }
  .icon:hover { background: var(--surface-3); }
  .icon.danger:hover { color: var(--danger); }

  .caveat { margin: 0; color: var(--text-3); font-size: .67rem; display: flex; gap: .3rem;
            align-items: flex-start; line-height: 1.5; }
</style>
