<script lang="ts">
  /**
   * Persistent orders: every order the app will place, or keep placing, on the person's behalf, in one
   * panel (owner, 2026-11-17). It merges two tabs that answered the same question — Waiting orders
   * (triggers, scheduled orders, protective stops) and Persistent orders (keep-working intents) — and
   * that often held ONE order at two stages: a trigger with "keep working" fires into
   * a keep-working intent (`PersistentOrder.sourceArmedId`).
   *
   * It composes the two existing lists rather than re-implementing them: each keeps its own reads,
   * actions, confirmations and keyboard handling, which differ for good reasons (cancelling a trigger
   * places nothing; stopping a keep-working order may cancel a live broker order). This component owns
   * only what they share: the header, the approval strip, one search box, one history toggle, the
   * filter chips and a single empty state. It kept the name "Persistent orders" (owner) because users
   * know it, and nothing here contradicts it: a trigger waits for days, a repeat order is re-placed
   * daily and a stop-loss each session. The old keep-working list is the "Repeat until filled" chip.
   *
   * The broker's own book stays in Portfolio → Working orders. These are the app's instructions.
   */
  import { Zap, RefreshCw, Repeat2 } from 'lucide-svelte';
  import ArmedOrdersPanel from './ArmedOrdersPanel.svelte';
  import PersistentOrdersPanel from './PersistentOrdersPanel.svelte';
  import ApprovalStrip from './ApprovalStrip.svelte';
  import HelpTip from './HelpTip.svelte';
  import type { ArmedOrdersResponse } from './api';
  import type { OrderSideFilter } from './orderListNavigation';

  export let refreshTick = 0;
  export let keyboardMode = false;

  type Chip = 'all' | 'triggers' | 'repeat' | 'stops';
  let chip: Chip = 'all';
  let query = '';
  let showHistory = false;
  /** Buy / Sell view filter; the chip counts and the empty state follow it. */
  let side: OrderSideFilter = 'all';
  const sides: { id: OrderSideFilter; label: string }[] = [{ id: 'all', label: 'All' }, { id: 'BUY', label: 'Buy' }, { id: 'SELL', label: 'Sell' }];

  let triggersPanel: ArmedOrdersPanel;
  let repeatPanel: PersistentOrdersPanel;
  let approval: ArmedOrdersResponse['approval'] | null = null;
  let triggerCount = 0;
  let stopCount = 0;
  let repeatCount = 0;
  let triggersLoaded = false;
  let repeatLoaded = false;
  let sourceArmedIds: ReadonlySet<string> = new Set();

  /** Re-reads both lists. Called by the page after an order, a fill or a cancel elsewhere. */
  export async function load() {
    await Promise.all([triggersPanel?.load(), repeatPanel?.load()]);
  }

  $: counts = { all: triggerCount + repeatCount + stopCount, triggers: triggerCount, repeat: repeatCount, stops: stopCount };
  $: chips = [
    { id: 'all', label: 'All' },
    { id: 'triggers', label: 'On a trigger' },
    { id: 'repeat', label: 'Repeat until filled' },
    { id: 'stops', label: 'Stop-loss' }
  ] as { id: Chip; label: string }[];
  $: loaded = triggersLoaded && repeatLoaded;
  $: showRepeat = chip === 'all' || chip === 'repeat';
  $: empty = loaded && counts[chip] === 0;
</script>

<section class="auto-orders" aria-label="Persistent orders">
  <header>
    <div class="title">
      <Zap size={15}/>
      <b>Persistent orders</b>
      <HelpTip label="Help about persistent orders">
        Orders the app places for you when your conditions are met.
        <br/><br/><b>On a trigger</b>: sent when a price, an event or a date is reached.
        <br/><b>Repeat until filled</b>: re-placed every trading day until it fills or expires.
        <br/><b>Stop-loss</b>: protects shares you hold by selling if the price falls to your level.
        <br/><br/>Orders already at the broker are in Portfolio → Working orders.
      </HelpTip>
    </div>
    <div class="actions">
      <label class="toggle"><input type="checkbox" bind:checked={showHistory}
        on:change={() => setTimeout(load)}/> Show history</label>
      <button class="btn btn-ghost icon-only" on:click={load} title="Refresh" aria-label="Refresh persistent orders"><RefreshCw size={13}/></button>
    </div>
  </header>

  {#if approval}<ApprovalStrip {approval} on:changed={() => triggersPanel?.load()}/>{/if}

  <div class="chips" role="tablist" aria-label="Show persistent orders">
    {#each chips as c}
      <button role="tab" class="chip" aria-selected={chip === c.id} on:click={() => chip = c.id}>
        {c.label}{#if loaded}<span class="n">{counts[c.id]}</span>{/if}
      </button>
    {/each}
  </div>

  <div class="filters">
    {#if keyboardMode}
      <input class="find" type="search" bind:value={query} aria-label="Find persistent order" placeholder="Find symbol or state"/>
    {/if}
    <div class="sides" role="group" aria-label="Show buy or sell orders">
      {#each sides as s}
        <button type="button" class={s.id.toLowerCase()} aria-pressed={side === s.id} on:click={() => side = s.id}>{s.label}</button>
      {/each}
    </div>
  </div>

  {#if !loaded}<p class="empty">Loading…</p>{:else if empty}
    <p class="empty">
      {#if side !== 'all'}No {side === 'BUY' ? 'buy' : 'sell'} orders{chip === 'all' ? '' : ' here'}.{#if side === 'BUY' && chip === 'stops'} A stop-loss is always a sell.{/if}
      {:else if chip === 'stops'}No stop-loss is set. Add one from New Order or a holding's plan.
      {:else if chip === 'repeat'}Nothing repeating. Tick “Keep the unfilled remainder working” when placing a limit or stop-loss order.
      {:else if chip === 'triggers'}Nothing waiting for a trigger. Use New Order to schedule a date, or a chart level for a price.
      {:else}No persistent orders. Schedule one from New Order, set a price level on the chart, or tick “Keep the unfilled remainder working”.{/if}
    </p>
  {/if}

  <ArmedOrdersPanel bind:this={triggersPanel} embedded show={chip} {query} {side} {showHistory} {refreshTick} {keyboardMode} on:select
    hideArmedIds={sourceArmedIds}
    bind:approval bind:triggerCount bind:stopCount bind:loaded={triggersLoaded}>
    <div slot="between" class="between" class:hidden={!showRepeat}>
      {#if chip === 'all' && repeatCount}<h3 class="group"><Repeat2 size={13}/> Repeat until filled</h3>{/if}
      <PersistentOrdersPanel bind:this={repeatPanel} embedded hidden={!showRepeat} {query} {side} {showHistory} on:select
        {refreshTick} {keyboardMode}
        bind:orderCount={repeatCount} bind:loaded={repeatLoaded} bind:sourceArmedIds/>
    </div>
  </ArmedOrdersPanel>
</section>

<style>
  .auto-orders { background:var(--surface); border:1px solid var(--border); border-radius:var(--radius);
    padding:1rem; display:flex; flex-direction:column; gap:.7rem; margin-bottom:1.25rem; }
  header { display:flex; justify-content:space-between; align-items:center; gap:1rem; flex-wrap:wrap; }
  .title { display:flex; align-items:center; gap:.45rem; color:var(--primary); }
  .title b { color:var(--text); font-size:.9rem; }
  .actions { display:flex; align-items:center; gap:.6rem; flex-wrap:wrap; }
  .actions .btn { display:flex; align-items:center; gap:.35rem; }
  .toggle { display:flex; align-items:center; gap:.3rem; color:var(--text-3); font-size:.7rem; cursor:pointer; }
  .chips { display:flex; gap:.35rem; flex-wrap:wrap; }
  .chip { display:inline-flex; align-items:center; gap:.35rem; padding:.28rem .65rem; border-radius:999px;
    border:1px solid var(--border); background:var(--surface-2); color:var(--text-2); font-size:.72rem; cursor:pointer; }
  .chip:hover { color:var(--text); }
  .chip[aria-selected="true"] { background:color-mix(in srgb, var(--primary) 18%, transparent);
    border-color:var(--primary); color:var(--text); }
  .chip:focus-visible, input:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .n { font-size:.64rem; font-weight:700; color:var(--text-3); }
  .chip[aria-selected="true"] .n { color:var(--primary); }
  .actions .icon-only { padding:.35rem .45rem; }
  /* Search and the Buy / Sell filter share one row, so adding the filter costs no height. */
  .filters { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; }
  .find { flex:1 1 9rem; min-width:0; padding:.35rem; border:1px solid var(--border-md); border-radius:4px;
    background:var(--surface-2); color:var(--text); font-size:.75rem; }
  .sides { display:inline-flex; margin-left:auto; padding:.15rem; gap:.15rem; border:1px solid var(--border); border-radius:999px; background:var(--surface-2); }
  .sides button { border:0; background:transparent; color:var(--text-2); font-size:.72rem; padding:.2rem .6rem; border-radius:999px; cursor:pointer; }
  .sides button:hover { color:var(--text); }
  .sides button[aria-pressed="true"] { background:color-mix(in srgb, var(--primary) 18%, transparent); color:var(--text); }
  .sides button.buy[aria-pressed="true"] { background:color-mix(in srgb, var(--success) 22%, transparent); color:var(--success); }
  .sides button.sell[aria-pressed="true"] { background:color-mix(in srgb, var(--danger) 22%, transparent); color:var(--danger); }
  .sides button:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .empty { margin:0; color:var(--text-3); font-size:.74rem; padding:.9rem 0; text-align:center; line-height:1.6; }
  .between { display:flex; flex-direction:column; gap:.4rem; }
  .between.hidden { display:none; }
  .group { margin:0; display:flex; align-items:center; gap:.35rem; font-size:.74rem; font-weight:600; color:var(--text-2); }
</style>
