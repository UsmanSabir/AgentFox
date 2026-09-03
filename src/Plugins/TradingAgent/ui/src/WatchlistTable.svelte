<script lang="ts">
  import { tick } from 'svelte';
  import { Bell, BellOff, Pin, Lock, MoreHorizontal, X } from 'lucide-svelte';
  import type { WatchlistEntry, CandleArchiveStatus } from './api';
  import type { SymbolExtensionComponent } from './symbolExtensions';
  import LivePriceInline from './LivePriceInline.svelte';
  import { retainedWatchlistFocus, watchlistGridTarget, type WatchlistAction } from './watchlistNavigation';

  // Presentation only: no requests, timers, order types or mutation logic live in this component.
  export let entries: WatchlistEntry[];
  export let selected: string | null;
  export let archive: CandleArchiveStatus | null;
  export let rowStatus: SymbolExtensionComponent | null = null;
  export let busy = false;
  export let canReorder = false;
  export let onSelect: (symbol:string) => void;
  export let onAction: (action:WatchlistAction, symbol:string) => Promise<void>;
  export let onSearch: () => void;
  export let onDragStart: (event:DragEvent, entry:WatchlistEntry) => void;
  export let onDragOver: (event:DragEvent, entry:WatchlistEntry) => void;
  export let onDrop: (event:DragEvent, entry:WatchlistEntry) => void;
  export let onDragEnd: () => void;
  export let dragOverSymbol: string | null = null;

  let grid: HTMLDivElement;
  let menu: HTMLDialogElement;
  let focused: string | null = null;
  let column = 0;
  let menuSymbol: string | null = null;
  let actionBusy = false;
  let actionError = '';
  let returnColumn = 0;
  $: gaps = new Map((archive?.symbolsShortOfWeekly ?? []).map(g => [g.symbol,g]));
  $: reconcileFocus(entries, selected);
  function reconcileFocus(next:WatchlistEntry[], picked:string | null) {
    const retained = retainedWatchlistFocus(next.map(e => e.symbol), focused, picked);
    if (retained !== focused) {
      const restore = typeof document !== 'undefined' && grid?.contains(document.activeElement);
      focused = retained;
      if (restore && focused) void focusCell(focused, column);
    }
  }
  $: menuEntry = entries.find(e => e.symbol === menuSymbol);
  $: detail = entries.find(e => e.symbol === focused);
  $: menuIndex = entries.findIndex(e => e.symbol === menuSymbol);
  $: actions = menuEntry ? [
    { id:'order' as const, label:'New Order…', reason:'' },
    { id:'pin' as const, label:menuEntry.pinned ? 'Unpin from top' : 'Pin to top', reason:'' },
    { id:'alerts' as const, label:menuEntry.alertsEnabled ? 'Mute alerts' : 'Enable alerts', reason:'' },
    ...(menuEntry.tradable ? [{ id:'automation' as const, label:menuEntry.autoTradeEnabled ? 'Set manual-only' : 'Allow automation', reason:menuEntry.manualOnlyLocked ? 'Locked by ManualOnlySymbols configuration' : '' }] : []),
    ...(!menuEntry.hasWeeklyHistory ? [{ id:'history' as const, label:'Fetch missing history', reason:gaps.get(menuEntry.symbol)?.noEarlierHistory ? 'New listing — no earlier sessions exist' : archive?.progress.isRunning ? 'Backfill already running' : '' }] : []),
    { id:'up' as const, label:'Move up', reason:!canReorder ? 'Clear filters to reorder' : menuIndex <= 0 || entries[menuIndex - 1]?.pinned !== menuEntry.pinned ? 'First symbol in this pin group' : '' },
    { id:'down' as const, label:'Move down', reason:!canReorder ? 'Clear filters to reorder' : menuIndex >= entries.length - 1 || entries[menuIndex + 1]?.pinned !== menuEntry.pinned ? 'Last symbol in this pin group' : '' },
    { id:'remove' as const, label:'Remove from watchlist…', reason:'' }
  ] : [];

  async function focusCell(symbol:string, nextColumn:number) {
    focused = symbol; column = nextColumn;
    await tick();
    const row = Array.from(grid?.querySelectorAll<HTMLElement>('[data-symbol]') ?? []).find(e => e.dataset.symbol === symbol);
    const cell = row?.querySelector<HTMLElement>(`[data-col="${nextColumn}"]`);
    cell?.focus({preventScroll:true});
    cell?.scrollIntoView({block:'nearest',inline:'nearest'});
  }
  async function openActions(symbol:string, fromColumn = column) {
    menuSymbol = symbol; returnColumn = fromColumn; actionError = '';
    await tick();
    menu.showModal();
    menu.querySelector<HTMLButtonElement>('.action:not(:disabled)')?.focus();
  }
  function closeActions() {
    menu.close();
    if (!grid?.isConnected) { onSearch(); return; }
    const next = retainedWatchlistFocus(entries.map(e => e.symbol), menuSymbol, selected);
    if (next) void focusCell(next, returnColumn);
  }
  async function execute(action:WatchlistAction) {
    if (busy || actionBusy || !menuEntry || actions.find(a => a.id === action)?.reason) return;
    // Capture identity, never a row index that a quote/list refresh could retarget.
    const symbol = menuEntry.symbol;
    if (action === 'order') {
      // Let the existing order dialog acquire focus after this modal has returned it to the row.
      closeActions();
      await tick();
      await onAction(action, symbol);
      return;
    }
    actionBusy = true;
    try { await onAction(action, symbol); closeActions(); }
    catch (error) { actionError = error instanceof Error ? error.message : String(error); }
    finally { actionBusy = false; }
  }
  function gridKey(event:KeyboardEvent) {
    if (event.defaultPrevented || event.isComposing || event.altKey || !focused) return;
    const target = event.target as HTMLElement;
    if (target.closest('input,textarea,select,[contenteditable="true"]')) return;
    if (event.key === '/' && !event.ctrlKey && !event.metaKey) { event.preventDefault(); onSearch(); return; }
    if ((event.key === 'F10' && event.shiftKey) || event.key === 'ContextMenu' || event.key === 'F2') {
      event.preventDefault(); if (!event.repeat) void openActions(focused); return;
    }
    // The native Actions button handles Enter itself. Other cells select without moving focus.
    if ((event.key === 'Enter' || event.key === ' ') && column !== 2 && !event.ctrlKey && !event.metaKey) {
      event.preventDefault(); if (!event.repeat) onSelect(focused); return;
    }
    const row = entries.findIndex(e => e.symbol === focused);
    const rowHeight = grid.querySelector<HTMLElement>('[data-symbol]')?.offsetHeight ?? 44;
    const next = watchlistGridTarget(event.key,row,column,entries.length,Math.floor(grid.clientHeight / rowHeight),event.ctrlKey || event.metaKey);
    if (next) { event.preventDefault(); void focusCell(entries[next.row].symbol,next.column); }
  }
  function menuKey(event:KeyboardEvent) {
    if (event.key === 'Enter' && event.repeat) { event.preventDefault(); return; }
    if (!['ArrowDown','ArrowUp','Home','End'].includes(event.key)) return;
    event.preventDefault();
    const buttons = Array.from(menu.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'));
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : (index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length;
    buttons[next]?.focus();
  }
</script>

<div class="table-shell">
  <div class="grid" role="grid" tabindex="-1" aria-label="Watched symbols" aria-colcount="3" aria-rowcount={entries.length + 1} aria-describedby="watchlist-key-help" bind:this={grid} on:keydown={gridKey}>
    <div class="grid-head" role="row" aria-rowindex="1">
      <span role="columnheader">Symbol / status</span><span role="columnheader">Price / change</span><span role="columnheader" class="sr-only">Actions</span>
    </div>
    {#each entries as entry, index (entry.symbol)}
      <div class="grid-row" role="row" tabindex="-1" data-symbol={entry.symbol} aria-rowindex={index + 2} aria-selected={selected === entry.symbol}
        class:selected={selected === entry.symbol} class:drop-target={dragOverSymbol === entry.symbol}
        draggable={canReorder && !busy} on:dragstart={e => onDragStart(e,entry)} on:dragover={e => onDragOver(e,entry)} on:drop={e => onDrop(e,entry)} on:dragend={onDragEnd}>
        <div role="gridcell" class="identity" data-col="0" tabindex={focused === entry.symbol && column === 0 ? 0 : -1}
          aria-describedby={rowStatus ? 'watchlist-status-' + entry.symbol : undefined}
          on:focus={() => {focused = entry.symbol; column = 0;}} on:keydown={gridKey}
          on:click={event => {if (!(event.target as HTMLElement).closest('button,a,input,select,textarea')) onSelect(entry.symbol);}}
          on:contextmenu|preventDefault={() => openActions(entry.symbol,0)}
          aria-label={`${entry.symbol}, ${entry.companyName ?? 'company name unavailable'}, ${!entry.tradable ? 'monitor only' : entry.manualOnly ? 'manual only' : 'automation allowed'}${entry.manualOnlyLocked ? ', configuration locked' : ''}, ${entry.openAlerts} alerts${!entry.alertsEnabled ? ', alerts muted' : ''}`}>
          <span class="symbol">{#if entry.pinned}<Pin size={10}/>{/if}{entry.symbol}{#if entry.manualOnlyLocked}<Lock size={10}/>{/if}</span>
          <span class="company" title={entry.companyName ?? ''}>{entry.companyName ?? 'Name unavailable'}</span>
          <span class="tags"><span>{!entry.tradable ? 'Monitor' : entry.manualOnly ? 'Manual' : 'Auto'}</span>
            {#if entry.openAlerts > 0}<span class="alert-count"><Bell size={10}/>{entry.openAlerts}</span>{/if}
            {#if !entry.alertsEnabled}<BellOff size={10} aria-label="Alerts muted"/>{/if}
            {#if !entry.hasWeeklyHistory}<span class="history">{gaps.get(entry.symbol)?.noEarlierHistory ? 'New listing' : 'No weekly'}</span>{/if}
          </span>
          {#if rowStatus}<span class="extension" id={'watchlist-status-' + entry.symbol}><svelte:component this={rowStatus} symbol={entry.symbol}/></span>{/if}
        </div>
        <div role="gridcell" class="quote" data-col="1" tabindex={focused === entry.symbol && column === 1 ? 0 : -1}
          on:focus={() => {focused = entry.symbol; column = 1;}}>
          <LivePriceInline symbol={entry.symbol} fallbackChange={entry.dayChangePercent} showUnavailable/>
        </div>
        <button type="button" role="gridcell" class="actions-toggle" data-col="2" aria-label={`Actions for ${entry.symbol}`} aria-haspopup="dialog"
          tabindex={focused === entry.symbol && column === 2 ? 0 : -1} on:focus={() => {focused = entry.symbol; column = 2;}}
          on:click={() => openActions(entry.symbol,2)}><MoreHorizontal size={16}/></button>
      </div>
    {/each}
  </div>
  <div class="table-footer">
    <span>{entries.length} shown · {detail?.symbol ?? 'No focused row'}</span>
    <span id="watchlist-key-help">↑ ↓ navigate · Enter select · F2 / Shift F10 actions · / search</span>
  </div>
</div>

<dialog bind:this={menu} class="row-menu" aria-label={`Watchlist actions for ${menuSymbol ?? 'symbol'}`} on:keydown={menuKey}
  on:cancel|preventDefault={closeActions}>
  <header><div><strong>{menuSymbol}</strong><small>{menuEntry?.companyName ?? 'Symbol is no longer in this view'}</small></div>
    <button type="button" class="close" aria-label="Close watchlist actions" on:click={closeActions}><X size={16}/></button></header>
  {#if menuEntry}
    <p>{!menuEntry.tradable ? 'Monitor-only — not permitted by the execution universe.' : menuEntry.manualOnly ? 'Manual-only — your own orders still work; strategy entries and exits are disabled.' : 'Automation allowed, subject to all existing policy and risk controls.'}</p>
    {#if !menuEntry.hasWeeklyHistory}<p>{menuEntry.archivedBars} daily bars archived{archive ? ` / ${archive.dailyBarsForWeekly} needed for weekly levels` : ''}. {gaps.get(menuEntry.symbol)?.noEarlierHistory ? 'Earlier history does not exist for this new listing.' : `${gaps.get(menuEntry.symbol)?.missingSessions ?? 'Unknown'} sessions not yet requested.`}</p>{/if}
  {/if}
  {#if actionError}<p role="alert">{actionError}</p>{/if}
  {#each actions as action (action.id)}
    <button type="button" class="action" class:danger={action.id === 'remove'} disabled={busy || actionBusy || !!action.reason} on:click={() => execute(action.id)}>
      <span>{action.label}</span>{#if action.reason}<small>{action.reason}</small>{/if}
    </button>
  {/each}
  <small>↑ ↓ navigate · Enter choose · Esc return. Orders open the existing review form.</small>
</dialog>

<style>
  .table-shell { display:flex; flex-direction:column; flex:1 1 0; min-height:100px; min-width:0; }
  .grid { overflow:auto; flex:1 1 0; min-height:0; overscroll-behavior:contain; }
  .grid-head,.grid-row { display:grid; grid-template-columns:minmax(95px,1fr) minmax(68px,.75fr) 26px; align-items:stretch; }
  .grid-head { position:sticky; top:0; z-index:1; background:var(--surface-2); color:var(--text-3); padding:.4rem .15rem; font-size:.6rem; border-bottom:1px solid var(--border-md); }
  .grid-row { border-bottom:1px solid var(--border); color:var(--text); }
  .grid-row:hover { background:var(--surface-2); }
  .grid-row.selected { background:var(--primary-dim); box-shadow:inset 2px 0 var(--primary); }
  .grid-row.drop-target { border-top:2px solid var(--primary); }
  [role='gridcell'] { min-width:0; padding:.35rem .3rem; outline-offset:-2px; }
  [role='gridcell']:focus-visible { outline:2px solid var(--primary); background:var(--surface-2); }
  .identity { display:flex; flex-direction:column; gap:.1rem; cursor:pointer; }
  .symbol { font-family:ui-monospace,monospace; font-size:.73rem; font-weight:700; display:flex; align-items:center; gap:.2rem; }
  .company { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:.6rem; color:var(--text-2); }
  .tags { display:flex; align-items:center; flex-wrap:wrap; gap:.35rem; font-size:.56rem; color:var(--text-2); }
  .alert-count { display:inline-flex; align-items:center; gap:.1rem; color:var(--danger); }
  .history { color:var(--warning); }
  .extension { display:contents; }
  .extension :global(span) { white-space:normal; max-width:100%; }
  .quote { display:flex; align-items:center; justify-content:flex-end; flex-wrap:wrap; }
  .quote :global(.live-quote) { flex-wrap:wrap; justify-content:flex-end; }
  .actions-toggle { align-self:center; height:30px; border:0; background:transparent; color:var(--text-2); cursor:pointer; }
  .actions-toggle:hover { color:var(--primary); }
  .table-footer { display:flex; flex-direction:column; gap:.15rem; flex:none; padding:.35rem .1rem 0; color:var(--text-3); font-size:.57rem; }
  .row-menu { width:min(340px,calc(100vw - 2rem)); max-height:80dvh; overflow:auto; padding:.85rem; color:var(--text); background:var(--surface); border:1px solid var(--border-high); border-radius:8px; }
  .row-menu::backdrop { background:#0007; }
  .row-menu header { display:flex; justify-content:space-between; gap:.5rem; margin-bottom:.5rem; }
  .row-menu header div { display:flex; flex-direction:column; gap:.2rem; }
  .row-menu p,.row-menu small { color:var(--text-2); font-size:.68rem; line-height:1.5; }
  .row-menu button { font:inherit; cursor:pointer; border:1px solid var(--border); color:var(--text); background:var(--surface-2); border-radius:4px; }
  .row-menu button:focus-visible { outline:2px solid var(--primary); outline-offset:1px; }
  .row-menu button:hover { border-color:var(--primary); }
  .row-menu .action { display:flex; flex-direction:column; width:100%; text-align:left; padding:.45rem .55rem; margin-bottom:.35rem; font-size:.75rem; }
  .row-menu .action.danger { color:var(--danger); }
  .row-menu button:disabled { cursor:default; color:var(--text-3); }
  .close { align-self:flex-start; padding:.3rem; }
  .sr-only { position:absolute; width:1px; height:1px; overflow:hidden; clip-path:inset(50%); }
  @media (max-width:900px) {
    .table-shell { flex:none; height:clamp(220px,52vh,420px); min-height:0; }
  }
</style>
