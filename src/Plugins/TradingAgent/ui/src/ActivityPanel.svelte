<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import { trading, type TradingActivity, type TradingActivityFeed } from './api';
  import {
    Activity, ChevronDown, ChevronRight, AlertTriangle, XCircle, Chrome, Loader
  } from 'lucide-svelte';

  /**
   * What the trading agent is doing, and what it just did.
   *
   * <p>Collapsed by default and it stays that way: this is a reassurance surface, not a working one.
   * The header carries the two things worth knowing without opening it — whether a browser window on
   * screen is this system's, and whether anything has gone wrong — so the panel only needs opening
   * when one of those says so.</p>
   *
   * <p>Polling follows the same logic: every 4s while open, every 30s while closed and then only for
   * the counts behind the header chips. The list is always taken whole from the server rather than
   * merged locally — the server folds a repeated activity into its existing entry, and a client
   * keeping its own copy would go on showing a count that had since moved.</p>
   */

  /** Collapsed by default — see above. */
  let open = false;

  let feed: TradingActivityFeed | null = null;
  let entries: TradingActivity[] = [];
  let error: string | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let loading = true;

  const OPEN_POLL_MS = 4_000;
  const CLOSED_POLL_MS = 30_000;

  async function poll() {
    try {
      // A collapsed panel asks for the counts only; the entry list behind them is not on screen.
      const next = await trading.activity(open ? undefined : 1);
      error = null;
      if (open) entries = next.activities;
      feed = next;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
      timer = setTimeout(poll, open ? OPEN_POLL_MS : CLOSED_POLL_MS);
    }
  }

  function toggle() {
    open = !open;
    if (timer) clearTimeout(timer);
    poll();
  }

  onMount(poll);
  onDestroy(() => { if (timer) clearTimeout(timer); });

  const time = (iso: string) =>
    new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  /** Relative age, which is what "is this current?" actually asks. */
  function ago(iso: string) {
    const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    if (seconds < 60) return `${Math.round(seconds)}s ago`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
    return `${Math.round(seconds / 3600)}h ago`;
  }

  $: issues = (feed?.warnings ?? 0) + (feed?.errors ?? 0);
  $: feedDegraded = feed
    ? (feed.now.feedDegraded ?? !feed.now.feedHealthy)
    : false;
  $: feedProvider = (feed?.now.feedProvider ?? 'live').toLowerCase();
  $: feedLabel = feedProvider === 'ahl'
    ? 'AHL push'
    : `${feedProvider.toUpperCase()} feed`;
  $: feedReason = feed?.now.feedReason ?? 'Live quotes are falling back to the PSX market watch.';
</script>

<section class="activity" class:open>
  <button class="head" on:click={toggle} aria-expanded={open}>
    <span class="head-copy">
      {#if open}<ChevronDown size={14} />{:else}<ChevronRight size={14} />{/if}
      <Activity size={15} />
      <b>Activity</b>
      <span class="sub">What the trading agent is doing</span>
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
        <span class="chip danger"><XCircle size={11} /> {feed.errors}</span>
      {/if}
      {#if feed?.warnings}
        <span class="chip warn"><AlertTriangle size={11} /> {feed.warnings}</span>
      {/if}
      {#if feed && issues === 0 && !feedDegraded && !loading}
        <span class="chip ok">no issues</span>
      {/if}
      {#if loading}<span class="chip"><Loader size={11} /> …</span>{/if}
    </span>
  </button>

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

    {#if entries.length === 0}
      <p class="empty">
        Nothing recorded in the last {feed?.retentionMinutes ?? 120} minutes.
        <small>Activity appears when the agent reads the portal, places an order, or manages a stop.</small>
      </p>
    {:else}
      <ul class="list">
        {#each entries as item (item.seq)}
          <li class="row {item.level}">
            <span class="when" title={new Date(item.lastUtc).toLocaleString()}>{time(item.lastUtc)}</span>
            <span class="source">{item.source}</span>
            <span class="msg">
              {item.message}
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
        Newest first · repeats collapse into a count · kept for {feed?.retentionMinutes ?? 120}
        minutes, then dropped. The ledger and the log file are the durable records.
      </p>
    {/if}
  {/if}
</section>

<style>
  .activity {
    background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
    margin-bottom: 1.25rem; overflow: hidden;
  }
  .head {
    width: 100%; background: none; border: 0; cursor: pointer; font: inherit; color: var(--text);
    display: flex; align-items: center; justify-content: space-between; gap: 1rem;
    padding: .7rem 1rem; text-align: left;
  }
  .head:hover { background: var(--surface-2); }
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

  .list {
    list-style: none; margin: 0; padding: 0 1rem;
    display: flex; flex-direction: column; gap: .15rem;
    max-height: 300px; overflow-y: auto;
  }
  .row {
    display: grid; grid-template-columns: auto auto 1fr auto; gap: .5rem; align-items: baseline;
    padding: .3rem .5rem; border-radius: var(--radius-sm);
    background: var(--surface-2); border-left: 3px solid transparent;
  }
  /* Level is the only colour signal in the list, so it has to read at a glance. */
  .row.warn  { border-left-color: var(--warning); }
  .row.error { border-left-color: var(--danger); }

  .when   { font-family: ui-monospace, monospace; font-size: .65rem; color: var(--text-3); }
  .source { font-size: .6rem; color: var(--text-3); text-transform: uppercase; letter-spacing: .04em; min-width: 3.6rem; }
  .msg    { font-size: .73rem; color: var(--text-2); min-width: 0; }
  .row.warn .msg  { color: var(--text); }
  .row.error .msg { color: var(--text); }
  .msg em { display: block; color: var(--text-3); font-style: normal; font-size: .68rem; line-height: 1.5; }
  .repeat {
    font-size: .6rem; color: var(--text-3); border: 1px solid var(--border-md);
    border-radius: 999px; padding: 0 .3rem; margin-left: .25rem; white-space: nowrap;
  }
  .age    { font-size: .62rem; color: var(--text-3); white-space: nowrap; }

  @media (max-width: 720px) {
    .row { grid-template-columns: auto 1fr; }
    .source, .age { display: none; }
  }
</style>
