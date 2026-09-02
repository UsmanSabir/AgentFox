<script lang="ts">
  import { onMount } from 'svelte';
  import {
    trading, type WatchlistEntry, type WatchlistResponse, type CandleArchiveStatus,
    type WatchlistPresetPreview
  } from './api';
  import {
    Plus, RotateCcw, Trash2, Bell, BellOff, Eye, AlertTriangle, Clock, Search,
    Download, Pin, PinOff, GripVertical, PanelLeftClose, PanelLeftOpen,
    Bot, Hand, Lock, ListPlus, X, Sparkles
  } from 'lucide-svelte';
  import type { SymbolExtensionComponent } from './symbolExtensions';
  import LivePriceInline from './LivePriceInline.svelte';

  /** Selected symbol, so the chart pane (Phase 2) can follow the list. */
  export let selected: string | null = null;
  /** Human issuer name for the selected ticker; bound to the chart heading by the dashboard. */
  export let selectedCompany: string | null = null;
  /** Collapses the panel horizontally so the chart receives most of the workspace width. */
  export let compact = false;
  /** Shared dashboard clock; used to refresh live session moves without adding another timer. */
  export let refreshTick = 0;
  /** Session gate supplied by the dashboard status endpoint. Closed-market moves do not change. */
  export let marketOpen = false;
  /**
   * Optional component rendered in each row after the tags. Null in a community build. It receives
   * only `symbol` and must render nothing when it has nothing to say — see `symbolExtensions.ts`.
   */
  export let rowStatus: SymbolExtensionComponent | null = null;

  let data: WatchlistResponse | null = null;
  /**
   * Archive coverage, for the "no weekly" rows only: it says how many sessions each starved symbol was
   * never requested for, which is what the targeted backfill below would fetch. Optional — the list
   * still works without it, the offer just loses its numbers.
   */
  let archive: CandleArchiveStatus | null = null;
  /** True only while the FIRST paint is waiting for the symbol list; see load(). */
  let loading = true;
  /** True while the portal metadata pass (company names, live moves) is still in flight. */
  let enriching = false;
  let error: string | null = null;
  let notice: string | null = null;
  let input = '';
  let busy = false;
  /** Narrows the rows below by symbol — separate from `input`, which is for adding a new ticker. */
  let search = '';
  let searchInput: HTMLInputElement;
  /** When active, show only symbols that currently have one or more unacknowledged alerts. */
  let alertsOnly = false;
  let draggedSymbol: string | null = null;
  let dragOverSymbol: string | null = null;
  let preset: WatchlistPresetPreview | null = null;
  let presetLoading = false;
  let refreshingEntries = false;
  let configuredListNoteDismissed = false;
  let executionSourceNoteDismissed = false;
  const configuredListNoteStorageKey = 'trading-watchlist-configured-list-note-dismissed-v1';
  const executionSourceNoteStorageKey = 'trading-watchlist-execution-source-note-dismissed-v1';

  function dismissConfiguredListNote() {
    configuredListNoteDismissed = true;
    localStorage.setItem(configuredListNoteStorageKey, 'true');
  }

  function dismissExecutionSourceNote() {
    executionSourceNoteDismissed = true;
    localStorage.setItem(executionSourceNoteStorageKey, 'true');
  }

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

  /** Adopts a response without losing the selection when the list it was made against changed. */
  function apply(next: WatchlistResponse) {
    data = next;
    if (selected && !next.entries.some(entry => entry.symbol === selected)) selected = null;
    if (!selected && next.entries.length) selected = next.entries[0].symbol;
  }

  /**
   * Loads the list in TWO passes, because the two halves of it have wildly different costs.
   *
   * The symbols, their tags and their settings come from the local database and return in
   * milliseconds. Company names and the live session move come from the PSX portal, which on a cold
   * start — exactly the state a restart leaves the process in — can take ten seconds or fail. Asking
   * for both at once is what made the panel sit empty after every restart: the whole list waited on
   * the slowest field in it. So paint from the database first, then merge the portal metadata in.
   *
   * The spinner is shown only when there is nothing to keep. Every action in this panel reloads
   * through here, and blanking a hundred rows on each pin, mute or add is what made adding a symbol
   * look like it had emptied the watchlist.
   */
  async function load() {
    loading = data === null;
    error = null;
    try {
      apply(await trading.watchlist.list(false));
      loading = false;
      await enrich();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
    await loadArchive();
  }

  /** Never fatal: names and live moves are presentation metadata, and the list stands without them. */
  async function enrich() {
    enriching = true;
    try {
      apply(await trading.watchlist.list(true));
    } catch {
      /* keep the database-only rows; the next dashboard tick tries again */
    } finally {
      enriching = false;
    }
  }

  /**
   * Silently refreshes the list's live fields. The dashboard calls this once a minute while the
   * market is open; keeping it separate from load() avoids flashing the initial spinner and from
   * loadArchive() avoids an unrelated archive-status request on every market tick.
   */
  async function refreshEntries() {
    if (loading || busy || refreshingEntries) return;
    refreshingEntries = true;
    try {
      apply(await trading.watchlist.list());
    } catch {
      /* transient: keep the last successful values and retry on the next dashboard tick */
    } finally {
      refreshingEntries = false;
    }
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

  async function previewPreset(index: 'KSE100' | 'KSE30') {
    if (presetLoading || busy) return;
    presetLoading = true;
    error = null;
    try {
      preset = await trading.watchlist.previewPreset(index);
    } catch (e) {
      preset = null;
      error = e instanceof Error ? e.message : String(e);
    } finally {
      presetLoading = false;
    }
  }

  async function applyPreset(mode: 'merge' | 'replace') {
    if (!preset || busy || presetLoading) return;
    if (mode === 'replace' && !confirm(
      `Replace the watchlist with ${preset.label}?\n\n` +
      `${preset.outsideIndex} current symbol(s) will be removed and ${preset.missing} added. ` +
      `${preset.alreadyWatched} overlapping symbol(s) keep their pin, alerts, notes and manual-only settings.\n\n` +
      (preset.grantsTradingPermission
        ? `The watchlist is your execution universe. Index members will become tradable, subject to every risk control.`
        : `This changes monitoring only. It does not add anything to AllowedSymbols or grant trading permission.`)
    )) return;

    busy = true;
    error = null;
    notice = null;
    try {
      const result = await trading.watchlist.applyPreset(preset.index, mode);
      notice = `${result.message} ${result.added} added, ${result.removed} removed, ${result.preserved} preserved.` +
        (result.warning ? ` ${result.warning}` : '');
      preset = null;
      await load();
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
   * the exit is the point, but it is not something to discover later. It does NOT switch off the
   * orders you place or arm yourself; those keep working, which is what makes hand-managing possible.
   * The server's reply carries the one case the toggle cannot satisfy: a symbol pinned manual-only in
   * appsettings.
   */
  async function toggleAutoTrade(entry: WatchlistEntry) {
    if (busy || entry.manualOnlyLocked) return;
    if (entry.autoTradeEnabled && !confirm(
      `Set ${entry.symbol} to manual-only?\n\n` +
      `No strategy will trade it again — no automatic entries, and no protective stops or ` +
      `take-profits on the way out. The decisions become yours.\n\n` +
      `Your own orders still work, including ones you arm to fire on a level. It stays charted, ` +
      `scanned and alerted on.`
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

  async function setAllAutoTrading(enabled: boolean) {
    if (busy || !data?.entries.length) return;
    const total = data.entries.length;
    const action = enabled ? 'allow automation for' : 'set to manual-only';
    const consequence = enabled
      ? `A strategy may place entry and exit orders for them again, subject to every global policy and risk control.`
      : `No strategy will place entries, protective stops, or take-profits for them. The decisions become yours; your own orders, armed ones included, still work.`;
    if (!confirm(
      `${enabled ? 'Allow automation for all watched symbols' : 'Set all watched symbols to manual-only'}?\n\n` +
      `This will ${action} ${total} watched symbol(s). ${consequence}\n\n` +
      `You can still change individual symbols afterwards.`
    )) return;

    busy = true;
    error = null;
    notice = null;
    try {
      const result = await trading.watchlist.setAutoTrading(enabled);
      notice = enabled
        ? `Automation allowed for ${result.updated} watched symbol(s).`
        : `${result.updated} watched symbol(s) set to manual-only.`;
      if (result.message) notice += ` ${result.message}`;
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  function clearSearch() {
    search = '';
    searchInput?.focus();
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
        ? `\n\n${manual} symbol(s) are currently manual-only. Resetting clears that, and a strategy ` +
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
    await refreshEntries();
    await loadArchive();
  }

  onMount(() => {
    compact = localStorage.getItem('trading-watchlist-density') === 'compact';
    configuredListNoteDismissed = localStorage.getItem(configuredListNoteStorageKey) === 'true';
    executionSourceNoteDismissed = localStorage.getItem(executionSourceNoteStorageKey) === 'true';
    load();
  });

  // Resetting adopts the configured list and clears the server flag. Clear the local dismissal at
  // the same point so a genuinely later configuration change is visible again.
  $: if (data && !data.configuredListChanged && configuredListNoteDismissed) {
    configuredListNoteDismissed = false;
    localStorage.removeItem(configuredListNoteStorageKey);
  }

  // The parent does not advance this clock in a hidden tab. Keep the local visibility check as a
  // guard in case the component is reused under a different parent later.
  let lastRefreshTick = refreshTick;
  $: if (refreshTick !== lastRefreshTick) {
    lastRefreshTick = refreshTick;
    if (marketOpen && typeof document !== 'undefined' && !document.hidden) refreshEntries();
  }

  $: gapBySymbol = new Map(
    (archive?.symbolsShortOfWeekly ?? []).map(gap => [gap.symbol, gap]));
  $: openAlertCount = (data?.entries ?? []).reduce((total, entry) => total + entry.openAlerts, 0);
  // Counted against `manualOnly`, not the stored toggle: a symbol pinned in appsettings is just as
  // hand-managed as one switched off here, and the header is answering "how many do I place myself".
  // Restricted to tradable rows for the same reason the badge is — see the badge's comment.
  $: manualOnlyCount = (data?.entries ?? []).filter(e => e.tradable && e.manualOnly).length;
  $: watchlistControlsExecution = data?.executionUniverseSource === 'Watchlist';
  $: selectedCompany = data?.entries.find(entry => entry.symbol === selected)?.companyName ?? null;
  $: filteredEntries = (data?.entries ?? []).filter(entry =>
    (!alertsOnly || entry.openAlerts > 0) &&
    `${entry.symbol} ${entry.companyName ?? ''}`.toLowerCase().includes(search.trim().toLowerCase())
  );

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
          <!-- Said out loud because the rows are already on screen without their names and moves. A
               row showing only its ticker for a second is a load in progress, not a broken row. -->
          {#if enriching} · names and prices loading…{/if}
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

  <div class="preset-row" aria-label="Index watchlist presets">
    <span><ListPlus size={13} /> Index lists</span>
    <button type="button" on:click={() => previewPreset('KSE100')} disabled={busy || presetLoading}>
      KSE 100
    </button>
    <button type="button" on:click={() => previewPreset('KSE30')} disabled={busy || presetLoading}>
      KSE 30
    </button>
  </div>

  {#if preset}
    <div class="preset-card">
      <div class="preset-summary">
        <div>
          <b>{preset.label}</b>
          <span>{preset.count} members · {preset.missing} missing · {preset.alreadyWatched} already watched</span>
        </div>
        <button class="icon close" type="button" on:click={() => preset = null}
          aria-label="Close index list options" title="Close"><X size={13} /></button>
      </div>
      <div class="preset-actions">
        <button
          class="btn btn-primary"
          type="button"
          on:click={() => applyPreset('merge')}
          disabled={busy || preset.missing === 0 || preset.projectedMergeCount > preset.maxSymbols}
          title={preset.projectedMergeCount > preset.maxSymbols
            ? `Adding would exceed the ${preset.maxSymbols}-symbol limit`
            : 'Keep the current watchlist and add only missing index members'}
        >Add {preset.missing} missing</button>
        <button class="btn btn-ghost replace" type="button" on:click={() => applyPreset('replace')} disabled={busy}>
          Replace list
        </button>
      </div>
      <small>
        {#if preset.grantsTradingPermission}
          Execution universe — members become tradable, subject to all risk controls.
        {:else}
          Monitoring only — trading permissions stay unchanged.
        {/if}
        Source: {preset.source}.
        {#if preset.warning} {preset.warning}{/if}
      </small>
    </div>
  {/if}

  {#if data?.configuredListChanged && !configuredListNoteDismissed}
    <div class="note warn configured-list" role="note">
      <AlertTriangle size={13} />
      <span>
        The configured allowed-symbols list has changed since this watchlist was seeded. It is not
        updated automatically — that would discard your edits. Use Reset to adopt the new list.
      </span>
      <button
        class="note-close"
        type="button"
        on:click={dismissConfiguredListNote}
        aria-label="Dismiss configured symbols note"
        title="Dismiss this note"
      ><X size={13} /></button>
    </div>
  {/if}

  {#if watchlistControlsExecution && !executionSourceNoteDismissed}
    <div class="note execution-source" role="note">
      <span>
        Watchlist controls trading eligibility. Removing a symbol blocks new orders; adding one permits
        orders only after every normal risk and safety check passes.
      </span>
      <button
        class="note-close"
        type="button"
        on:click={dismissExecutionSourceNote}
        aria-label="Dismiss trading eligibility note"
        title="Dismiss this note"
      ><X size={13} /></button>
    </div>
  {/if}

  {#if error}<p class="note danger">{error}</p>{/if}
  {#if notice}<p class="note">{notice}</p>{/if}

  {#if loading}
    <!-- Rows rather than a line of text. The panel is stretched to the chart card's height by the
         grid, so a single centred note can land below the fold and read as "the list is empty" —
         which is the exact wrong answer while it is still loading. -->
    <ul class="rows skeleton" aria-hidden="true">
      {#each Array(7) as _}
        <li><span class="bar wide"></span><span class="bar narrow"></span></li>
      {/each}
    </ul>
    <p class="note" role="status" aria-live="polite">Loading watchlist…</p>
  {:else if !data?.entries.length}
    <p class="note">
      No symbols are being watched. Add a ticker above, or Reset to seed from the configured
      allowed-symbols list.
    </p>
  {:else}
    <div class="filter-row">
      <div class="search-row">
        <Search size={13} aria-hidden="true" />
        <input
          bind:this={searchInput}
          class="search-input"
          placeholder="Search watched symbols…"
          bind:value={search}
          spellcheck="false"
          aria-label="Search watched symbols"
        />
        {#if search}
          <button
            class="search-clear"
            type="button"
            on:click={clearSearch}
            aria-label="Clear watchlist search"
            title="Clear search"
          ><X size={13} /></button>
        {/if}
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
    <div class="automation-actions" aria-label="Bulk auto-trading actions">
      <span><Bot size={13} aria-hidden="true" /> Auto trading</span>
      <button
        type="button"
        on:click={() => setAllAutoTrading(true)}
        disabled={busy || data.entries.every(entry => entry.autoTradeEnabled)}
        title="Allow automation for every watched symbol"
      ><Bot size={13} aria-hidden="true" /> Allow all</button>
      <button
        type="button"
        on:click={() => setAllAutoTrading(false)}
        disabled={busy || data.entries.every(entry => !entry.autoTradeEnabled)}
        title="Set every watched symbol to manual-only"
      ><Hand size={13} aria-hidden="true" /> Manual-only all</button>
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
                <LivePriceInline symbol={entry.symbol} fallbackChange={entry.dayChangePercent} showPrice={!compact} />
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
                    ? 'Pinned manual-only in appsettings (ManualOnlySymbols) — no strategy will trade it, in either direction. Your own orders, armed ones included, still work.'
                    : 'Manual-only — no strategy entries, no automatic stops or take-profits. Your own orders, armed ones included, still work.'}
                >
                  {#if entry.manualOnlyLocked}<Lock size={11} />{:else}<Hand size={11} />{/if} manual
                </span>
              {/if}
              {#if !entry.hasWeeklyHistory}
                <!-- Two different situations wear the same badge otherwise: history nobody has
                     fetched yet, and history that does not exist because the ticker is new. Only the
                     first one is worth acting on, so they are named apart. -->
                {#if gapBySymbol.get(entry.symbol)?.noEarlierHistory}
                  <span
                    class="tag new-listing"
                    title="{entry.symbol} has {entry.archivedBars} archived session(s) and no earlier history — it had not started trading. Weekly support/resistance needs {archive?.dailyBarsForWeekly ?? 0} sessions and arrives as it trades; there is nothing to download."
                  >
                    <Sparkles size={11} /> new listing
                  </span>
                {:else}
                  <span
                    class="tag pending"
                    title="{entry.archivedBars} daily bars archived{archive ? ` of the ${archive.dailyBarsForWeekly} needed` : ''} — weekly support/resistance is not confirmed for this symbol yet.{gapBySymbol.get(entry.symbol)?.missingSessions ? ` ${gapBySymbol.get(entry.symbol)?.missingSessions} session(s) were never requested for it; use the download action to fetch them.` : ''}"
                  >
                    <Clock size={11} /> no weekly
                  </span>
                {/if}
              {/if}
            </span>
          </button>
          <!-- Outside the picker button on purpose: an extension may render its own controls, and
               nesting an interactive element inside a button is invalid and breaks keyboard use. -->
          {#if rowStatus}
            <span class="row-extension">
              <svelte:component this={rowStatus} symbol={entry.symbol} />
            </span>
          {/if}
          <div class="row-actions">
            <button
              class="icon pin"
              class:active={entry.pinned}
              title={entry.pinned ? 'Unpin from the top' : 'Pin to the top'}
              data-tooltip={entry.pinned ? 'Unpin' : 'Pin to top'}
              aria-label={entry.pinned ? `Unpin ${entry.symbol}` : `Pin ${entry.symbol}`}
              on:click={() => togglePin(entry)}
              disabled={busy}
            >
              {#if entry.pinned}<PinOff size={13} />{:else}<Pin size={13} />{/if}
            </button>
            <!-- Hidden for a new listing: the pass would run and find nothing, because the sessions it
                 is short of have not been traded yet. -->
            {#if !entry.hasWeeklyHistory && !gapBySymbol.get(entry.symbol)?.noEarlierHistory}
              <button
                class="icon"
                title="Fetch the daily sessions this symbol was never requested for, so weekly levels can be computed"
                data-tooltip="Fetch history"
                aria-label={`Fetch missing price history for ${entry.symbol}`}
                on:click={() => fillHistory(entry)}
                disabled={busy || archive?.progress.isRunning}
              >
                <Download size={13} />
              </button>
            {/if}
            <button
              class="icon"
              title={entry.alertsEnabled ? 'Mute alerts for this symbol' : 'Unmute alerts'}
              data-tooltip={entry.alertsEnabled ? 'Mute alerts' : 'Enable alerts'}
              aria-label={entry.alertsEnabled ? `Mute alerts for ${entry.symbol}` : `Enable alerts for ${entry.symbol}`}
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
                    ? 'Hand this symbol to yourself: no strategy will trade it, entry or exit. Your own orders still work.'
                    : 'Let a strategy trade this symbol again'}
                data-tooltip={entry.manualOnlyLocked
                  ? 'Manual-only locked'
                  : entry.autoTradeEnabled ? 'Set manual-only' : 'Allow automation'}
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
            <button class="icon danger" title="Remove from watchlist" data-tooltip="Remove"
              aria-label={`Remove ${entry.symbol} from watchlist`} on:click={() => remove(entry)} disabled={busy}>
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
    height:auto;
    max-height:none;
    align-self:stretch;
    min-height: 0;
    overflow:hidden;
    /* Ignore the list's 100+ row intrinsic height when the parent grid chooses its row height. The
       chart/details card establishes that height; this card then stretches to match it. */
    contain:size;
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

  .preset-row {
    display:flex; align-items:center; gap:.35rem; min-width:0;
    color:var(--text-3); font-size:.68rem;
  }
  .preset-row > span { display:flex; align-items:center; gap:.3rem; margin-right:auto; white-space:nowrap; }
  .preset-row button {
    border:1px solid var(--border-md); background:var(--surface-2); color:var(--text-2);
    border-radius:999px; padding:.22rem .5rem; font:inherit; font-size:.68rem; cursor:pointer;
  }
  .preset-row button:hover { color:var(--primary); border-color:color-mix(in srgb, var(--primary) 45%, var(--border)); }
  .preset-row button:disabled { opacity:.5; cursor:wait; }
  .preset-card {
    display:flex; flex-direction:column; gap:.5rem; padding:.65rem;
    border:1px solid color-mix(in srgb, var(--primary) 28%, var(--border));
    border-radius:var(--radius-sm); background:color-mix(in srgb, var(--primary) 5%, var(--surface-2));
  }
  .preset-summary { display:flex; justify-content:space-between; gap:.5rem; align-items:flex-start; }
  .preset-summary > div { display:flex; flex-direction:column; gap:.1rem; min-width:0; }
  .preset-summary b { color:var(--text); font-size:.78rem; }
  .preset-summary span, .preset-card small { color:var(--text-3); font-size:.65rem; line-height:1.45; }
  .preset-actions { display:flex; gap:.4rem; }
  .preset-actions .btn { font-size:.7rem; padding:.32rem .55rem; }
  .preset-actions .replace { color:var(--danger); }
  .preset-card .close { flex:0 0 auto; }

  .filter-row { display:flex; align-items:center; gap:.45rem; }
  .search-row {
    flex:1; min-width:0; display:flex; align-items:center; gap:.4rem;
    background:var(--surface-2); border:1px solid var(--border-md);
    border-radius:var(--radius-sm); padding:.4rem .6rem; color:var(--text-3);
  }
  .search-row:focus-within {
    border-color:var(--primary);
    box-shadow:0 0 0 2px color-mix(in srgb, var(--primary) 16%, transparent);
  }
  .search-input {
    flex:1; min-width:0; background:none; border:0; color:var(--text); font:inherit; font-size:.78rem;
  }
  .search-input::placeholder { color:var(--text-3); }
  .search-input:focus { outline:none; }
  .search-clear {
    flex:0 0 auto; display:grid; place-items:center; width:22px; height:22px; padding:0;
    border:0; border-radius:var(--radius-sm); background:transparent; color:var(--text-3);
    cursor:pointer;
  }
  .search-clear:hover { color:var(--text); background:var(--surface-3); }
  .search-clear:focus-visible { outline:2px solid var(--primary); outline-offset:1px; }
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
  .automation-actions {
    display:flex; align-items:center; gap:.35rem; flex-wrap:wrap;
  }
  .automation-actions > span {
    display:flex; align-items:center; gap:.3rem; margin-right:auto;
    color:var(--text-3); font-size:.68rem; font-weight:600;
  }
  .automation-actions button {
    display:flex; align-items:center; gap:.28rem; white-space:nowrap;
    background:var(--surface-2); border:1px solid var(--border-md);
    border-radius:var(--radius-sm); padding:.34rem .5rem; color:var(--text-2);
    font:inherit; font-size:.68rem; cursor:pointer;
    transition:color 160ms ease, border-color 160ms ease, background 160ms ease;
  }
  .automation-actions button:hover:not(:disabled) {
    color:var(--text); border-color:color-mix(in srgb, var(--primary) 45%, var(--border));
    background:var(--surface-3);
  }
  .automation-actions button:focus-visible { outline:2px solid var(--primary); outline-offset:1px; }
  .automation-actions button:disabled { opacity:.45; cursor:not-allowed; }

  .note {
    margin:0; color:var(--text-2); font-size:.72rem;
    display:flex; align-items:flex-start; gap:.4rem; line-height:1.5;
  }
  .note.warn { color:var(--warning); }
  .note.danger { color:var(--danger); }
  .configured-list > span { flex:1; min-width:0; }
  .note.execution-source {
    color:var(--info); padding:.42rem .52rem; border-radius:var(--radius-sm);
    border:1px solid color-mix(in srgb, var(--info) 28%, var(--border));
    background:color-mix(in srgb, var(--info) 7%, var(--surface-2));
  }
  .execution-source > span { flex:1; min-width:0; }
  .note-close {
    flex:0 0 auto; display:grid; place-items:center; width:22px; height:22px; padding:0;
    margin:-.18rem -.25rem 0 0; border:0; border-radius:var(--radius-sm);
    background:transparent; color:currentColor; cursor:pointer; opacity:.72;
  }
  .note-close:hover { opacity:1; background:color-mix(in srgb, currentColor 12%, transparent); }
  .note-close:focus-visible { opacity:1; outline:2px solid currentColor; outline-offset:1px; }

  /* Scrolls internally: a 150-symbol watchlist must not push the rest of the page off-screen. */
  .rows {
    list-style:none; margin:0; padding:0; display:flex; flex-direction:column; gap:2px;
    flex:1 1 0; min-height:0; max-height:none; overflow-y:auto; overflow-x:hidden;
    scrollbar-gutter:stable; overscroll-behavior:contain;
  }
  /* Four columns: handle, picker, optional extension, actions. With no extension the third column
     has no content and collapses to zero, so a community build is unchanged. */
  .rows li {
    display:grid; grid-template-columns:auto minmax(0,1fr) auto auto; align-items:center; gap:.3rem;
    width:100%; box-sizing:border-box;
    border-radius:var(--radius-sm); padding:.1rem .2rem .1rem .05rem;
    border:1px solid transparent;
    /* Anchors the picker's full-row hit area below. */
    position:relative;
  }
  .rows li:hover { background:var(--surface-2); }
  .rows li.selected { background:var(--primary-dim); }
  .rows li.pinned { border-color:color-mix(in srgb, var(--primary) 20%, transparent); }
  .rows li.drag-over { border-color:var(--primary); background:var(--primary-dim); }
  .rows li.dragging { opacity:.45; }
  .rows li.muted .symbol { color:var(--text-3); }

  .rows.skeleton li { display:flex; flex-direction:column; align-items:flex-start; gap:.25rem; padding:.5rem; }
  .rows.skeleton .bar {
    height:.55rem; border-radius:999px; background:var(--surface-3);
    animation:watchlist-skeleton 1.2s ease-in-out infinite;
  }
  .rows.skeleton .wide { width:42%; }
  .rows.skeleton .narrow { width:68%; opacity:.6; }
  @keyframes watchlist-skeleton { 0%,100% { opacity:.45; } 50% { opacity:.9; } }
  @media (prefers-reduced-motion: reduce) {
    .rows.skeleton .bar { animation:none; }
  }

  .drag-handle { color:var(--text-3); display:flex; cursor:grab; opacity:.55; }
  .drag-handle:active { cursor:grabbing; }

  .pick {
    min-width:0; width:100%; display:flex; flex-direction:column;
    align-items:flex-start; gap:.28rem;
    background:none; border:0; cursor:pointer; padding:.5rem; text-align:left;
    font:inherit; color:var(--text);
  }
  /*
   * Selecting a stock is the thing this list is for, and it used to be the smallest target on the
   * row: the picker column is squeezed by the actions and any row extension beside it, so on a row
   * carrying several tags the only reliably clickable pixels were the ticker itself. This stretches
   * the button's hit area over the WHOLE row without nesting anything inside it — the pseudo-element
   * carries no content, so the button keeps its accessible name and its keyboard behaviour.
   */
  .pick::after { content:''; position:absolute; inset:0; border-radius:inherit; }
  /* Everything genuinely interactive, plus the extension slot, stays above that overlay. */
  .rows li .drag-handle,
  .rows li .row-extension,
  .rows li .row-actions { position:relative; z-index:1; }
  .rows li:hover .pick::after { cursor:pointer; }
  .identity { display:flex; width:100%; min-width:0; overflow:hidden; flex-direction:column; gap:.08rem; }
  .symbol-line { display:flex; align-items:center; gap:.4rem; min-width:0; }
  .symbol { font-weight:600; font-size:.8rem; font-family:ui-monospace, monospace; }
  .company { color:var(--text-3); font-size:.64rem; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; width:100%; }
  .tags { display:flex; justify-content:flex-start; align-items:center; gap:.3rem; flex-wrap:wrap; }
  .tags:empty { display:none; }
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
  /* A fact about the stock, not a shortfall to chase — so it reads informational, not warning. */
  .tag.new-listing { color:var(--info); border-color:color-mix(in srgb, var(--info) 35%, transparent); }
  .tag.alert {
    color:var(--danger); border-color:color-mix(in srgb, var(--danger) 45%, transparent);
    background:color-mix(in srgb, var(--danger) 12%, transparent); font-weight:600;
  }

  .row-actions { display:flex; align-items:center; gap:.05rem; flex:0 0 auto; }
  .row-extension { display:flex; align-items:center; min-width:0; }
  .icon {
    background:none; border:0; cursor:pointer; color:var(--text-3);
    width:26px; height:26px; padding:0; box-sizing:border-box;
    border-radius:var(--radius-sm); display:grid; place-items:center; position:relative;
  }
  .icon:hover { background:var(--surface-3); color:var(--text); }
  .icon.danger:hover { color:var(--danger); }
  .icon.pin.active { color:var(--primary); }
  .icon:disabled { opacity:.5; cursor:wait; }
  .icon:focus-visible { outline:2px solid var(--primary); outline-offset:1px; }
  .row-actions .icon[data-tooltip]::after {
    content:attr(data-tooltip); position:absolute; right:calc(100% + .35rem); top:50%;
    transform:translateY(-50%) translateX(.15rem); z-index:30; opacity:0; pointer-events:none;
    padding:.28rem .45rem; border-radius:5px; white-space:nowrap;
    background:var(--text); color:var(--surface); box-shadow:0 4px 14px rgba(0,0,0,.18);
    font-size:.64rem; font-weight:600; line-height:1;
    transition:opacity 120ms ease, transform 120ms ease;
  }
  .row-actions .icon[data-tooltip]:hover::after,
  .row-actions .icon[data-tooltip]:focus-visible::after {
    opacity:1; transform:translateY(-50%) translateX(0);
  }

  /* Compact mode is a narrow rail, not merely shorter rows. Editing/search controls remain one click
     away in the expanded view while selection, pinning, alerts, and drag sorting stay visible. */
  .watchlist.compact { gap:.45rem; padding:.6rem .4rem; }
  .watchlist.compact header { align-items:center; gap:.25rem; }
  .watchlist.compact .head-copy { min-width:0; flex:1; }
  .watchlist.compact .head-copy > b { font-size:.72rem; white-space:nowrap; overflow:hidden; }
  .watchlist.compact .head-copy > span,
  .watchlist.compact header .btn,
  .watchlist.compact .add-row,
  .watchlist.compact .preset-row,
  .watchlist.compact .preset-card,
  .watchlist.compact .filter-row,
  .watchlist.compact .automation-actions,
  .watchlist.compact .note { display:none; }
  .watchlist.compact .header-actions { flex:0 0 auto; }
  .watchlist.compact .rows { gap:0; }
  .watchlist.compact .rows li { grid-template-columns:auto minmax(0,1fr) auto auto; gap:.15rem; padding:0 .1rem 0 0; }
  .watchlist.compact .drag-handle { flex:0 0 auto; }
  .watchlist.compact .pick { min-width:0; padding:.34rem .1rem; gap:.2rem; }
  .watchlist.compact .identity { min-width:0; }
  .watchlist.compact .symbol-line { flex-direction:column; align-items:flex-start; gap:0; }
  .watchlist.compact .symbol { font-size:.72rem; }
  .watchlist.compact .company { display:none; }
  .watchlist.compact .tags .tag:not(.alert),
  .watchlist.compact .row-actions .icon:not(.pin) { display:none; }
  .watchlist.compact .tag.alert { padding:.08rem .2rem; font-size:.58rem; }
  .watchlist.compact .row-actions { flex:0 0 auto; }
  .watchlist.compact .row-actions .icon { width:22px; height:22px; }

  /* In the stacked mobile layout the chart no longer establishes this panel's height. */
  @media (max-width: 900px) {
    .watchlist { height:auto; max-height:none; overflow:visible; contain:none; }
    .rows { flex:none; max-height:min(52vh, 420px); }
  }
</style>
