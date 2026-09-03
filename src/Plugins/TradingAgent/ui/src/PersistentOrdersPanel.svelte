<script lang="ts">
  import { onMount } from 'svelte';
  import { RefreshCw, Repeat2, Trash2, AlertTriangle, CheckCircle2, RotateCcw, ShieldQuestion } from 'lucide-svelte';
  import { trading, type PersistentOrder, type BrokerOrdersView } from './api';
  import LivePriceInline from './LivePriceInline.svelte';
  import { orderListNavigation, focusOrderControls } from './orderListNavigation';
  export let keyboardMode = false;
  let query = '';
  $: visibleOrders = orders.filter(o => !keyboardMode || `${o.symbol} ${o.action} ${o.state} ${o.intentId}`.toLowerCase().includes(query.trim().toLowerCase()));

  export let refreshTick = 0;

  let orders: PersistentOrder[] = [];
  let loading = true;
  let busy: string | null = null;
  let error: string | null = null;
  let notice: string | null = null;
  let showHistory = false;
  let lastTick = 0;
  /**
   * The broker's own resting orders, per intent, once someone asks for them.
   *
   * Cancel can only aim at order numbers the ledger wrote down. If the process stopped between the
   * broker accepting an order and that row being written, the order exists and nothing points at it —
   * measured 2026-09-01, when a 50-share SYS SELL rested unreachable while every further sell was
   * refused for want of free shares. This is the way out: show what is actually there and let a person
   * cancel the right one by number.
   */
  let brokerView: Record<string, BrokerOrdersView> = {};
  let inspecting: string | null = null;

  async function inspectBroker(order: PersistentOrder) {
    if (inspecting) return;
    inspecting = order.intentId;
    error = null;
    try {
      brokerView = { ...brokerView, [order.intentId]: await trading.persistentOrders.brokerOrders(order.intentId) };
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      inspecting = null;
    }
  }

  async function cancelBrokerOrder(order: PersistentOrder, orderNo: string) {
    if (busy) return;
    // Named in full, because this is the one action here that is taken on a person's identification
    // rather than on the ledger's own record.
    if (!confirm(
      `Cancel broker order #${orderNo}?\n\n`
      + `This cancels that exact order at the broker and records it against this ${order.symbol} `
      + `${order.action}.\n\nOnly do this if you have checked the broker's own book and it is this `
      + `order's. It cannot be undone.`
    )) return;

    busy = order.intentId;
    notice = null;
    try {
      const result = await trading.persistentOrders.cancelBrokerOrder(order.intentId, orderNo);
      notice = result.message;
      await inspectBroker(order);
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = null;
    }
  }

  const describeOrder = (o: { side: string | null; remainingQuantity: number | null; price: number | null }) =>
    `${o.side ?? '?'} ${o.remainingQuantity ?? '?'}${o.price != null ? ` @ ${o.price}` : ''}`;

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

  async function resolveAttention(order: PersistentOrder) {
    if (busy) return;

    const choice = prompt(
      `Resolve ${order.symbol} ${order.action} — the broker's own order history only covers TODAY, `
      + `so AgentFox cannot check what happened on ${order.lastAttemptSessionDate ?? 'the prior attempt date'} `
      + `itself. Check the broker's own order book or statement for that date yourself, then type exactly `
      + `what you found:\n\n`
      + `  not_filled — no fill occurred; resume daily retries\n`
      + `  partial — some of the ${order.quantity} share(s) filled\n`
      + `  filled — the full ${order.quantity} share(s) filled\n\n`
      + `Type: not_filled, partial, or filled`
    )?.trim().toLowerCase();
    if (!choice) return;
    if (!['not_filled', 'partial', 'filled'].includes(choice)) {
      error = `"${choice}" is not one of not_filled, partial, filled.`;
      return;
    }
    const resolution = choice as 'not_filled' | 'partial' | 'filled';

    let filledQuantity: number | null = null;
    if (resolution === 'partial') {
      const qtyText = prompt(
        `How many of the ${order.quantity} total share(s) actually filled (1-${order.quantity - 1})?`
      );
      if (!qtyText) return;
      filledQuantity = Number(qtyText);
      if (!Number.isFinite(filledQuantity) || filledQuantity <= 0 || filledQuantity >= order.quantity) {
        error = `Enter a whole number between 1 and ${order.quantity - 1}.`;
        return;
      }
    }

    const note = prompt(
      'What did you check at the broker (order book, activity log, statement) to confirm this? Required.'
    )?.trim();
    if (!note) {
      error = 'A note describing what you checked at the broker is required.';
      return;
    }

    busy = order.intentId;
    notice = null;
    error = null;
    try {
      const result = await trading.persistentOrders.resolveAttention(
        order.intentId, resolution, filledQuantity, note);
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

<section class="persistent" use:orderListNavigation={keyboardMode}>
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
  {#if keyboardMode}<label class="order-filter">Find persistent order <input type="search" bind:value={query} aria-label="Find persistent order" placeholder="Symbol, state or intent ID"/></label><p class="notice">↑ ↓ / Home / End navigate rows · Enter focuses actions · Tab reaches each recovery control.</p>{/if}

  {#if notice}<p class="notice">{notice}</p>{/if}
  {#if error}<p class="notice bad"><AlertTriangle size={12} /> {error}</p>{/if}

  {#if loading}
    <p class="empty">Loading…</p>
  {:else if !orders.length}
    <p class="empty">No keep-working orders. Enable “Keep the unfilled remainder working” when placing or arming a LIMIT or STOPLOSS.</p>
  {:else if !visibleOrders.length}
    <p class="empty">No persistent orders match this filter.</p>
  {:else}
    <div class="grid">
      {#each visibleOrders as order (order.intentId)}
        <article class:danger={danger(order.state)} class:done={terminal(order.state)} data-order-row>
          <div class="top">
            {#if keyboardMode}<button class="symbol" data-order-focus aria-label={`Controls for ${order.symbol} ${order.action} intent ${order.intentId}, ${order.state}`} on:click={focusOrderControls}>{order.symbol}</button>{:else}<span class="symbol">{order.symbol}</span>{/if}
            <LivePriceInline symbol={order.symbol} fallbackChange={null} />
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
              {#if order.state === 'attention'}
                <button class="resolve" on:click={() => resolveAttention(order)} disabled={busy != null}
                        title="Only you can say what happened on a prior trading date — the broker's own history API only covers today.">
                  <ShieldQuestion size={12} />
                  {busy === order.intentId ? 'Resolving…' : 'Resolve from broker check'}
                </button>
              {/if}
              <button class="cancel" on:click={() => cancel(order)} disabled={busy != null}>
                <Trash2 size={12} /> {busy === order.intentId ? 'Working…' : 'Stop & cancel remainder'}
              </button>
            </div>

          {/if}

          <!--
            Reachable on a TERMINAL intent too, and that is the point. The false-terminal transition this
            fixes (see PersistentOrderWorker's expiring/cancelling branch) leaves an intent marked
            'cancelled' while an order it never managed to name is still resting — so the states that
            most need a way out are exactly the ones whose action row is hidden. 'fulfilled' is excluded
            because that path already cancels what it can name and escalates when it cannot.
          -->
          {#if order.state !== 'fulfilled'}
            <div class="order-actions">
              <button class="inspect" on:click={() => inspectBroker(order)}
                      disabled={inspecting != null || busy != null}
                      title="Read the broker's own outstanding orders for this symbol. Use this when a cancel could not find the order it was looking for, or when an order was marked cancelled but you suspect something is still resting.">
                <ShieldQuestion size={12} />
                {inspecting === order.intentId ? 'Reading broker…' : 'What is resting at the broker?'}
              </button>
            </div>

            {#if brokerView[order.intentId]}
              {@const view = brokerView[order.intentId]}
              <div class="broker-view">
                <p class="broker-message">{view.message}</p>

                {#if view.ours.length > 0}
                  <p class="broker-heading">Known to be this order's</p>
                  {#each view.ours as resting}
                    <div class="broker-row">
                      <code>#{resting.orderNo}</code>
                      <span>{describeOrder(resting)}</span>
                    </div>
                  {/each}
                {/if}

                {#if view.unclaimed.length > 0}
                  <p class="broker-heading warn">
                    <AlertTriangle size={11} /> Resting on {order.symbol}, but not recorded against this
                    order. Shape alone cannot prove whose an order is — check the broker before cancelling.
                  </p>
                  {#each view.unclaimed as candidate}
                    <div class="broker-row">
                      <code>#{candidate.orderNo}</code>
                      <span>{describeOrder(candidate)}</span>
                      <button class="cancel" disabled={busy != null}
                              on:click={() => cancelBrokerOrder(order, candidate.orderNo)}>
                        <Trash2 size={11} /> Cancel #{candidate.orderNo}
                      </button>
                    </div>
                  {/each}
                {/if}
              </div>
            {/if}
          {/if}
        </article>
      {/each}
    </div>
  {/if}
</section>

<style>
  [data-order-row]:focus-visible, button:focus-visible, input:focus-visible { outline:2px solid var(--primary); outline-offset:-2px; }
  .order-filter { display:flex; gap:.5rem; align-items:center; padding:.7rem; color:var(--text-2); font-size:.75rem; }
  .order-filter input { min-width:0; padding:.35rem; border:1px solid var(--border-md); background:var(--surface-2); color:var(--text); border-radius:4px; }
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
  .grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(min(100%,310px),1fr)); gap:.65rem; padding:.75rem; }
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
  .retry { color:var(--primary); }.resolve { color:var(--warning); }.cancel { color:var(--danger); }
  .inspect { color:var(--text-2); }
  .broker-view { margin:.5rem .9rem .7rem; padding:.55rem .7rem; border:1px solid var(--border);
    border-radius:6px; display:flex; flex-direction:column; gap:.35rem; }
  .broker-message { margin:0; font-size:.68rem; color:var(--text-2); }
  .broker-heading { margin:.15rem 0 0; font-size:.65rem; text-transform:uppercase;
    letter-spacing:.04em; color:var(--text-3); display:flex; align-items:center; gap:.25rem; }
  .broker-heading.warn { color:var(--warning); text-transform:none; letter-spacing:0; }
  .broker-row { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; font-size:.7rem; }
  .broker-row code { font-size:.68rem; color:var(--text-1); }
  .broker-row span { color:var(--text-2); }.notice,.empty { margin:0; padding:.65rem .9rem; color:var(--text-3); font-size:.69rem; }
  .notice { display:flex; align-items:center; gap:.35rem; border-bottom:1px solid var(--border); }.notice.bad { color:var(--danger); }
</style>
