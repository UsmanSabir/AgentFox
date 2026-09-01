<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import {
    trading, MOVER_SCREENS, MOVER_SCREEN_LABELS,
    type MoverScreen, type MoversResponse, type SectorsResponse, type MoverRow
  } from './api';
  import {
    TrendingUp, TrendingDown, Activity, AlertTriangle, Lock, RefreshCw, Layers, ChevronRight
  } from 'lucide-svelte';

  /** Clicking a row selects that symbol, so the chart pane follows the screen. */
  export let selected: string | null = null;

  let screen: MoverScreen = 'gainers';
  /**
   * Defaults to KSE100 rather than the whole market on purpose. The unrestricted universe is 857
   * symbols, hundreds of which are effectively untradable, and an unfiltered gainers list is
   * dominated by names nobody can get size in. A trader wanting the long tail can widen it.
   */
  let index = 'KSE100';
  let limit = 15;
  let showSectors = false;

  /**
   * Collapsed until asked for.
   *
   * This is a market-wide screen, not a decision surface: it answers "what is moving" rather than
   * "what should I do", and it is the tallest thing on the page. Collapsed by default it stops pushing
   * the panels an operator actually acts on below the fold.
   *
   * A collapsed panel also stops POLLING — it refreshes every 30s and drives two backend calls per
   * refresh (movers plus sectors), both of which reach the analytics portal. Keeping that running for a
   * panel nobody is looking at spends the broker's session on nothing.
   */
  let open = false;
  let loadedOnce = false;

  let data: MoversResponse | null = null;
  let sectors: SectorsResponse | null = null;
  let loading = false;
  let error: string | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let requestInFlight = false;
  let reloadRequested = false;
  let disposed = false;

  const READY_REFRESH_MS = 30_000;
  // The first movers request often races the broker's startup login. Retry the cheap local endpoint
  // promptly while it is waiting; the backend still refuses to create a broker session itself.
  const WAITING_FOR_BROKER_MS = 2_000;

  const INDEX_OPTIONS = [
    { value: 'KSE100',  label: 'KSE 100' },
    { value: 'KSE30',   label: 'KSE 30' },
    { value: 'KMI30',   label: 'KMI 30' },
    { value: 'ALLSHR',  label: 'All Share' },
    { value: '',        label: 'Whole market' }
  ];

  async function load() {
    if (disposed) return;
    if (requestInFlight) {
      reloadRequested = true;
      return;
    }
    requestInFlight = true;
    if (timer) {
      clearTimeout(timer);
      timer = null;
    }

    try {
      const query = { screen, index: index || undefined, limit };
      const [movers, sectorData] = await Promise.all([
        trading.movers.list(query),
        showSectors ? trading.movers.sectors(index || undefined) : Promise.resolve(null)
      ]);
      data = movers;
      if (sectorData) sectors = sectorData;
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      requestInFlight = false;
      loading = false;
      if (reloadRequested) {
        reloadRequested = false;
        load();
      } else {
        scheduleNext();
      }
    }
  }

  function scheduleNext() {
    if (disposed || !open) return;
    if (timer) clearTimeout(timer);

    const waitingForBroker = data?.available === false
      && !data.hasToken
      && !data.handshakeCoolingDown;
    timer = setTimeout(() => {
      timer = null;
      if (typeof document !== 'undefined' && document.hidden) {
        scheduleNext();
        return;
      }
      load();
    }, waitingForBroker ? WAITING_FOR_BROKER_MS : READY_REFRESH_MS);
  }

  function toggle() {
    open = !open;
    if (!open) {
      // Stop the refresh loop rather than let it keep firing behind a closed panel.
      if (timer) clearTimeout(timer);
      timer = null;
      return;
    }
    if (loadedOnce) {
      // Reopening shows the last snapshot immediately and refreshes behind it, so the panel is never
      // blank on a re-open.
      scheduleNext();
      load();
      return;
    }
    loadedOnce = true;
    loading = true;
    load();
  }

  function pick(newScreen: MoverScreen) {
    if (newScreen === screen) return;
    screen = newScreen;
    loading = true;
    load();
  }

  // Deliberately does NOT load on mount: see `open`. The first fetch happens when someone opens it.
  onMount(() => {});

  onDestroy(() => {
    disposed = true;
    if (timer) clearTimeout(timer);
  });

  function fmtPkr(value?: number | null): string {
    if (value == null) return '-';
    if (value >= 1_000_000_000) return (value / 1_000_000_000).toFixed(2) + 'bn';
    if (value >= 1_000_000) return (value / 1_000_000).toFixed(1) + 'mn';
    if (value >= 1_000) return (value / 1_000).toFixed(0) + 'k';
    return value.toFixed(0);
  }

  function fmtNum(value?: number | null, digits = 2): string {
    return value == null ? '-' : value.toLocaleString(undefined, {
      minimumFractionDigits: digits, maximumFractionDigits: digits
    });
  }

  /** The column that matters differs per screen, so the table's last metric follows the screen. */
  function metricLabel(s: MoverScreen): string {
    switch (s) {
      case 'unusual_volume': return 'Vol × avg';
      case 'gap_up':
      case 'gap_down': return 'Gap %';
      case 'near_upper_cap': return 'To cap %';
      case 'near_lower_lock': return 'To lock %';
      case 'most_valuable': return 'Turnover';
      default: return 'Turnover';
    }
  }

  function metricValue(row: MoverRow, s: MoverScreen): string {
    switch (s) {
      case 'unusual_volume': return row.volumeVsAvg10Day == null ? '-' : row.volumeVsAvg10Day.toFixed(2) + '×';
      case 'gap_up':
      case 'gap_down': return fmtNum(row.gapPercent) + '%';
      case 'near_upper_cap': return fmtNum(row.distanceToUpperCapPercent) + '%';
      case 'near_lower_lock': return fmtNum(row.distanceToLowerLockPercent) + '%';
      default: return fmtPkr(row.turnoverPkr);
    }
  }

  /**
   * Reasons a row is not the clean signal it looks like. Surfaced per row because an ex-dividend
   * price drop is mechanical, and a symbol pinned at its cap cannot be bought any higher — both
   * change the decision, and neither is visible from the percentage alone.
   */
  function caveats(row: MoverRow): string[] {
    const out: string[] = [];
    if (row.exDividend) out.push('ex-dividend — drop is mechanical');
    if (row.exBonus) out.push('ex-bonus');
    if (row.exRights) out.push('ex-rights');
    if (row.atUpperCap) out.push('at upper cap — cannot buy higher');
    if (row.atLowerLock) out.push('at lower lock');
    return out;
  }

  $: breadth = data?.breadth ?? null;
  $: isClosed = data?.marketState != null && data.marketState !== 'OPN';
</script>

<div class="panel" class:collapsed={!open}>
  <header>
    <button class="disclose" on:click={toggle} aria-expanded={open}
            title={open ? 'Collapse Market Movers' : 'Expand Market Movers'}>
      <ChevronRight size={14} class={open ? 'turned' : ''} />
      <h3><Activity size={16} /> Market Movers</h3>
    </button>
    <div class="meta">
      {#if open && data?.marketState}
        <span class="state" class:closed={isClosed}>
          {isClosed ? 'Closed' : 'Open'}
        </span>
      {/if}
      {#if open && data?.asOf}<span class="as-of">as of {data.asOf}</span>{/if}
      {#if open}
        <button class="icon" title="Refresh" on:click={load} disabled={loading}>
          <RefreshCw size={14} class={loading ? 'spin' : ''} />
        </button>
      {/if}
    </div>
  </header>

{#if open}

  {#if data && !data.enabled}
    <p class="notice">
      The AHL analytics portal is disabled. Set <code>Plugins:AhlAnalytics:Enabled</code> to
      <code>true</code> to use market-wide screens.
    </p>
  {:else if data && data.available === false}
    <!-- Say which of the two very different causes it is. A held token means the handshake already
         succeeded, so blaming a missing broker session would send the operator to the wrong place. -->
    <p class="notice">
      <AlertTriangle size={14} />
      {#if data.hasToken}
        The portal is authenticated but the market snapshot was refused.
      {:else if data.handshakeCoolingDown}
        The analytics handshake failed and is paused for a few minutes. This panel deliberately does
        not retry it on a timer — the handshake needs a broker session, so retrying could cost a
        broker login each time.
      {:else}
        Waiting for the live broker session. This panel starts automatically as soon as AHK connects,
        then establishes the separate AHL analytics session without launching another broker login.
      {/if}
    </p>
    {#if data.error}
      <p class="error-detail">{data.error}</p>
    {/if}
  {:else}
    <div class="controls">
      <div class="screens">
        {#each MOVER_SCREENS as s}
          <button class:active={screen === s} on:click={() => pick(s)}>
            {MOVER_SCREEN_LABELS[s]}
          </button>
        {/each}
      </div>
      <div class="filters">
        <select bind:value={index} on:change={() => { loading = true; load(); }}>
          {#each INDEX_OPTIONS as opt}
            <option value={opt.value}>{opt.label}</option>
          {/each}
        </select>
        <select bind:value={limit} on:change={() => { loading = true; load(); }}>
          {#each [10, 15, 25, 50] as n}<option value={n}>Top {n}</option>{/each}
        </select>
        <label class="toggle">
          <input type="checkbox" bind:checked={showSectors} on:change={load} />
          <Layers size={13} /> Sectors
        </label>
      </div>
    </div>

    {#if breadth}
      <div class="breadth">
        <span class="up"><TrendingUp size={13} /> {breadth.advancing} up</span>
        <span class="down"><TrendingDown size={13} /> {breadth.declining} down</span>
        <span>{breadth.unchanged} flat</span>
        {#if breadth.atUpperCap > 0}<span class="cap"><Lock size={12} /> {breadth.atUpperCap} at cap</span>{/if}
        {#if breadth.atLowerLock > 0}<span class="lock"><Lock size={12} /> {breadth.atLowerLock} locked</span>{/if}
        <span class="turnover">Rs {fmtPkr(breadth.totalTurnoverPkr)} traded</span>
        <!-- traded/listed is the honest denominator: only symbols that ticked this session are ranked. -->
        <span class="muted">{breadth.tradedToday} of {breadth.totalListed} traded</span>
      </div>
    {/if}

    {#if error}
      <p class="error"><AlertTriangle size={14} /> {error}</p>
    {/if}

    {#if showSectors && sectors?.sectors?.length}
      <div class="sectors">
        {#each sectors.sectors.slice(0, 8) as s}
          <div class="sector" class:pos={s.medianChangePercent > 0} class:neg={s.medianChangePercent < 0}>
            <span class="name">{s.sectorName}</span>
            <span class="pct">{s.medianChangePercent > 0 ? '+' : ''}{fmtNum(s.medianChangePercent)}%</span>
            <span class="muted">{s.advancing}/{s.declining} · Rs {fmtPkr(s.totalTurnoverPkr)}</span>
          </div>
        {/each}
      </div>
    {/if}

    {#if loading && !data?.rows?.length}
      <p class="muted">Loading…</p>
    {:else if !data?.rows?.length}
      <p class="muted">
        No symbol matched. {isClosed ? 'The market has not traded in this session.' : 'Try widening the filters.'}
      </p>
    {:else}
      <table>
        <thead>
          <tr>
            <th>Symbol</th>
            <th class="num">Price</th>
            <th class="num">Change</th>
            <th class="num">Volume</th>
            <th class="num">{metricLabel(screen)}</th>
            <th class="num">RSI</th>
          </tr>
        </thead>
        <tbody>
          {#each data.rows as row}
            {@const flags = caveats(row)}
            <tr class:selected={selected === row.symbol} on:click={() => selected = row.symbol}>
              <td>
                <span class="sym">{row.symbol}</span>
                <span class="name">{row.name ?? ''}</span>
                {#if row.sector}<span class="sector-tag">{row.sector}</span>{/if}
                {#if flags.length}
                  <span class="flags" title={flags.join(' · ')}>
                    <AlertTriangle size={11} /> {flags.length}
                  </span>
                {/if}
              </td>
              <td class="num">{fmtNum(row.price)}</td>
              <td class="num" class:pos={(row.changePercent ?? 0) > 0} class:neg={(row.changePercent ?? 0) < 0}>
                {(row.changePercent ?? 0) > 0 ? '+' : ''}{fmtNum(row.changePercent)}%
              </td>
              <td class="num">{row.volume?.toLocaleString() ?? '-'}</td>
              <td class="num strong">{metricValue(row, screen)}</td>
              <td class="num" class:hot={(row.rsi ?? 0) > 70} class:cold={row.rsi != null && row.rsi < 30}>
                {row.rsi == null ? '-' : row.rsi.toFixed(0)}
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}
  {/if}
{/if}
</div>

<style>
  .panel { background: var(--surface); border: 1px solid var(--border); border-radius: 8px; padding: 1rem; }
  /* A collapsed panel is just its header, so the generous padding would leave it floating in space. */
  .panel.collapsed { padding-bottom: .55rem; }
  .disclose { display: flex; align-items: center; gap: .4rem; background: none; border: 0; padding: 0;
    cursor: pointer; color: inherit; font: inherit; }
  .disclose :global(svg.turned) { transform: rotate(90deg); }
  .disclose :global(svg) { transition: transform .12s ease; flex: none; }
  header { display: flex; justify-content: space-between; align-items: center; gap: .5rem; flex-wrap: wrap; }
  h3 { display: flex; align-items: center; gap: .4rem; margin: 0; font-size: .95rem; }
  .meta { display: flex; align-items: center; gap: .5rem; font-size: .75rem; color: var(--text-2); }
  .state { padding: .1rem .4rem; border-radius: 4px; background: #16a34a22; color: #16a34a; font-weight: 600; }
  .state.closed { background: #64748b22; color: var(--text-2); }
  button.icon { background: none; border: 0; color: var(--text-2); cursor: pointer; padding: .2rem; }
  :global(.spin) { animation: spin 1s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }

  .controls { display: flex; justify-content: space-between; gap: .6rem; flex-wrap: wrap; margin: .8rem 0 .5rem; }
  .screens { display: flex; gap: .25rem; flex-wrap: wrap; }
  .screens button {
    border: 1px solid var(--border); background: none; color: var(--text-2);
    border-radius: 5px; padding: .3rem .55rem; font-size: .75rem; cursor: pointer;
  }
  .screens button.active { color: #fff; background: var(--primary); border-color: var(--primary); }
  .filters { display: flex; gap: .4rem; align-items: center; }
  .filters select {
    background: var(--surface); color: var(--text); border: 1px solid var(--border);
    border-radius: 5px; padding: .28rem .4rem; font-size: .75rem;
  }
  .toggle { display: flex; align-items: center; gap: .25rem; font-size: .75rem; color: var(--text-2); cursor: pointer; }

  .breadth {
    display: flex; gap: .8rem; flex-wrap: wrap; font-size: .75rem; color: var(--text-2);
    padding: .45rem .1rem; border-top: 1px solid var(--border); border-bottom: 1px solid var(--border);
  }
  .breadth span { display: inline-flex; align-items: center; gap: .25rem; }
  .breadth .up { color: #16a34a; } .breadth .down { color: #dc2626; }
  .breadth .cap { color: #16a34a; } .breadth .lock { color: #dc2626; }
  .breadth .turnover { font-weight: 600; color: var(--text); }

  .sectors { display: flex; gap: .4rem; flex-wrap: wrap; margin: .6rem 0; }
  .sector {
    display: flex; flex-direction: column; gap: .1rem; padding: .35rem .5rem;
    border: 1px solid var(--border); border-radius: 5px; font-size: .7rem; min-width: 8rem;
  }
  .sector .name { font-weight: 600; }
  .sector.pos .pct { color: #16a34a; } .sector.neg .pct { color: #dc2626; }

  table { width: 100%; border-collapse: collapse; margin-top: .5rem; font-size: .8rem; }
  th { text-align: left; color: var(--text-2); font-weight: 500; font-size: .7rem;
       text-transform: uppercase; letter-spacing: .03em; padding: .35rem .4rem; }
  th.num, td.num { text-align: right; }
  tbody tr { border-top: 1px solid var(--border); cursor: pointer; }
  tbody tr:hover { background: var(--surface-2, #ffffff08); }
  tbody tr.selected { background: color-mix(in srgb, var(--primary) 12%, transparent); }
  td { padding: .4rem; }
  td .sym { font-weight: 600; }
  td .name { color: var(--text-2); font-size: .72rem; margin-left: .35rem; }
  .sector-tag { color: var(--text-2); font-size: .65rem; margin-left: .35rem; opacity: .75; }
  .flags { color: #d97706; font-size: .65rem; margin-left: .35rem; display: inline-flex; gap: .15rem; align-items: center; }
  td.strong { font-weight: 600; }
  .pos { color: #16a34a; } .neg { color: #dc2626; }
  .hot { color: #dc2626; } .cold { color: #2563eb; }
  .muted { color: var(--text-2); font-size: .78rem; }
  .notice, .error {
    display: flex; align-items: center; gap: .4rem; font-size: .8rem;
    color: var(--text-2); margin: .8rem 0 0;
  }
  .error { color: #dc2626; }
  .error-detail {
    font-size: .72rem; color: var(--text-2); margin: .3rem 0 0;
    font-family: ui-monospace, monospace; word-break: break-word;
  }
  code { font-size: .74rem; background: var(--surface-2, #ffffff10); padding: .05rem .25rem; border-radius: 3px; }

  @media (max-width: 720px) {
    .screens { overflow-x: auto; flex-wrap: nowrap; }
    .screens button { flex: 0 0 auto; }
    td .name, .sector-tag { display: none; }
  }
</style>
