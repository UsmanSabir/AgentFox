<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { Check, Filter, Plus, Search, Trash2, X } from 'lucide-svelte';
  import type { WatchlistEntry } from './api';
  import {
    addWatchlistList, changeWatchlistMembership, listNameLimit, matchingFilters, readWatchlistLists, watchlistListsKey,
    watchlistSectors, writeWatchlistLists, type WatchlistFilter, type WatchlistList
  } from './watchlistLists';

  interface Props {
    entries: WatchlistEntry[];
    loadingSectors: boolean;
    activeFilter?: WatchlistFilter | null;
    restoreRowFocus: (symbol: string) => void;
  }

  let {
    entries,
    loadingSectors,
    activeFilter = $bindable(null),
    restoreRowFocus
  }: Props = $props();

  let lists: WatchlistList[] = $state([]);
  let selection: Pick<WatchlistFilter, 'kind' | 'name'> | null = $state<Pick<WatchlistFilter, 'kind' | 'name'> | null>(null);
  let trigger: HTMLButtonElement = $state()!;
  let menu: HTMLDialogElement = $state()!;
  let editor: HTMLDialogElement = $state()!;
  let nameInput: HTMLInputElement = $state()!;
  let menuOpen = $state(false);
  let menuError = $state('');
  let editorError = $state('');
  let editSymbol: string | null = $state<string | null>(null);
  let before: string[] = [];
  let checked: string[] = $state([]);
  let newName = $state('');
  let showName = $state(false);
  let saving = $state(false);
  // The search box appears only when the menu is long enough to scroll; the query lives while the menu is open.
  let searchable = $state(false);
  let query = $state('');
  let searchRow: HTMLDivElement | undefined = $state();
  let searchInput: HTMLInputElement | undefined = $state();


  function loadLists() {
    try {
      lists = readWatchlistLists(localStorage);
      if (selection?.kind === 'list' && !lists.some(list => list.name === selection?.name)) selection = null;
    } catch {
      menuError = 'Saved lists could not be read in this browser.';
    }
  }

  onMount(() => {
    loadLists();
    function sync(event: StorageEvent) {
      if (event.key === watchlistListsKey || event.key === null) loadLists();
    }
    window.addEventListener('storage', sync);
    document.addEventListener('visibilitychange', loadLists);
    return () => {
      window.removeEventListener('storage', sync);
      document.removeEventListener('visibilitychange', loadLists);
    };
  });

  async function saveLists(update: (latest: WatchlistList[]) => WatchlistList[]) {
    const save = () => {
      const next = update(readWatchlistLists(localStorage));
      writeWatchlistLists(localStorage, next);
      lists = next;
    };
    // Serialise read/modify/write among other tabs of the same workspace where Web Locks is available.
    if (navigator.locks) await navigator.locks.request(watchlistListsKey, save);
    else save();
  }

  export function clear() {
    selection = null;
    trigger.focus();
  }

  async function openMenu() {
    menuError = '';
    query = '';
    loadLists();
    const { left, bottom } = trigger.getBoundingClientRect();
    menu.style.setProperty('--filter-left', Math.max(8, Math.min(left, window.innerWidth - 248)) + 'px');
    menu.style.setProperty('--filter-top', Math.max(8, Math.min(bottom + 6, window.innerHeight - 164)) + 'px');
    await tick();
    menu.showModal();
    menuOpen = true;
    // Measured without the search row itself, so it never keeps itself on screen.
    searchable = menu.scrollHeight - (searchRow?.offsetHeight ?? 0) > menu.clientHeight + 1;
    await tick();
    // A touch screen would pop its keyboard over the list, so only a pointer device starts typing at once.
    if (searchable && matchMedia('(pointer: fine)').matches) searchInput?.focus();
    else menu.querySelector<HTMLButtonElement>('.choice[aria-pressed="true"]')?.focus();
  }

  function searchKeys(event: KeyboardEvent) {
    if (event.key === 'Escape' && query) {
      // First Escape clears the search; the next closes the menu as before.
      event.preventDefault();
      event.stopPropagation();
      query = '';
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const first = [...shownSectors, ...shownGroups][0];
      if (searching && first) choose(first);
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      menu.querySelector<HTMLButtonElement>('.menu-content .choice')?.focus();
    }
  }

  function choose(filter: WatchlistFilter | null) {
    selection = filter && { kind: filter.kind, name: filter.name };
    menu.close();
  }

  async function openEditor(symbol: string | null) {
    loadLists();
    editSymbol = symbol;
    before = symbol ? lists.filter(list => list.symbols.includes(symbol)).map(list => list.name) : [];
    checked = [...before];
    editorError = '';
    newName = '';
    showName = symbol === null || !lists.length;
    if (menu.open) menu.close();
    await tick();
    editor.showModal();
    if (showName) nameInput?.focus();
  }

  export function editLists(symbol: string) {
    return openEditor(symbol);
  }

  async function closeEditor() {
    const symbol = editSymbol;
    await tick();
    if (symbol) restoreRowFocus(symbol);
    else trigger.focus();
  }

  async function showNewName() {
    showName = true;
    await tick();
    nameInput?.focus();
  }

  function errorMessage(error: unknown) {
    return error instanceof DOMException ? 'Lists could not be saved in this browser.'
      : error instanceof Error ? error.message : 'Lists could not be saved.';
  }

  async function submit() {
    if (saving) return;
    const name = newName.trim();
    const symbol = editSymbol;
    const additions = name ? [...checked, name] : checked;
    if (!symbol && !name) { editorError = 'Enter a list name.'; return; }
    if (symbol && !entries.some(entry => entry.symbol === symbol)) {
      editorError = `${symbol} is no longer on the Watchlist.`;
      return;
    }
    saving = true;
    editorError = '';
    try {
      await saveLists(latest => {
        const next = name ? addWatchlistList(latest, name) : latest;
        return symbol ? changeWatchlistMembership(next, symbol, before, additions) : next;
      });
      if (!symbol) selection = { kind: 'list', name };
      editor.close();
    } catch (error) { editorError = errorMessage(error); }
    finally { saving = false; }
  }

  async function deleteList(name: string) {
    if (saving || !confirm(`Delete “${name}”? Its stocks will stay on your Watchlist.`)) return;
    saving = true;
    menuError = '';
    try {
      await saveLists(latest => latest.filter(list => list.name !== name));
      if (selection?.kind === 'list' && selection.name === name) selection = null;
      await tick();
      menu.querySelector<HTMLButtonElement>('.choice[aria-pressed="true"]')?.focus();
    } catch (error) { menuError = errorMessage(error); }
    finally { saving = false; }
  }
  let sectors = $derived(watchlistSectors(entries));
  let shownSectors = $derived(matchingFilters(sectors, query));
  let watched = $derived(new Set(entries.map(entry => entry.symbol)));
  let customGroups = $derived(lists.map(({ name, symbols }): WatchlistFilter => ({
    kind: 'list', name, symbols: new Set(symbols.filter(symbol => watched.has(symbol)))
  })));
  let shownGroups = $derived(matchingFilters(customGroups, query));
  let searching = $derived(query.trim() !== '');
  // Writes the bound prop, so it stays an effect rather than a derived value.
  $effect.pre(() => {
    activeFilter = selection
      ? [...sectors, ...customGroups].find(group => group.kind === selection?.kind && group.name === selection.name)
        ?? (selection.kind === 'sector' ? { ...selection, symbols: new Set<string>() } : null)
      : null;
  });
</script>

<button class="trigger" class:active={activeFilter !== null} type="button" bind:this={trigger}
  aria-haspopup="dialog" aria-expanded={menuOpen} aria-label="Filter watched stocks" onclick={openMenu}>
  <Filter size={13} aria-hidden="true"/> Filter
</button>

<dialog class="filter-menu" bind:this={menu} aria-label="Filter watched stocks"
  onclose={() => menuOpen = false} onclick={event => { if (event.target === menu) menu.close(); }}>
  {#if searchable}
    <div class="search" bind:this={searchRow}>
      <Search size={13} aria-hidden="true"/>
      <input type="search" bind:this={searchInput} bind:value={query} onkeydown={searchKeys}
        placeholder="Find sector or list…" aria-label="Find a sector or list" autocomplete="off" spellcheck="false"/>
      {#if query}
        <button type="button" class="clear" aria-label="Clear search"
          onclick={() => { query = ''; searchInput?.focus(); }}><X size={13} aria-hidden="true"/></button>
      {/if}
    </div>
  {/if}
  <div class="menu-content">
    <button type="button" class="choice" aria-pressed={!activeFilter} onclick={() => choose(null)}>
      <span>All stocks</span><small>{entries.length}</small>
      {#if !activeFilter}<Check size={14} aria-hidden="true"/>{/if}
    </button>
    {#if searching && !shownSectors.length && !shownGroups.length}
      <p class="hint" role="status">No sector or list matches “{query.trim()}”.</p>
    {/if}
    {#if !searching || shownSectors.length}<h3>Sectors</h3>{/if}
    {#each shownSectors as sector (sector.name)}
      <button type="button" class="choice" aria-pressed={activeFilter?.kind === 'sector' && activeFilter.name === sector.name}
        onclick={() => choose(sector)}>
        <span>{sector.name}</span><small>{sector.symbols.size}</small>
        {#if activeFilter?.kind === 'sector' && activeFilter.name === sector.name}<Check size={14} aria-hidden="true"/>{/if}
      </button>
    {:else}
      {#if !searching}<p class="hint">{loadingSectors ? 'Loading sectors…' : 'No sector details available.'}</p>{/if}
    {/each}
    {#if !searching || shownGroups.length}<h3>Your lists</h3>{/if}
    {#each shownGroups as group (group.name)}
      <div class="list-choice">
        <button type="button" class="choice" aria-pressed={activeFilter?.kind === 'list' && activeFilter.name === group.name}
          onclick={() => choose(group)}>
          <span>{group.name}</span><small>{group.symbols.size}</small>
          {#if activeFilter?.kind === 'list' && activeFilter.name === group.name}<Check size={14} aria-hidden="true"/>{/if}
        </button>
        <button type="button" class="delete" disabled={saving} aria-label={`Delete list ${group.name}`}
          onclick={() => deleteList(group.name)}><Trash2 size={13} aria-hidden="true"/></button>
      </div>
    {/each}
    <button type="button" class="new-list" onclick={() => openEditor(null)}><Plus size={14} aria-hidden="true"/> New list</button>
    {#if menuError}<p class="error" role="alert">{menuError}</p>{/if}
  </div>
</dialog>

<dialog class="list-editor" bind:this={editor} aria-label={editSymbol ? `Lists for ${editSymbol}` : 'New list'}
  onclose={closeEditor} oncancel={event => { if (saving) event.preventDefault(); }}>
  <form onsubmit={(event) => { event.preventDefault(); void submit(); }}>
    <header><strong>{editSymbol ? `Lists for ${editSymbol}` : 'New list'}</strong>
      <button type="button" class="delete" aria-label="Close list editor" disabled={saving} onclick={() => editor.close()}>
        <X size={15} aria-hidden="true"/>
      </button>
    </header>
    {#if editSymbol}
      <div class="list-checks">
        {#each lists as list (list.name)}
          <label class="check"><input type="checkbox" value={list.name} bind:group={checked} disabled={saving}/> {list.name}</label>
        {/each}
      </div>
      {#if !showName}
        <button type="button" class="new-list" onclick={showNewName}><Plus size={14} aria-hidden="true"/> New list</button>
      {/if}
    {/if}
    {#if showName}
      <label class="name">List name
        <input type="text" bind:this={nameInput} bind:value={newName} maxlength={listNameLimit} required={!editSymbol}
          placeholder="e.g. Keep an eye on" disabled={saving}/>
      </label>
    {/if}
    {#if editorError}<p class="error" role="alert">{editorError}</p>{/if}
    <footer>
      <button type="button" class="cancel" disabled={saving} onclick={() => editor.close()}>Cancel</button>
      <button type="submit" class="done" disabled={saving}>{editSymbol ? 'Done' : 'Create list'}</button>
    </footer>
  </form>
</dialog>

<style>
  button { font:inherit; cursor:pointer; color:inherit; }
  button:focus-visible, input:focus-visible { outline:2px solid var(--primary); outline-offset:1px; }
  .trigger { display:flex; align-items:center; gap:.23rem; min-height:25px; padding:0 .3rem;
    border:1px solid var(--border-md); border-radius:5px; background:var(--surface-2); color:var(--text-2);
    font-size:.69rem; white-space:nowrap; }
  .trigger:hover, .trigger.active, .trigger[aria-expanded="true"] { color:var(--primary); background:var(--primary-dim); }
  dialog { box-sizing:border-box; color:var(--text); background:var(--surface-1, var(--surface));
    border:1px solid var(--border-high); border-radius:8px; box-shadow:0 8px 28px #0006;
    font-size:.82rem; }
  .filter-menu { position:fixed; inset:var(--filter-top) auto auto var(--filter-left); margin:0; padding:0;
    width:min(240px, calc(100dvw - 16px)); max-height:min(28rem, calc(100dvh - var(--filter-top) - 8px));
    overscroll-behavior:contain; }
  .filter-menu::backdrop { background:transparent; }
  .menu-content { padding:.45rem; }
  .search { position:sticky; top:0; z-index:1; display:flex; align-items:center; gap:.3rem; margin:0;
    padding:.45rem .55rem; border-bottom:1px solid var(--border); background:var(--surface-1, var(--surface));
    color:var(--text-2); }
  .search input { flex:1; min-width:0; padding:.3rem .35rem; border:1px solid var(--border-md); border-radius:4px;
    background:var(--surface-2); color:var(--text); font:inherit; font-size:.78rem; }
  .search input::-webkit-search-cancel-button { display:none; }
  .search .clear { display:flex; align-items:center; justify-content:center; min-width:24px; min-height:24px;
    padding:0; border:0; border-radius:4px; background:transparent; color:var(--text-2); }
  .search .clear:hover { background:var(--surface-2); }
  h3 { padding:.55rem .4rem .25rem; margin:.3rem 0 0; border-top:1px solid var(--border);
    color:var(--text-2); font-size:.72rem; }
  .choice, .new-list, .delete { display:flex; align-items:center; gap:.35rem; min-height:29px; padding:.25rem .4rem;
    border:0; border-radius:4px; background:transparent; text-align:left; }
  .choice { width:100%; min-width:0; }
  .choice span { flex:1; overflow-wrap:anywhere; }
  .choice small, .hint { color:var(--text-2); }
  .choice:hover, .delete:hover, .new-list:hover { background:var(--surface-2); }
  .choice[aria-pressed="true"] { color:var(--primary); background:var(--primary-dim); }
  .list-choice { display:flex; gap:.1rem; }
  .delete { min-width:27px; justify-content:center; color:var(--text-2); }
  .new-list { color:var(--primary); }
  .hint, .error { margin:.3rem .4rem; overflow-wrap:anywhere; }
  .error { color:var(--danger); }
  .list-editor { width:min(330px,calc(100dvw - 1rem)); max-height:85dvh; padding:.85rem; }
  .list-editor::backdrop { background:#0007; }
  form { display:grid; gap:.65rem; }
  header, footer { display:flex; align-items:center; justify-content:space-between; gap:.5rem; }
  footer { justify-content:flex-end; }
  .list-checks { display:grid; gap:.15rem; }
  .list-checks:empty { display:none; }
  .check, .name { padding:.2rem; overflow-wrap:anywhere; }
  .check { display:flex; align-items:flex-start; gap:.35rem; }
  .name { display:grid; gap:.25rem; }
  .name input { min-width:0; padding:.4rem; border:1px solid var(--border-md); border-radius:4px;
    background:var(--surface-2); color:var(--text); font:inherit; }
  .cancel, .done { min-height:30px; padding:.25rem .6rem; border-radius:4px; border:1px solid var(--border-md);
    background:var(--surface-2); }
  .done { color:var(--primary); background:var(--primary-dim); border-color:var(--primary); }
</style>
