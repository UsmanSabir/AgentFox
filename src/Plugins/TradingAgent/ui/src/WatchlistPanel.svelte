<script lang="ts">
  import { onMount } from 'svelte';
  import { trading, type WatchlistEntry, type WatchlistResponse } from './api';
  import { Plus, RotateCcw, Trash2, Bell, BellOff, Eye, AlertTriangle, Clock } from 'lucide-svelte';

  /** Selected symbol, so the chart pane (Phase 2) can follow the list. */
  export let selected: string | null = null;

  let data: WatchlistResponse | null = null;
  let loading = true;
  let error: string | null = null;
  let notice: string | null = null;
  let input = '';
  let busy = false;

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

  async function reset() {
    if (busy) return;
    const userAdded = data?.entries.filter(e => e.source === 'user').length ?? 0;
    if (!confirm(
      `Reset the watchlist to the configured allowed-symbols list?\n\n` +
      (userAdded > 0
        ? `This discards ${userAdded} symbol(s) you added, along with any mute settings and notes.`
        : `Any mute settings and notes will be discarded.`)
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
  }

  onMount(load);
</script>

<section class="watchlist">
  <header>
    <div class="head-copy">
      <b>Watchlist</b>
      <span>
        {#if data}
          {data.entries.length} / {data.maxSymbols} watched · {data.tradableSymbols} tradable
        {:else}Symbols monitored for trend, support, and resistance{/if}
      </span>
    </div>
    <button class="btn btn-ghost" on:click={reset} disabled={busy || loading} title="Reset to the configured allowed-symbols list">
      <RotateCcw size={13} /> Reset
    </button>
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
    <ul class="rows">
      {#each data.entries as entry (entry.symbol)}
        <li class:selected={selected === entry.symbol} class:muted={!entry.alertsEnabled}>
          <button class="pick" on:click={() => selected = entry.symbol}>
            <span class="symbol">{entry.symbol}</span>
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
              {/if}
              {#if !entry.hasWeeklyHistory}
                <span class="tag pending" title="{entry.archivedBars} daily bars archived — weekly support/resistance needs roughly two years, so it is not confirmed yet">
                  <Clock size={11} /> no weekly
                </span>
              {/if}
            </span>
          </button>
          <div class="row-actions">
            <button
              class="icon"
              title={entry.alertsEnabled ? 'Mute alerts for this symbol' : 'Unmute alerts'}
              on:click={() => toggleAlerts(entry)}
              disabled={busy}
            >
              {#if entry.alertsEnabled}<Bell size={13} />{:else}<BellOff size={13} />{/if}
            </button>
            <button class="icon danger" title="Remove from watchlist" on:click={() => remove(entry)} disabled={busy}>
              <Trash2 size={13} />
            </button>
          </div>
        </li>
      {/each}
    </ul>
  {/if}
</section>

<style>
  .watchlist {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
    display: flex;
    flex-direction: column;
    gap: .7rem;
  }
  header { display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; }
  .head-copy { display:flex; flex-direction:column; gap:.2rem; }
  .head-copy b { color:var(--text); font-size:.9rem; }
  .head-copy span { color:var(--text-3); font-size:.72rem; }
  header .btn { display:flex; align-items:center; gap:.35rem; white-space:nowrap; }

  .add-row { display:flex; gap:.5rem; }
  .symbol-input {
    flex:1; background:var(--surface-2); border:1px solid var(--border-md);
    border-radius:var(--radius-sm); padding:.45rem .6rem; color:var(--text);
    font-size:.8rem; font-family:inherit; text-transform:uppercase;
  }
  .symbol-input::placeholder { color:var(--text-3); text-transform:none; }
  .symbol-input:focus { outline:none; border-color:var(--primary); }
  .add-row .btn { display:flex; align-items:center; gap:.35rem; }

  .note {
    margin:0; color:var(--text-2); font-size:.72rem;
    display:flex; align-items:flex-start; gap:.4rem; line-height:1.5;
  }
  .note.warn { color:var(--warning); }
  .note.danger { color:var(--danger); }

  /* Scrolls internally: a 150-symbol watchlist must not push the rest of the page off-screen. */
  .rows {
    list-style:none; margin:0; padding:0; display:flex; flex-direction:column; gap:2px;
    max-height:min(52vh, 420px); overflow-y:auto; overflow-x:hidden;
  }
  .rows li {
    display:flex; align-items:center; gap:.4rem;
    border-radius:var(--radius-sm); padding:.1rem .25rem .1rem .1rem;
  }
  .rows li:hover { background:var(--surface-2); }
  .rows li.selected { background:var(--primary-dim); }
  .rows li.muted .symbol { color:var(--text-3); }

  .pick {
    flex:1; display:flex; align-items:center; gap:.5rem; flex-wrap:wrap;
    background:none; border:0; cursor:pointer; padding:.4rem .5rem; text-align:left;
    font:inherit; color:var(--text);
  }
  .symbol { font-weight:600; font-size:.8rem; font-family:ui-monospace, monospace; }
  .tags { display:flex; gap:.3rem; flex-wrap:wrap; }
  .tag {
    display:inline-flex; align-items:center; gap:.2rem;
    font-size:.63rem; padding:.1rem .35rem; border-radius:999px;
    border:1px solid var(--border-md); color:var(--text-3);
  }
  .tag.monitor { color:var(--info); border-color:color-mix(in srgb, var(--info) 35%, transparent); }
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
  .icon:disabled { opacity:.5; cursor:wait; }
</style>
