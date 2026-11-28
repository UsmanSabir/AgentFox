<script lang="ts">
  import { onMount, onDestroy, untrack } from 'svelte';
  import { trading, type TradingActivity, type TradingActivityFeed } from './api';
  import {
    displayMessage, filterActivity, kindLabel, kindOf, mergeActivity, routineCandleUpdateCount, tradeKindFilters, viewCounts,
    type ActivityKindFilter, type ActivityView
  } from './activityFeed';
  import {
    Activity, ChevronDown, ChevronRight, AlertTriangle, XCircle, Chrome, Loader, ListFilter, Mail
  } from 'lucide-svelte';

  interface Props {
    /**
   * What the trading agent did TODAY (PKT), for the trader, with the diagnostics one click away.
   *
   * <p>The classic dashboard starts collapsed. Workspace tabs already own visibility and show the
   * content directly, polling only while the panel is on screen. The classic header carries browser
   * ownership and issue counts without needing to expand its list.</p>
   *
   * <p>Polling: every 4s while open, every 30s while closed and then only for the counts behind the
   * header chips. While open it asks only for what changed since the version it last saw, and merges
   * by `seq` — a repeat changes a row in place and bumps its version, so the count it shows is the
   * server's. A server that went backwards (a restart) or moved to a new day is read whole again.</p>
   *
   * <p>Two views. Trades: orders, stops, triggers, strategy actions, copies and every message sent to
   * the person. System: reads, connections, candle sources. Warnings and errors appear in both.</p>
   */
    embedded?: boolean;
    active?: boolean;
    /** Opens a stock's chart; a host that has a chart passes it. */
    onSelect?: ((symbol: string) => void) | null;
  }

  let { embedded = false, active = true, onSelect = null }: Props = $props();
  let expanded = $state(false);
  let open = $derived(embedded || expanded);
  let mounted = $state(false);
  let inFlight = false;
  type PollMode = 'off' | 'counts' | 'full';
  let pollMode: PollMode = 'off';

  let feed: TradingActivityFeed | null = $state<TradingActivityFeed | null>(null);
  let entries: TradingActivity[] = $state([]);
  let heldVersion = 0;
  let heldDay: string | undefined;
  let error: string | null = $state<string | null>(null);
  let timer: ReturnType<typeof setTimeout> | null = null;
  let loading = $state(true);

  let view: ActivityView = $state('trades');
  let kind: ActivityKindFilter = $state('all');
  let stock = $state('');
  let showRoutineCandles = $state(false);

  const OPEN_POLL_MS = 4_000;
  const CLOSED_POLL_MS = 30_000;

  /** Today's rows: only the change since the last read when the server can say what changed, otherwise all of them. */
  async function readEntries(): Promise<TradingActivityFeed> {
    if (heldVersion > 0) {
      const changed = await trading.activity(undefined, heldVersion);
      const sameDay = changed.keptSinceUtc === heldDay;
      if (sameDay && (changed.version ?? 0) >= heldVersion) {
        entries = mergeActivity(entries, changed.activities, changed.oldestSeq);
        heldVersion = changed.version ?? heldVersion;
        return changed;
      }
    }
    const whole = await trading.activity();
    entries = whole.activities;
    heldVersion = whole.version ?? 0;
    heldDay = whole.keptSinceUtc;
    return whole;
  }

  async function poll() {
    if (!mounted || pollMode === 'off' || inFlight) return;
    const requestedMode = pollMode;
    inFlight = true;
    try {
      // A collapsed panel asks for the counts only; the entry list behind them is not on screen.
      const next = requestedMode === 'full' ? await readEntries() : await trading.activity(1);
      if (!mounted) return;
      error = null;
      feed = next;
    } catch (e) {
      if (mounted) error = e instanceof Error ? e.message : String(e);
    } finally {
      inFlight = false;
      loading = false;
      if (mounted) {
        if (pollMode !== requestedMode) void poll();
        else timer = setTimeout(poll, requestedMode === 'full' ? OPEN_POLL_MS : CLOSED_POLL_MS);
      }
    }
  }

  function setPollingMode(mode: PollMode) {
    if (pollMode === mode) return;
    pollMode = mode;
    if (timer) clearTimeout(timer);
    timer = null;
    if (mode !== 'off') void poll();
  }

  onMount(() => { mounted = true; });
  $effect.pre(() => {
    // Reads only what the old `$:` named; the call is untracked so its own bookkeeping cannot re-trigger it.
    const m = mounted, mode = embedded && !active ? 'off' : open ? 'full' : 'counts';
    untrack(() => { if (m) setPollingMode(mode); });
  });
  onDestroy(() => { mounted = false; if (timer) clearTimeout(timer); });

  const time = (iso: string) =>
    new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  /** Relative age, which is what "is this current?" actually asks. */
  function ago(iso: string) {
    const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (seconds < 60) return `${Math.round(seconds)}s ago`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
    return `${Math.round(seconds / 3600)}h ago`;
  }

  let issues = $derived((feed?.warnings ?? 0) + (feed?.errors ?? 0));
  let feedDegraded = $derived(feed
    ? (feed.now.feedDegraded ?? !feed.now.feedHealthy)
    : false);
  let feedProvider = $derived((feed?.now.feedProvider ?? 'live').toLowerCase());
  let feedLabel = $derived(feedProvider === 'ahl'
    ? 'AHL push'
    : `${feedProvider.toUpperCase()} feed`);
  let feedReason = $derived(feed?.now.feedReason ?? 'Live quotes are falling back to the PSX market watch.');
  let counts = $derived(viewCounts(entries));
  let routineCandleUpdates = $derived(routineCandleUpdateCount(entries));
  let visibleEntries = $derived(filterActivity(entries, { view, kind: view === 'trades' ? kind : 'all', stock, showRoutineCandles }));
  let filtered = $derived(stock.trim() !== '' || (view === 'trades' && kind !== 'all'));
  // An older host keeps a rolling window rather than the day; say what it actually kept.
  let windowWords = $derived(feed?.keptSinceUtc ? 'today' : `in the last ${feed?.retentionMinutes ?? 120} minutes`);
</script>

{#snippet activityHeading()}
    <span class="head-copy">
      {#if !embedded}{#if open}<ChevronDown size={14} />{:else}<ChevronRight size={14} />{/if}{/if}
      <Activity size={15} />
      <b>Activity</b>
      <span class="sub">{feed?.keptSinceUtc ? 'Today' : 'What the trading agent is doing'}</span>
    </span>

    <span class="head-state">
      {#if feed?.now.browserBusy}
        <span class="chip busy" title="A browser window is open because this system is driving the broker portal">
          <Chrome size={11} /> broker portal open
        </span>
      {/if}
      {#if feedDegraded}
        <span class="chip warn" title={feedReason}>
          {feedLabel} degraded
        </span>
      {/if}
      {#if feed?.errors}
        <span class="chip danger" title="Errors today"><XCircle size={11} /> {feed.errors}</span>
      {/if}
      {#if feed?.warnings}
        <span class="chip warn" title="Warnings today"><AlertTriangle size={11} /> {feed.warnings}</span>
      {/if}
      {#if feed && issues === 0 && !feedDegraded && !loading}
        <span class="chip ok">no issues</span>
      {/if}
      {#if loading}<span class="chip"><Loader size={11} /> …</span>{/if}
    </span>
{/snippet}

<section class="activity" class:open class:embedded>
  {#if embedded}
    <div class="head static-head">{@render activityHeading()}</div>
  {:else}
    <button class="head" onclick={() => expanded = !expanded} aria-expanded={open}>{@render activityHeading()}</button>
  {/if}

  {#if open}
    {#if error}
      <p class="error">Activity unavailable: {error}</p>
    {/if}

    {#if feed}
      <p class="now">
        Market {feed.now.marketOpen ? 'open' : 'closed'} — {feed.now.marketReason}.
        {#if feed.now.browserBusy}
          A broker browser session is <b>in use right now</b>.
        {/if}
        {#if feedDegraded}
          <b>{feedReason}</b>
        {/if}
      </p>
    {/if}

    <div class="controls">
      <div class="segments" role="group" aria-label="Activity view">
        <button type="button" aria-pressed={view === 'trades'} onclick={() => view = 'trades'}>Trades <span>{counts.trades}</span></button>
        <button type="button" aria-pressed={view === 'system'} onclick={() => view = 'system'}>System <span>{counts.system}</span></button>
      </div>
      {#if view === 'trades'}
        <select bind:value={kind} aria-label="Show activity of kind">
          {#each tradeKindFilters as f (f.id)}<option value={f.id}>{f.label}</option>{/each}
        </select>
      {:else if routineCandleUpdates > 0}
        <button
          type="button"
          class="filter"
          class:active={showRoutineCandles}
          onclick={() => showRoutineCandles = !showRoutineCandles}
          aria-pressed={showRoutineCandles}
        >
          <ListFilter size={12} aria-hidden="true" />
          {showRoutineCandles ? 'Hide' : 'Show'} routine candles
          <span>{routineCandleUpdates} {showRoutineCandles ? 'shown' : 'hidden'}</span>
        </button>
      {/if}
      <input class="stock" type="search" bind:value={stock} placeholder="Stock" aria-label="Show activity for one stock"
        maxlength="16" autocomplete="off"/>
    </div>

    {#if loading && !feed}
      <p class="empty" role="status">Loading activity…</p>
    {:else if !error && visibleEntries.length === 0}
      <p class="empty">
        {#if filtered}
          Nothing matches these filters {windowWords}.
        {:else if view === 'trades'}
          No orders, stops, triggers or messages to you {windowWords}.
          <small>System shows what the agent read and connected to.</small>
        {:else if routineCandleUpdates > 0 && !showRoutineCandles}
          No other system activity {windowWords}.
          <small>Routine candle diagnostics are hidden by the filter above.</small>
        {:else}
          Nothing recorded {windowWords}.
        {/if}
      </p>
    {:else if visibleEntries.length}
      <ul class="list">
        {#each visibleEntries as item (item.seq)}
          {@const k = kindOf(item)}
          <li class="row {item.level}">
            <span class="when" title={new Date(item.lastUtc).toLocaleString()}>{time(item.lastUtc)}</span>
            <span class="stock-cell">
              {#if item.symbol && onSelect}
                <button type="button" class="symbol" onclick={() => onSelect?.(item.symbol!)} title={`Open the ${item.symbol} chart`}>{item.symbol}</button>
              {:else if item.symbol}<b>{item.symbol}</b>{/if}
            </span>
            <span class="kind" data-kind={k}>{#if k === 'notified'}<Mail size={10} aria-hidden="true" />{/if}{view === 'system' && k === 'system' ? item.source : kindLabel[k]}</span>
            <span class="msg">
              {displayMessage(item)}
              {#if item.repeats > 0}
                <!-- The count IS the information for a recurring line: "it opened the browser again"
                     is noise, "it opened the browser 14 times in the last hour" is a finding. -->
                <span class="repeat" title="Repeated since {new Date(item.utc).toLocaleTimeString()}">
                  ×{item.repeats + 1}
                </span>
              {/if}
              {#if item.detail}<em>{item.detail}</em>{/if}
            </span>
            <span class="age">{ago(item.lastUtc)}</span>
          </li>
        {/each}
      </ul>
      <p class="foot">
        {#if feed?.keptSinceUtc}
          Today since midnight PKT · newest first · repeats fold into a count · cleared on restart. The ledger and the log
          file are the durable records.
        {:else}
          Newest first · repeats collapse into a count · kept for {feed?.retentionMinutes ?? 120} minutes, then dropped.
        {/if}
      </p>
    {/if}
  {/if}
</section>

<style>
  .activity {
    background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
    margin-bottom: 1.25rem; overflow: hidden; container-type: inline-size;
  }
  .activity.embedded { border:0; border-radius:0; margin-bottom:0; }
  .head {
    width: 100%; background: none; border: 0; cursor: pointer; font: inherit; color: var(--text);
    display: flex; align-items: center; justify-content: space-between; gap: 1rem;
    padding: .7rem 1rem; text-align: left;
  }
  .head:not(.static-head):hover { background: var(--surface-2); }
  .static-head { box-sizing:border-box; cursor:default; }
  .head-copy { display: flex; align-items: center; gap: .45rem; color: var(--primary); min-width: 0; }
  .head-copy b { color: var(--text); font-size: .85rem; }
  .sub { color: var(--text-3); font-size: .7rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .head-state { display: flex; align-items: center; gap: .35rem; flex-wrap: wrap; }

  .chip {
    font-size: .6rem; padding: .1rem .4rem; border-radius: 999px;
    border: 1px solid var(--border-md); color: var(--text-3);
    display: inline-flex; align-items: center; gap: .2rem; white-space: nowrap;
  }
  .chip.ok     { color: var(--success); border-color: color-mix(in srgb, var(--success) 35%, transparent); }
  .chip.warn   { color: var(--warning); border-color: color-mix(in srgb, var(--warning) 35%, transparent); }
  .chip.danger { color: var(--danger);  border-color: color-mix(in srgb, var(--danger) 35%, transparent); }
  .chip.busy   { color: var(--info);    border-color: color-mix(in srgb, var(--info) 35%, transparent); }

  .now, .error, .empty, .foot { margin: 0; padding: 0 1rem; font-size: .72rem; line-height: 1.55; }
  .now  { color: var(--text-2); padding-bottom: .5rem; }
  .now b { color: var(--info); }
  .error { color: var(--danger); padding-bottom: .6rem; }
  .empty { color: var(--text-3); padding-bottom: 1rem; display: flex; flex-direction: column; gap: .25rem; }
  .empty small { color: var(--text-3); opacity: .8; font-size: .68rem; }
  .foot { color: var(--text-3); font-size: .65rem; padding: .5rem 1rem .8rem; }

  .controls { display: flex; align-items: center; flex-wrap: wrap; gap: .4rem; padding: 0 1rem .55rem; }
  .segments { display: inline-flex; border: 1px solid var(--border-md); border-radius: .45rem; overflow: hidden; }
  .segments button {
    min-height: 28px; padding: .2rem .6rem; border: 0; background: transparent; color: var(--text-2);
    font: inherit; font-size: .72rem; cursor: pointer; display: inline-flex; align-items: center; gap: .3rem;
  }
  .segments button + button { border-left: 1px solid var(--border-md); }
  .segments button[aria-pressed="true"] { background: var(--surface-2); color: var(--text); }
  .segments button span { color: var(--text-3); font-size: .65rem; font-variant-numeric: tabular-nums; }
  .segments button:focus-visible, select:focus-visible, .stock:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
  select, .stock {
    min-height: 28px; padding: .15rem .4rem; border: 1px solid var(--border-md); border-radius: .4rem;
    background: var(--surface-2); color: var(--text); font: inherit; font-size: .72rem;
  }
  .stock { width: 6.5rem; margin-left: auto; text-transform: uppercase; }
  .filter {
    min-height: 28px; padding: .25rem .5rem; cursor: pointer; font: inherit;
    display: inline-flex; align-items: center; gap: .35rem;
    border: 1px solid var(--border-md); border-radius: 999px;
    background: var(--surface-2); color: var(--text-2); font-size: .68rem;
  }
  .filter:hover { border-color: var(--primary); color: var(--text); }
  .filter:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
  .filter.active { border-color: var(--primary); color: var(--primary); }
  .filter span {
    padding-left: .35rem; border-left: 1px solid var(--border-md);
    color: var(--text-3); font-variant-numeric: tabular-nums;
  }

  .list {
    list-style: none; margin: 0; padding: 0 1rem;
    display: flex; flex-direction: column; gap: .15rem;
    max-height: 300px; overflow-y: auto;
  }
  .activity.embedded .list { max-height: none; }
  .row {
    display: grid; grid-template-columns: auto 4.2rem auto 1fr auto; gap: .5rem; align-items: baseline;
    padding: .3rem .5rem; border-radius: var(--radius-sm);
    background: var(--surface-2); border-left: 3px solid transparent;
  }
  /* Level is the only colour signal in the list, so it has to read at a glance. */
  .row.warn  { border-left-color: var(--warning); }
  .row.error { border-left-color: var(--danger); }

  .when   { font-family: ui-monospace, monospace; font-size: .65rem; color: var(--text-3); }
  .stock-cell { font-size: .7rem; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .stock-cell b { color: var(--text); }
  .symbol {
    padding: 0; border: 0; background: none; font: inherit; font-weight: 700; color: var(--primary);
    cursor: pointer; text-decoration: underline; text-underline-offset: 2px;
  }
  .symbol:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
  .kind {
    font-size: .58rem; color: var(--text-3); text-transform: uppercase; letter-spacing: .04em; white-space: nowrap;
    display: inline-flex; align-items: center; gap: .2rem;
  }
  .kind[data-kind="notified"] { color: var(--info); }
  .msg    { font-size: .73rem; color: var(--text-2); min-width: 0; }
  .row.warn .msg  { color: var(--text); }
  .row.error .msg { color: var(--text); }
  .msg em { display: block; color: var(--text-3); font-style: normal; font-size: .68rem; line-height: 1.5; overflow-wrap: anywhere; }
  .repeat {
    font-size: .6rem; color: var(--text-3); border: 1px solid var(--border-md);
    border-radius: 999px; padding: 0 .3rem; margin-left: .25rem; white-space: nowrap;
  }
  .age    { font-size: .62rem; color: var(--text-3); white-space: nowrap; }

  /* A docked panel is usually narrow, so the layout follows the panel, not the window. */
  @container (max-width: 520px) {
    .row { grid-template-columns: auto auto 1fr; row-gap: .1rem; }
    .msg { grid-column: 1 / -1; }
    .age { display: none; }
    .stock { margin-left: 0; }
  }
  @media (pointer: coarse) { .segments button, select, .stock, .filter { min-height: 44px; } }
</style>
