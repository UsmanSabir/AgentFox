<script lang="ts">
  import { onMount } from 'svelte';
  import { trading, type ArmedOrdersResponse, type ArmedOrder, type ProtectiveStop } from './api';
  import { Crosshair, Trash2, RefreshCw, Zap, ShieldAlert, Clock, Shield, ShieldOff } from 'lucide-svelte';

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
      `Disarm this trigger?\n\n${order.action} ${order.quantity} ${order.symbol} ${describeFill(order)} ` +
      `(${describeValue(order) ?? 'value unknown'}) on ${describeTrigger(order)}.\n\n` +
      `The order is not placed and the trigger stops being watched.`
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

  const num = (v: number, digits = 2) =>
    v.toLocaleString(undefined, { maximumFractionDigits: digits });

  /**
   * What the trigger is waiting for.
   *
   * A percent trigger reports the level AND the move behind it, because the level alone is not the
   * thing that was armed — it is a projection of a percentage off a reference, and for a trailing
   * order that reference has since moved. Showing only the number would make a trailing stop
   * indistinguishable from a fixed one on the row that is supposed to explain it.
   */
  const describeTrigger = (o: ArmedOrder) => {
    if (o.triggerKind === 'Event')
      return (o.triggerAlertKind ?? '').replace(/([a-z])([A-Z])/g, '$1 $2');

    const drop = o.triggerKind === 'PercentDrop';
    const comparator = drop || o.triggerKind === 'PriceBelow' ? '≤' : '≥';
    const level = o.triggerPrice != null ? num(o.triggerPrice) : '—';

    if (o.triggerKind !== 'PercentDrop' && o.triggerKind !== 'PercentRise')
      return `${comparator} ${level}`;

    const basis = o.referencePrice != null
      ? ` ${drop ? 'below' : 'above'} ${o.trailing ? 'peak ' : ''}${num(o.referencePrice)}`
      : '';
    return `${comparator} ${level} — ${o.triggerPercent}%${basis}`;
  };

  /**
   * The price the ORDER goes in at — which is not the trigger, and was previously not shown at all.
   * A limit with no price is called out rather than papered over with the trigger level: the two are
   * separate numbers, and an armed limit that never had a price set will not go in as intended.
   */
  const describeFill = (o: ArmedOrder) =>
    o.orderType === 'MARKET' ? 'at market'
      : o.price != null ? `@ ${num(o.price)}`
      : 'no price set';

  /** What the order actually commits, which is the number the size is really chosen against. */
  const describeValue = (o: ArmedOrder) =>
    o.price != null && o.price > 0
      ? `${num(o.quantity * o.price, 0)} PKR`
      : null;

  const when = (iso: string | null) => iso ? new Date(iso).toLocaleString() : '—';

  // ── Protective stops ─────────────────────────────────────────────────────

  /**
   * Stops get their own section rather than nesting under their entry. Once an entry fills it is no
   * longer `armed`, so the default view filters it out — nesting would hide precisely the stops that
   * matter, the live ones.
   */
  $: stops = data?.protectiveStops ?? [];

  /** The local backstops are shown as part of their stop, not as loose triggers in the main list. */
  $: entries = (data?.orders ?? []).filter(o => !o.protectiveStopId);

  /**
   * Where the protection actually IS, which is the only thing worth reading on this row. "Armed" is
   * not an answer: a stop resting at the broker survives this process dying, and one that is merely
   * intended does not.
   */
  const describeCover = (s: ProtectiveStop) =>
    s.state === 'closed' ? { text: 'closed', tone: 'off' as const }
      : s.state === 'pending_fill'
        ? s.baselineQuantity == null
          ? { text: 'cannot confirm the fill — needs you', tone: 'warn' as const }
          : { text: 'waiting for the entry to fill', tone: 'idle' as const }
      : s.restingToday
        ? { text: `resting at the broker · ${s.placedQuantity} sh`, tone: 'on' as const }
        : { text: 'not at the broker yet — local backstop only', tone: 'warn' as const };

  async function disarmStop(stop: ProtectiveStop) {
    if (busy) return;
    if (!confirm(
      `Stop managing this protective stop?\n\n` +
      `SELL ${stop.desiredQuantity || '—'} ${stop.symbol} at ${stop.stopTrigger}.\n\n` +
      (stop.restingToday
        ? `A native stop is ALREADY RESTING at the broker for today and cannot be cancelled from ` +
          `here — you must cancel it in the portal yourself.`
        : `No native stop is resting at the broker for this session.`)
    )) return;

    busy = true;
    try {
      const result = await trading.stops.disarm(stop.stopId);
      notice = result.message;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

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
  {:else if !entries.length && !stops.length}
    <p class="empty">
      Nothing armed. Use "sell if it drops" on the chart to protect a holding against a fall of a
      given percent, click a support or resistance level for an exact price, or "arm on this event"
      on an alert.
    </p>
  {:else}
    <ul class="list">
      {#each entries as order (order.armedId)}
        <li class="row {order.state}">
          <div class="body">
            <!-- The ORDER first (size, price, what it commits), the trigger second. The trigger is
                 when it goes in; this line is what goes in, and reading the card without it meant
                 sizing was invisible. -->
            <div class="row-1">
              <span class="symbol">{order.symbol}</span>
              <span class="side {order.action.toLowerCase()}">{order.action}</span>
              <span class="qty">{order.quantity}</span>
              <span class="at" class:missing={order.price == null && order.orderType !== 'MARKET'}>
                {describeFill(order)}
              </span>
              <span class="type">{order.orderType}</span>
              {#if describeValue(order)}<span class="value">≈ {describeValue(order)}</span>{/if}
              {#if order.state !== 'armed'}<span class="chip">{order.state}</span>{/if}
            </div>
            <div class="row-2">
              fires when {order.triggerKind === 'Event' ? 'event' : 'price'} {describeTrigger(order)}
              {#if order.trailing}<span class="chip trail">trailing</span>{/if}
              {#if order.orderType === 'STOPLOSS' && order.limitPrice != null}
                · stop limit {num(order.limitPrice)}
              {/if}
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

  {#if stops.length}
    <div class="stops">
      <h3><Shield size={13} /> Protection</h3>
      <ul class="list">
        {#each stops as stop (stop.stopId)}
          {@const cover = describeCover(stop)}
          <li class="row stop {cover.tone}">
            <div class="body">
              <div class="row-1">
                <span class="symbol">{stop.symbol}</span>
                <span class="side sell">STOP</span>
                {#if stop.desiredQuantity}<span class="qty">{stop.desiredQuantity}</span>{/if}
                <span class="at">@ {num(stop.stopTrigger)}</span>
                <span class="type">limit {num(stop.stopLimit)}</span>
                {#if !stop.recurring}<span class="chip">one session</span>{/if}
              </div>
              <!-- Where the protection actually is. "Armed" would not distinguish an order resting
                   at the exchange from an intention held in this process. -->
              <div class="row-2 cover {cover.tone}">
                {#if cover.tone === 'on'}<Shield size={11} />{:else}<ShieldOff size={11} />{/if}
                {cover.text}
              </div>
              <div class="meta">
                {#if stop.lastPlacedSessionDate}last placed {stop.lastPlacedSessionDate} · {/if}
                {stop.recurring ? 're-placed each session' : 'not re-placed'}
                {#if stop.lastOrderNo} · order no {stop.lastOrderNo}{/if}
              </div>
              {#if stop.stateReason}<p class="reason">{stop.stateReason}</p>{/if}
            </div>
            {#if stop.state !== 'closed'}
              <button class="icon danger" title="Disarm this stop"
                      on:click={() => disarmStop(stop)} disabled={busy}>
                <Trash2 size={13} />
              </button>
            {/if}
          </li>
        {/each}
      </ul>
      <p class="caveat">
        <ShieldAlert size={12} />
        A stop resting at the broker fires on its own. One that is not yet placed depends on AgentFox
        running — and nothing here can cancel an order already resting at the broker.
      </p>
    </div>
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
  .qty { color: var(--text); font-weight: 600; }
  .at { color: var(--text); font-family: ui-monospace, monospace; }
  .at.missing { color: var(--danger); font-family: inherit; font-size: .7rem; }
  .type { color: var(--text-3); font-size: .7rem; }
  .value { color: var(--text-2); font-size: .7rem; margin-left: auto; }
  .row-2 { color: var(--text-2); font-size: .71rem; }
  .chip { font-size: .6rem; padding: .05rem .35rem; border-radius: 999px;
          border: 1px solid var(--border-md); color: var(--text-3); }
  .chip.trail { color: var(--primary); border-color: color-mix(in srgb, var(--primary) 40%, transparent); }
  .meta { color: var(--text-3); font-size: .65rem; }
  .note { margin: 0; color: var(--text-2); font-size: .71rem; }
  .reason { margin: 0; color: var(--warning); font-size: .69rem; line-height: 1.45; }

  .stops { display: flex; flex-direction: column; gap: .4rem; }
  .stops h3 { margin: 0; font-size: .74rem; color: var(--text-2); font-weight: 600;
              display: flex; align-items: center; gap: .35rem; }
  .row.stop { border-left-color: var(--text-3); }
  .row.stop.on { border-left-color: var(--success); }
  .row.stop.warn { border-left-color: var(--warning); }
  .row.stop.off { border-left-color: var(--text-3); opacity: .55; }
  .cover { display: flex; align-items: center; gap: .3rem; }
  .cover.on { color: var(--success); }
  .cover.warn { color: var(--warning); }
  .cover.idle, .cover.off { color: var(--text-3); }

  .icon { background: none; border: 0; cursor: pointer; color: var(--text-3);
          padding: .4rem; border-radius: var(--radius-sm); display: flex; }
  .icon:hover { background: var(--surface-3); }
  .icon.danger:hover { color: var(--danger); }

  .caveat { margin: 0; color: var(--text-3); font-size: .67rem; display: flex; gap: .3rem;
            align-items: flex-start; line-height: 1.5; }
</style>
