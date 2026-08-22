<script lang="ts">
  import { onMount } from 'svelte';
  import { trading, type WatchlistEntry, type WatchlistResponse, type CandleArchiveStatus } from './api';
  import {
    Plus, RotateCcw, Trash2, Bell, BellOff, Eye, AlertTriangle, Clock, Search,
    Download, Pin, PinOff, GripVertical, PanelLeftClose, PanelLeftOpen,
    ArrowUpRight, ArrowDownRight, Minus, Bot, Hand, Lock
  } from 'lucide-svelte';

  /** Selected symbol, so the chart pane (Phase 2) can follow the list. */
  export let selected: string | null = null;
  /** Human issuer name for the selected ticker; bound to the chart heading by the dashboard. */
  export let selectedCompany: string | null = null;
  /** Collapses the panel horizontally so the chart receives most of the workspace width. */
  export let compact = false;

  let data: WatchlistResponse | null = null;
  /**
   * Archive coverage, for the "no weekly" rows only: it says how many sessions each starved symbol was
   * never requested for, which is what the targeted backfill below would fetch. Optional — the list
   * still works without it, the offer just loses its numbers.
   */
  let archive: CandleArchiveStatus | null = null;
  let loading = true;
  let error: string | null = null;
  let notice: string | null = null;
  let input = '';
  let busy = false;
  /** Narrows the rows below by symbol — separate from `input`, which is for adding a new ticker. */
  let search = '';
  /** When active, show only symbols that currently have one or more unacknowledged alerts. */
  let alertsOnly = false;
  let draggedSymbol: string | null = null;
  let dragOverSymbol: string | null = null;

  function toggleCompact() {
    compact = !compact;
    localStorage.setItem('trading-watchlist-density', compact ? 'compact' : 'comfortable');
  }

  async function togglePin(entry: WatchlistEntry) {
    if (busy) return;
    busy = true;
    try {
      await trading.watchlist.update(entry.symbol, { pinned: !entry.pinned });
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  function startDrag(event: DragEvent, entry: WatchlistEntry) {
    if (search.trim() || alertsOnly) {
      event.preventDefault();
      return;
    }
    draggedSymbol = entry.symbol;
    event.dataTransfer?.setData('text/plain', entry.symbol);
    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
  }

  function dragOver(event: DragEvent, entry: WatchlistEntry) {
    if (!draggedSymbol || draggedSymbol === entry.symbol) return;
    const source = data?.entries.find(item => item.symbol === draggedSymbol);
    // Pinned and regular symbols are intentionally separate lanes. Crossing the boundary would make
    // a drop appear to work and then jump back to the pin-defined order after the next refresh.
    if (!source || source.pinned !== entry.pinned) return;
    event.preventDefault();
    dragOverSymbol = entry.symbol;
    if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
  }

  async function dropOn(event: DragEvent, target: WatchlistEntry) {
    event.preventDefault();
    const sourceSymbol = draggedSymbol;
    draggedSymbol = null;
    dragOverSymbol = null;
    if (!data || !sourceSymbol || sourceSymbol === target.symbol) return;

    const source = data.entries.find(item => item.symbol === sourceSymbol);
    if (!source || source.pinned !== target.pinned) return;

    const before = data.entries;
    const reordered = [...before];
    const from = reordered.findIndex(item => item.symbol === sourceSymbol);
    const to = reordered.findIndex(item => item.symbol === target.symbol);
    if (from < 0 || to < 0) return;
    const [moved] = reordered.splice(from, 1);
    reordered.splice(to, 0, moved);
    data = { ...data, entries: reordered };

    try {
      await trading.watchlist.reorder(reordered.map(item => item.symbol));
    } catch (e) {
      data = { ...data, entries: before };
      error = e instanceof Error ? e.message : String(e);
    }
  }

  async function load() {
    loading = true;
    error = null;
    try {
      data = await trading.watchlist.list();
      if (!selected && data.entries.length) selected = data.entries[0].symbol;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
    await loadArchive();
  }

  /** Never fatal: a missing archive status costs the badge its detail, not the list. */
  async function loadArchive() {
    try {
      archive = await trading.candleArchive();
    } catch {
      /* leave whatever was last known */
    }
  }

  /**
   * Fills the daily history one symbol was never asked for.
   *
   * Needed as its own action because the archive can be complete market-wide while this symbol is
   * starved: coverage is per (date, symbol), and a symbol added after the deep history was fetched was
   * never requested for any of those dates. An unscoped backfill finds every date on record and does
   * nothing, so without this the badge would never clear.
   */
  async function fillHistory(entry: WatchlistEntry) {
    if (busy) return;
    const missing = gapBySymbol.get(entry.symbol)?.missingSessions ?? 0;
    const needed = archive?.dailyBarsForWeekly ?? 0;

    if (!confirm(
      `Backfill the daily history ${entry.symbol} is missing?\n\n` +
      (missing > 0
        ? `${missing} session(s) were never requested for it — one portal request each, so expect ` +
          `roughly ${Math.max(1, Math.round(missing * 0.4 / 60))} minute(s).\n\n`
        : '') +
      `It has ${entry.archivedBars} bar(s)` +
      (needed > 0 ? ` and needs ${needed} before weekly levels can be computed` : '') + `.\n\n` +
      `Each day fetched returns the whole market, so every other archived symbol is stored too.`
    )) return;

    busy = true;
    error = null;
    notice = null;
    try {
      const result = await trading.startBackfill(undefined, [entry.symbol]);
      archive = result.status;
      notice = result.started
        ? `Backfilling ${entry.symbol}. Progress is on the Candle archive card below.`
        : `A backfill pass is already running — start this one once it finishes.`;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function add() {
    const symbol = input.trim().toUpperCase();
    if (!symbol || busy) return;
    busy = true;
    error = null;
    notice = null;
    try {
      const result = await trading.watchlist.add(symbol);
      // Two distinct non-error outcomes worth saying out loud: the symbol is already there, or it was
      // added but cannot be traded. Neither is a failure, and neither should be silent.
      notice = !result.added
        ? `${result.symbol} is already on the watchlist.`
        : [result.message, result.warning].filter(Boolean).join(' ') || null;
      input = '';
      await load();
      selected = result.symbol;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function remove(entry: WatchlistEntry) {
    if (busy) return;
    if (!confirm(
      `Remove ${entry.symbol} from the watchlist?\n\n` +
      `Its archived candle history and alert record are kept, so re-adding it later does not have to ` +
      `download two years of data again.`
    )) return;
    busy = true;
    try {
      await trading.watchlist.remove(entry.symbol);
      if (selected === entry.symbol) selected = null;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function toggleAlerts(entry: WatchlistEntry) {
    if (busy) return;
    busy = true;
    try {
      await trading.watchlist.update(entry.symbol, { alertsEnabled: !entry.alertsEnabled });
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  /**
   * Hands the symbol to you, or back to the machine. Confirmed on the way IN because it silently
   * stops protective stops being raised and take-profits being armed for the position — hand-managing
   * the exit is the point, but it is not something to discover later. The server's reply carries the
   * one case the toggle cannot satisfy: a symbol pinned manual-only in appsettings.
   */
  async function toggleAutoTrade(entry: WatchlistEntry) {
    if (busy || entry.manualOnlyLocked) return;
    if (entry.autoTradeEnabled && !confirm(
      `Set ${entry.symbol} to manual-only?\n\n` +
      `No automation will place an order for it again — no armed triggers, no strategy entries, and ` +
      `no protective stops or take-profits on the way out. You place every order for it yourself.\n\n` +
      `It stays charted, scanned and alerted on.`
    )) return;

    busy = true;
    try {
      const result = await trading.watchlist.update(
        entry.symbol, { autoTradeEnabled: !entry.autoTradeEnabled });
      notice = result.message ?? null;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function reset() {
    if (busy) return;
    const userAdded = data?.entries.filter(e => e.source === 'user').length ?? 0;
    const manual = data?.entries.filter(e => !e.autoTradeEnabled).length ?? 0;
    if (!confirm(
      `Reset the watchlist to the configured allowed-symbols list?\n\n` +
      (userAdded > 0
        ? `This discards ${userAdded} symbol(s) you added, along with any mute settings and notes.`
        : `Any mute settings and notes will be discarded.`) +
      // Called out separately: the others are cosmetic, this one hands symbols back to automation.
      (manual > 0
        ? `\n\n${manual} symbol(s) are currently manual-only. Resetting clears that, and automation ` +
          `will be free to trade them again — unless they are also listed in ManualOnlySymbols.`
        : ``)
    )) return;
    busy = true;
    try {
      const result = await trading.watchlist.reset();
      notice = `Watchlist reset to ${result.symbols} configured symbol(s).`;
      selected = null;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  /**
   * Called by the dashboard when an alert's state changes elsewhere — the openAlerts badge below is
   * otherwise only refreshed by this panel's own actions. Silent (no loading flag) since it can fire
   * on every acknowledged alert; a background refetch shouldn't flash the list back to a spinner.
   */
  export async function refresh() {
    if (busy) return;
    try {
      data = await trading.watchlist.list();
    } catch {
      /* transient: the next refresh or the panel's own actions will retry */
    }
    await loadArchive();
  }

  onMount(() => {
    compact = localStorage.getItem('trading-watchlist-density') === 'compact';
    load();
  });

  $: gapBySymbol = new Map(
    (archive?.symbolsShortOfWeekly ?? []).map(gap => [gap.symbol, gap]));
  $: openAlertCount = (data?.entries ?? []).reduce((total, entry) => total + entry.openAlerts, 0);
  // Counted against `manualOnly`, not the stored toggle: a symbol pinned in appsettings is just as
  // hand-managed as one switched off here, and the header is answering "how many do I place myself".
  // Restricted to tradable rows for the same reason the badge is — see the badge's comment.
  $: manualOnlyCount = (data?.entries ?? []).filter(e => e.tradable && e.manualOnly).length;
  $: selectedCompany = data?.entries.find(entry => entry.symbol === selected)?.companyName ?? null;
  $: filteredEntries = (data?.entries ?? []).filter(entry =>
    (!alertsOnly || entry.openAlerts > 0) &&
    `${entry.symbol} ${entry.companyName ?? ''}`.toLowerCase().includes(search.trim().toLowerCase())
  );

  function formatDayChange(value: number) {
    return `${value > 0 ? '+' : ''}${value.toFixed(2)}%`;
  }
</script>

<section class="watchlist" class:compact>
  <header>
    <div class="head-copy">
      <b>
        Watchlist
        {#if data}
          <span class="alert-count" title="{openAlertCount} unacknowledged alert(s)">({openAlertCount})</span>
        {/if}
      </b>
      <span>
        {#if data}
          {data.entries.length} / {data.maxSymbols} watched · {data.tradableSymbols} tradable{manualOnlyCount > 0 ? ` · ${manualOnlyCount} manual` : ''}
        {:else}Symbols monitored for trend, support, and resistance{/if}
      </span>
    </div>
    <div class="header-actions">
      <button
        class="icon density"
        class:active={compact}
        on:click={toggleCompact}
        aria-pressed={compact}
        aria-label={compact ? 'Expand watchlist' : 'Collapse watchlist to a narrow rail'}
        title={compact ? 'Expand watchlist' : 'Collapse watchlist to give the chart more space'}
      >{#if compact}<PanelLeftOpen size={14} />{:else}<PanelLeftClose size={14} />{/if}</button>
      <button class="btn btn-ghost" on:click={reset} disabled={busy || loading} title="Reset to the configured allowed-symbols list">
        <RotateCcw size={13} /> Reset
      </button>
    </div>
  </header>

  <form class="add-row" on:submit|preventDefault={add}>
    <input
      class="symbol-input"
      placeholder="Add ticker (e.g. OGDC)"
      bind:value={input}
      maxlength="16"
      spellcheck="false"
      disabled={busy}
    />
    <button class="btn btn-primary" type="submit" disabled={busy || !input.trim()}>
      <Plus size={13} /> Add
    </button>
  </form>

  {#if data?.configuredListChanged}
    <p class="note warn">
      <AlertTriangle size={13} />
      The configured allowed-symbols list has changed since this watchlist was seeded. It is not
      updated automatically — that would discard your edits. Use Reset to adopt the new list.
    </p>
  {/if}

  {#if error}<p class="note danger">{error}</p>{/if}
  {#if notice}<p class="note">{notice}</p>{/if}

  {#if loading}
    <p class="note">Loading watchlist…</p>
  {:else if !data?.entries.length}
    <p class="note">
      No symbols are being watched. Add a ticker above, or Reset to seed from the configured
      allowed-symbols list.
    </p>
  {:else}
    <div class="filter-row">
      <div class="search-row">
        <Search size={13} />
        <input
          class="search-input"
          placeholder="Search watched symbols…"
          bind:value={search}
          spellcheck="false"
        />
      </div>
      <button
        class="alerts-filter"
        class:active={alertsOnly}
        type="button"
        aria-pressed={alertsOnly}
        title={alertsOnly ? 'Show all watched symbols' : 'Show only symbols with unacknowledged alerts'}
        on:click={() => alertsOnly = !alertsOnly}
      >
        <Bell size={13} /> Alerts only
      </button>
    </div>
    {#if !filteredEntries.length}
      <p class="note">
        {#if alertsOnly && search.trim()}
          No symbols with open alerts match "{search}".
        {:else if alertsOnly}
          No watched symbols have open alerts.
        {:else}
          No watched symbols match "{search}".
        {/if}
      </p>
    {:else}
    <ul class="rows">
      {#each filteredEntries as entry (entry.symbol)}
        <li
          class:selected={selected === entry.symbol}
          class:muted={!entry.alertsEnabled}
          class:pinned={entry.pinned}
          class:drag-over={dragOverSymbol === entry.symbol}
          class:dragging={draggedSymbol === entry.symbol}
          draggable={!search.trim() && !alertsOnly}
          on:dragstart={(event) => startDrag(event, entry)}
          on:dragover={(event) => dragOver(event, entry)}
          on:drop={(event) => dropOn(event, entry)}
          on:dragend={() => { draggedSymbol = null; dragOverSymbol = null; }}
        >
          <span class="drag-handle" title={search.trim() || alertsOnly ? 'Clear filters to reorder' : 'Drag to reorder'}>
            <GripVertical size={13} />
          </span>
          <button
            class="pick"
            title={entry.companyName ? `${entry.symbol} — ${entry.companyName}` : entry.symbol}
            on:click={() => selected = entry.symbol}
          >
            <span class="identity">
              <span class="symbol-line">
                <span class="symbol">{entry.symbol}</span>
                {#if entry.dayChangePercent != null}
                  <span
                    class="day-change"
                    class:up={entry.dayChangePercent > 0}
                    class:down={entry.dayChangePercent < 0}
                    class:flat={entry.dayChangePercent === 0}
                    title="Today's move from the previous close: {formatDayChange(entry.dayChangePercent)}"
                    aria-label="Today's change {formatDayChange(entry.dayChangePercent)}"
                  >
                    {#if entry.dayChangePercent > 0}
                      <ArrowUpRight size={11} aria-hidden="true" />
                    {:else if entry.dayChangePercent < 0}
                      <ArrowDownRight size={11} aria-hidden="true" />
                    {:else}
                      <Minus size={11} aria-hidden="true" />
                    {/if}
                    {formatDayChange(entry.dayChangePercent)}
                  </span>
                {/if}
              </span>
              {#if entry.companyName}<span class="company">{entry.companyName}</span>{/if}
            </span>
            <span class="tags">
              {#if entry.openAlerts > 0}
                <span class="tag alert" title="{entry.openAlerts} unacknowledged alert(s)">
                  <Bell size={11} /> {entry.openAlerts}
                </span>
              {/if}
              {#if !entry.tradable}
                <span class="tag monitor" title="Not in AllowedSymbols — an order for it would be rejected by the risk engine">
                  <Eye size={11} /> monitor-only
                </span>
              {:else if entry.manualOnly}
                <!-- Only shown for a tradable symbol: "manual-only" on a name nothing may trade at all
                     would be describing a restriction that is not the operative one. -->
                <span
                  class="tag manual"
                  title={entry.manualOnlyLocked
                    ? 'Pinned manual-only in appsettings (ManualOnlySymbols) — you place every order for it; no automation will, in either direction.'
                    : 'Manual-only — you place every order for it. No armed triggers, no strategy entries, no automatic stops or take-profits.'}
                >
                  {#if entry.manualOnlyLocked}<Lock size={11} />{:else}<Hand size={11} />{/if} manual
                </span>
              {/if}
              {#if !entry.hasWeeklyHistory}
                <span
                  class="tag pending"
                  title="{entry.archivedBars} daily bars archived{archive ? ` of the ${archive.dailyBarsForWeekly} needed` : ''} — weekly support/resistance is not confirmed for this symbol yet.{gapBySymbol.get(entry.symbol)?.missingSessions ? ` ${gapBySymbol.get(entry.symbol)?.missingSessions} session(s) were never requested for it; use the download action to fetch them.` : ''}"
                >
                  <Clock size={11} /> no weekly
                </span>
              {/if}
            </span>
          </button>
          <div class="row-actions">
            <button
              class="icon pin"
              class:active={entry.pinned}
              title={entry.pinned ? 'Unpin from the top' : 'Pin to the top'}
              aria-label={entry.pinned ? `Unpin ${entry.symbol}` : `Pin ${entry.symbol}`}
              on:click={() => togglePin(entry)}
              disabled={busy}
            >
              {#if entry.pinned}<PinOff size={13} />{:else}<Pin size={13} />{/if}
            </button>
            {#if !entry.hasWeeklyHistory}
              <button
                class="icon"
                title="Fetch the daily sessions this symbol was never requested for, so weekly levels can be computed"
                on:click={() => fillHistory(entry)}
                disabled={busy || archive?.progress.isRunning}
              >
                <Download size={13} />
              </button>
            {/if}
            <button
              class="icon"
              title={entry.alertsEnabled ? 'Mute alerts for this symbol' : 'Unmute alerts'}
              on:click={() => toggleAlerts(entry)}
              disabled={busy}
            >
              {#if entry.alertsEnabled}<Bell size={13} />{:else}<BellOff size={13} />{/if}
            </button>
            {#if entry.tradable}
              <button
                class="icon"
                class:active={entry.manualOnly}
                title={entry.manualOnlyLocked
                  ? 'Pinned manual-only in appsettings — remove it from ManualOnlySymbols and restart to change this'
                  : entry.autoTradeEnabled
                    ? 'Hand this symbol to yourself: no automation will trade it, entry or exit'
                    : 'Let automation trade this symbol again'}
                aria-label={entry.autoTradeEnabled
                  ? `Set ${entry.symbol} to manual-only`
                  : `Allow automation for ${entry.symbol}`}
                on:click={() => toggleAutoTrade(entry)}
                disabled={busy || entry.manualOnlyLocked}
              >
                {#if entry.manualOnlyLocked}
                  <Lock size={13} />
                {:else if entry.autoTradeEnabled}
                  <Bot size={13} />
                {:else}
                  <Hand size={13} />
                {/if}
              </button>
            {/if}
            <button class="icon danger" title="Remove from watchlist" on:click={() => remove(entry)} disabled={busy}>
              <Trash2 size={13} />
            </button>
          </div>
        </li>
      {/each}
    </ul>
    {/if}
  {/if}
</section>

<style>
  .watchlist {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
    height: 100%;
    min-height: 0;
    display: flex;
    flex-direction: column;
    gap: .7rem;
  }
  header { display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; }
  .header-actions { display:flex; align-items:center; gap:.35rem; }
  .head-copy { display:flex; flex-direction:column; gap:.2rem; }
  .head-copy b { color:var(--text); font-size:.9rem; }
  .head-copy span { color:var(--text-3); font-size:.72rem; }
  .head-copy .alert-count {
    color:var(--danger); font-size:.72rem; font-weight:700; margin-left:.15rem;
  }
  header .btn { display:flex; align-items:center; gap:.35rem; white-space:nowrap; }
  .density.active { color:var(--primary); background:var(--primary-dim); }

  .add-row { display:flex; gap:.5rem; }
  .symbol-input {
    flex:1; background:var(--surface-2); border:1px solid var(--border-md);
    border-radius:var(--radius-sm); padding:.45rem .6rem; color:var(--text);
    font-size:.8rem; font-family:inherit; text-transform:uppercase;
  }
  .symbol-input::placeholder { color:var(--text-3); text-transform:none; }
  .symbol-input:focus { outline:none; border-color:var(--primary); }
  .add-row .btn { display:flex; align-items:center; gap:.35rem; }

  .filter-row { display:flex; align-items:center; gap:.45rem; }
  .search-row {
    flex:1; min-width:0; display:flex; align-items:center; gap:.4rem;
    background:var(--surface-2); border:1px solid var(--border-md);
    border-radius:var(--radius-sm); padding:.4rem .6rem; color:var(--text-3);
  }
  .search-input {
    flex:1; min-width:0; background:none; border:0; color:var(--text); font:inherit; font-size:.78rem;
  }
  .search-input::placeholder { color:var(--text-3); }
  .search-input:focus { outline:none; }
  .alerts-filter {
    display:flex; align-items:center; gap:.3rem; white-space:nowrap;
    background:var(--surface-2); border:1px solid var(--border-md);
    border-radius:var(--radius-sm); padding:.4rem .55rem; color:var(--text-3);
    font:inherit; font-size:.7rem; cursor:pointer;
  }
  .alerts-filter:hover { border-color:var(--danger); color:var(--text); }
  .alerts-filter.active {
    color:var(--danger); border-color:color-mix(in srgb, var(--danger) 55%, transparent);
    background:color-mix(in srgb, var(--danger) 12%, transparent);
  }

  .note {
    margin:0; color:var(--text-2); font-size:.72rem;
    display:flex; align-items:flex-start; gap:.4rem; line-height:1.5;
  }
  .note.warn { color:var(--warning); }
  .note.danger { color:var(--danger); }

  /* Scrolls internally: a 150-symbol watchlist must not push the rest of the page off-screen. */
  .rows {
    list-style:none; margin:0; padding:0; display:flex; flex-direction:column; gap:2px;
    flex:1; min-height:0; max-height:none; overflow-y:auto; overflow-x:hidden;
  }
  .rows li {
    display:flex; align-items:center; gap:.4rem;
    border-radius:var(--radius-sm); padding:.1rem .25rem .1rem .1rem;
    border:1px solid transparent;
  }
  .rows li:hover { background:var(--surface-2); }
  .rows li.selected { background:var(--primary-dim); }
  .rows li.pinned { border-color:color-mix(in srgb, var(--primary) 20%, transparent); }
  .rows li.drag-over { border-color:var(--primary); background:var(--primary-dim); }
  .rows li.dragging { opacity:.45; }
  .rows li.muted .symbol { color:var(--text-3); }

  .drag-handle { color:var(--text-3); display:flex; cursor:grab; opacity:.55; }
  .drag-handle:active { cursor:grabbing; }

  .pick {
    flex:1; display:flex; align-items:center; gap:.5rem; flex-wrap:wrap;
    background:none; border:0; cursor:pointer; padding:.4rem .5rem; text-align:left;
    font:inherit; color:var(--text);
  }
  .identity { display:flex; min-width:0; flex-direction:column; gap:.08rem; }
  .symbol-line { display:flex; align-items:center; gap:.4rem; min-width:0; }
  .symbol { font-weight:600; font-size:.8rem; font-family:ui-monospace, monospace; }
  .day-change {
    display:inline-flex; align-items:center; gap:.08rem; white-space:nowrap;
    font-size:.65rem; font-weight:700; font-variant-numeric:tabular-nums;
  }
  .day-change.up { color:var(--success); }
  .day-change.down { color:var(--danger); }
  .day-change.flat { color:var(--text-3); }
  .company { color:var(--text-3); font-size:.64rem; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; max-width:180px; }
  .tags { display:flex; gap:.3rem; flex-wrap:wrap; }
  .tag {
    display:inline-flex; align-items:center; gap:.2rem;
    font-size:.63rem; padding:.1rem .35rem; border-radius:999px;
    border:1px solid var(--border-md); color:var(--text-3);
  }
  .tag.monitor { color:var(--info); border-color:color-mix(in srgb, var(--info) 35%, transparent); }
  /* Deliberately quieter than .alert: a hand-managed symbol is a standing arrangement, not a problem. */
  .tag.manual {
    color:var(--text-2); border-color:var(--border-md);
    background:color-mix(in srgb, var(--text-3) 10%, transparent);
  }
  .tag.pending { color:var(--warning); border-color:color-mix(in srgb, var(--warning) 35%, transparent); }
  .tag.alert {
    color:var(--danger); border-color:color-mix(in srgb, var(--danger) 45%, transparent);
    background:color-mix(in srgb, var(--danger) 12%, transparent); font-weight:600;
  }

  .row-actions { display:flex; gap:.15rem; }
  .icon {
    background:none; border:0; cursor:pointer; color:var(--text-3);
    padding:.3rem; border-radius:var(--radius-sm); display:flex; align-items:center;
  }
  .icon:hover { background:var(--surface-3); color:var(--text); }
  .icon.danger:hover { color:var(--danger); }
  .icon.pin.active { color:var(--primary); }
  .icon:disabled { opacity:.5; cursor:wait; }

  /* Compact mode is a narrow rail, not merely shorter rows. Editing/search controls remain one click
     away in the expanded view while selection, pinning, alerts, and drag sorting stay visible. */
  .watchlist.compact { gap:.45rem; padding:.6rem .4rem; }
  .watchlist.compact header { align-items:center; gap:.25rem; }
  .watchlist.compact .head-copy { min-width:0; flex:1; }
  .watchlist.compact .head-copy > b { font-size:.72rem; white-space:nowrap; overflow:hidden; }
  .watchlist.compact .head-copy > span,
  .watchlist.compact header .btn,
  .watchlist.compact .add-row,
  .watchlist.compact .filter-row,
  .watchlist.compact .note { display:none; }
  .watchlist.compact .header-actions { flex:0 0 auto; }
  .watchlist.compact .rows { gap:0; }
  .watchlist.compact .rows li { gap:.15rem; padding:0 .1rem 0 0; }
  .watchlist.compact .drag-handle { flex:0 0 auto; }
  .watchlist.compact .pick { min-width:0; padding:.34rem .1rem; gap:.2rem; }
  .watchlist.compact .identity { min-width:0; }
  .watchlist.compact .symbol-line { flex-direction:column; align-items:flex-start; gap:0; }
  .watchlist.compact .symbol { font-size:.72rem; }
  .watchlist.compact .day-change { font-size:.58rem; }
  .watchlist.compact .company { display:none; }
  .watchlist.compact .tags .tag:not(.alert),
  .watchlist.compact .row-actions .icon:not(.pin) { display:none; }
  .watchlist.compact .tag.alert { padding:.08rem .2rem; font-size:.58rem; }
  .watchlist.compact .row-actions { flex:0 0 auto; }
  .watchlist.compact .row-actions .icon { padding:.2rem; }

  /* In the stacked mobile layout the chart no longer establishes this panel's height. */
  @media (max-width: 820px) {
    .watchlist { height:auto; }
    .rows { flex:none; max-height:min(52vh, 420px); }
  }
</style>
