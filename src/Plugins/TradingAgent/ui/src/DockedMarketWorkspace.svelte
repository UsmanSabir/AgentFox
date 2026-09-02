<script lang="ts">
  import { createEventDispatcher, onDestroy, onMount } from 'svelte';
  import {
    createDockview,
    themeDark,
    themeLight,
    type DockviewApi,
    type IContentRenderer,
    type ITabRenderer,
    type SerializedDockview,
    type TabPartInitParameters
  } from 'dockview';
  import 'dockview/dist/styles/dockview.css';
  import { Keyboard, LayoutPanelLeft, RotateCcw } from 'lucide-svelte';
  import type { ArmOrderDialogContext, CandleArchiveStatus } from './api';
  import ChartPane from './ChartPane.svelte';
  import type { SymbolExtension } from './symbolExtensions';
  import WatchlistPanel from './WatchlistPanel.svelte';

  export let selectedSymbol: string | null = null;
  export let selectedCompany: string | null = null;
  export let refreshTick = 0;
  export let historyRefreshTick = 0;
  export let archive: CandleArchiveStatus | null = null;
  export let marketOpen = false;
  export let symbolExtension: SymbolExtension | null = null;

  const dispatch = createEventDispatcher<{ arm: ArmOrderDialogContext }>();
  const STORAGE_KEY = 'trading.market-workstation.layout.v1';
  const OLD_SPLITTER_KEY = 'trading.market-workspace.split.v1';
  const RETENTION_MS = 180 * 24 * 60 * 60 * 1000;
  const SAVE_DELAY_MS = 250;

  type PanelId = 'watchlist' | 'chart' | 'plan';
  type SavedLayout = {
    version: 1;
    savedAt: number;
    panelIds: PanelId[];
    layout: SerializedDockview;
  };

  let dockRoot: HTMLDivElement;
  let depot: HTMLDivElement;
  let watchlistHost: HTMLDivElement;
  let chartHost: HTMLDivElement;
  let planHost: HTMLDivElement;
  let watchlistPanel: WatchlistPanel | null = null;
  let api: DockviewApi | null = null;
  let defaultLayout: SerializedDockview | null = null;
  let saveTimer: ReturnType<typeof setTimeout> | null = null;
  let suppressPersistence = false;
  let layoutDirty = false;
  let notice = '';

  $: panelIds = (symbolExtension?.plan
    ? ['watchlist', 'chart', 'plan']
    : ['watchlist', 'chart']) as PanelId[];

  function currentTheme() {
    return document.documentElement.dataset.theme === 'light' ? themeLight : themeDark;
  }

  function cloneLayout(layout: SerializedDockview): SerializedDockview {
    return JSON.parse(JSON.stringify(layout)) as SerializedDockview;
  }

  function samePanelSet(saved: PanelId[]) {
    return saved.length === panelIds.length
      && [...saved].sort().every((id, index) => id === [...panelIds].sort()[index]);
  }

  function hostFor(id: string): HTMLDivElement | null {
    if (id === 'watchlist') return watchlistHost;
    if (id === 'chart') return chartHost;
    if (id === 'plan') return planHost;
    return null;
  }

  function createContent(id: string): IContentRenderer {
    const element = hostFor(id);
    if (!element) throw new Error(`Unknown market workspace panel: ${id}`);

    return {
      element,
      init() {},
      dispose() {
        // Dockview owns placement, not the Svelte component. Returning the live host to this hidden
        // depot lets fromJSON/reset rebuild the layout without unmounting or duplicating API readers.
        if (depot?.isConnected) depot.appendChild(element);
      }
    };
  }

  function createTab(): ITabRenderer {
    const element = document.createElement('div');
    element.className = 'agentfox-dock-tab';
    return {
      element,
      init(params: TabPartInitParameters) {
        element.textContent = params.title;
        element.title = `${params.title} panel — drag to move or dock`;
      }
    };
  }

  function addDefaultPanels(dock: DockviewApi) {
    const watchlistWidth = Math.max(290, Math.min(380, Math.round(dockRoot.clientWidth * 0.3)));
    const watchlist = dock.addPanel({
      id: 'watchlist',
      component: 'watchlist',
      tabComponent: 'market-tab',
      title: 'Watchlist',
      renderer: 'always',
      initialWidth: watchlistWidth,
      minimumWidth: 260,
      minimumHeight: 260
    });
    dock.addPanel({
      id: 'chart',
      component: 'chart',
      tabComponent: 'market-tab',
      title: 'Price chart',
      renderer: 'always',
      minimumWidth: 420,
      minimumHeight: 320,
      position: { referencePanel: 'watchlist', direction: 'right' }
    });
    watchlist.api.setSize({ width: watchlistWidth });

    if (symbolExtension?.plan) {
      dock.addPanel({
        id: 'plan',
        component: 'plan',
        tabComponent: 'market-tab',
        title: 'Trade plan',
        renderer: 'always',
        inactive: true,
        minimumWidth: 360,
        minimumHeight: 260,
        position: { referencePanel: 'chart', direction: 'within' }
      });
    }
  }

  function removeSavedLayout() {
    try {
      localStorage.removeItem(STORAGE_KEY);
      localStorage.removeItem(OLD_SPLITTER_KEY);
    } catch {
      // Browser storage is an enhancement. Docking must still work when it is blocked.
    }
  }

  function readSavedLayout(): SerializedDockview | null {
    try {
      const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null') as SavedLayout | null;
      if (!saved) return null;
      const age = Date.now() - saved.savedAt;
      const valid = saved.version === 1
        && Number.isFinite(age)
        && age >= 0
        && age <= RETENTION_MS
        && Array.isArray(saved.panelIds)
        && samePanelSet(saved.panelIds)
        && saved.layout != null;
      if (valid) return saved.layout;
      removeSavedLayout();
    } catch {
      removeSavedLayout();
    }
    return null;
  }

  function persistLayout() {
    if (!api || suppressPersistence) return;
    try {
      const saved: SavedLayout = {
        version: 1,
        savedAt: Date.now(),
        panelIds,
        layout: api.toJSON()
      };
      localStorage.setItem(STORAGE_KEY, JSON.stringify(saved));
      layoutDirty = false;
    } catch {
      // A quota or privacy-mode failure must not interrupt the trading workspace.
    }
  }

  function schedulePersist() {
    if (suppressPersistence) return;
    layoutDirty = true;
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(persistLayout, SAVE_DELAY_MS);
  }

  function focusPanel(id: PanelId) {
    const panel = api?.getPanel(id);
    if (!panel) return;
    panel.api.setActive();
    requestAnimationFrame(() => hostFor(id)?.focus());
  }

  function resetView() {
    if (!api || !defaultLayout) return;
    suppressPersistence = true;
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = null;
    api.fromJSON(cloneLayout(defaultLayout));
    removeSavedLayout();
    layoutDirty = false;
    notice = 'Default market view restored.';
    queueMicrotask(() => suppressPersistence = false);
  }

  function handleShortcut(event: KeyboardEvent) {
    const target = event.target as HTMLElement | null;
    if (target?.matches('input, textarea, select, [contenteditable="true"]')) return;
    if (!(event.ctrlKey || event.metaKey) || !event.shiftKey || event.altKey) return;

    // With Shift held, `event.key` is punctuation on most layouts (`!`, `@`, `#`, `)`). The
    // physical code remains Digit1/Digit2/Digit3/Digit0 and is therefore the reliable shortcut.
    const shortcut = event.code;
    if (shortcut === 'Digit0' || shortcut === 'Numpad0') resetView();
    else if (shortcut === 'Digit1' || shortcut === 'Numpad1') focusPanel('watchlist');
    else if (shortcut === 'Digit2' || shortcut === 'Numpad2') focusPanel('chart');
    else if ((shortcut === 'Digit3' || shortcut === 'Numpad3') && symbolExtension?.plan) focusPanel('plan');
    else return;
    event.preventDefault();
  }

  function flushLayout() {
    if (layoutDirty) persistLayout();
  }

  function handleSectionNavigate(event: Event) {
    if ((event as CustomEvent<string>).detail === 'trading-stock-plan' && symbolExtension?.plan) {
      focusPanel('plan');
    }
  }

  export async function refresh() {
    await watchlistPanel?.refresh();
  }

  onMount(() => {
    const dock = createDockview(dockRoot, {
      createComponent: ({ id }) => createContent(id),
      createTabComponent: () => createTab(),
      className: 'agentfox-dockview',
      theme: currentTheme(),
      keyboardNavigation: true,
      floatingGroupDragHandle: 'tabbar',
      scrollbars: 'native',
      noPanelsOverlay: 'emptyGroup'
    });
    api = dock;

    suppressPersistence = true;
    addDefaultPanels(dock);
    defaultLayout = cloneLayout(dock.toJSON());
    const saved = readSavedLayout();
    if (saved) {
      try {
        dock.fromJSON(saved);
      } catch {
        removeSavedLayout();
        dock.fromJSON(cloneLayout(defaultLayout));
      }
    } else {
      try { localStorage.removeItem(OLD_SPLITTER_KEY); } catch { /* optional storage */ }
    }
    suppressPersistence = false;
    layoutDirty = false;

    const layoutSubscription = dock.onDidLayoutChange(schedulePersist);
    const handleThemeChange = () => dock.updateOptions({ theme: currentTheme() });
    window.addEventListener('agentfox:themechange', handleThemeChange);
    window.addEventListener('agentfox:sectionnavigate', handleSectionNavigate);
    window.addEventListener('keydown', handleShortcut);
    window.addEventListener('pagehide', flushLayout);

    return () => {
      layoutSubscription.dispose();
      window.removeEventListener('agentfox:themechange', handleThemeChange);
      window.removeEventListener('agentfox:sectionnavigate', handleSectionNavigate);
      window.removeEventListener('keydown', handleShortcut);
      window.removeEventListener('pagehide', flushLayout);
    };
  });

  onDestroy(() => {
    if (saveTimer) clearTimeout(saveTimer);
    flushLayout();
    api?.dispose();
    api = null;
  });
</script>

<section class="market-workstation" aria-label="Dockable market workspace">
  <div class="workspace-toolbar">
    <div class="workspace-identity">
      <LayoutPanelLeft size={14} aria-hidden="true" />
      <span><b>Desktop layout</b><small>Drag tabs or dividers to arrange panels</small></span>
    </div>
    <div class="workspace-actions">
      <details class="shortcut-help">
        <summary title="Keyboard shortcuts" aria-label="Show market workspace keyboard shortcuts">
          <Keyboard size={14} aria-hidden="true" />
          <span>Shortcuts</span>
        </summary>
        <div class="shortcut-card">
          <b>Keyboard</b>
          <span><kbd>F6</kbd> next panel group</span>
          <span><kbd>Shift</kbd> + <kbd>F6</kbd> previous group</span>
          <span><kbd>Ctrl</kbd> + <kbd>]</kbd> next tab</span>
          <span><kbd>Ctrl</kbd> + <kbd>[</kbd> previous tab</span>
          <span><kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>1</kbd> watchlist</span>
          <span><kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>2</kbd> chart</span>
          {#if symbolExtension?.plan}
            <span><kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>3</kbd> trade plan</span>
          {/if}
          <span><kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>0</kbd> reset view</span>
        </div>
      </details>
      <button class="reset-view" type="button" on:click={resetView} title="Restore the default panel arrangement">
        <RotateCcw size={13} aria-hidden="true" /> Reset view
      </button>
    </div>
  </div>

  <div class="dock-shell" bind:this={dockRoot}></div>
  <p class="sr-status" aria-live="polite">{notice}</p>

  <!-- Live Svelte components start here, then Dockview reparents these hosts. Keeping ownership in
       this component preserves bindings and ensures exactly one watchlist/chart reader exists. -->
  <div class="component-depot" bind:this={depot} aria-hidden="true">
    <div class="panel-host" data-panel="watchlist" tabindex="-1" bind:this={watchlistHost}>
      <WatchlistPanel
        bind:this={watchlistPanel}
        bind:selected={selectedSymbol}
        bind:selectedCompany
        compact={false}
        allowCompact={false}
        {refreshTick}
        {marketOpen}
        rowStatus={symbolExtension?.rowStatus ?? null}
      />
    </div>
    <div class="panel-host chart-host" data-panel="chart" tabindex="-1" bind:this={chartHost}>
      <ChartPane
        symbol={selectedSymbol}
        companyName={selectedCompany}
        expanded={false}
        allowExpand={false}
        {refreshTick}
        {historyRefreshTick}
        {archive}
        {marketOpen}
        on:arm={(event) => dispatch('arm', event.detail)}
      />
    </div>
    {#if symbolExtension?.plan}
      <div id="trading-stock-plan" class="panel-host plan-host section-anchor" data-panel="plan" tabindex="-1" bind:this={planHost}>
        {#if selectedSymbol}
          <svelte:component
            this={symbolExtension.plan}
            symbol={selectedSymbol}
            companyName={selectedCompany}
          />
        {/if}
      </div>
    {/if}
  </div>
</section>

<style>
  .market-workstation { min-width: 0; margin-bottom: .75rem; }
  .workspace-toolbar {
    position: relative;
    z-index: 10;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: .75rem;
    min-height: 2.4rem;
    padding: .35rem .45rem .35rem .65rem;
    border: 1px solid var(--border-md);
    border-bottom: 0;
    border-radius: var(--radius) var(--radius) 0 0;
    background: var(--surface-2);
  }
  .workspace-identity, .workspace-actions, .workspace-identity span {
    display: flex;
    align-items: center;
  }
  .workspace-identity { gap: .45rem; color: var(--text-2); }
  .workspace-identity span { gap: .45rem; min-width: 0; }
  .workspace-identity b { color: var(--text); font-size: .72rem; }
  .workspace-identity small { color: var(--text-3); font-size: .64rem; }
  .workspace-actions { gap: .35rem; }
  .reset-view, .shortcut-help summary {
    display: inline-flex;
    align-items: center;
    gap: .35rem;
    min-height: 1.8rem;
    padding: .3rem .55rem;
    border: 1px solid var(--border-md);
    border-radius: 6px;
    background: var(--surface);
    color: var(--text-2);
    font: inherit;
    font-size: .66rem;
    font-weight: 650;
    cursor: pointer;
  }
  .reset-view:hover, .shortcut-help summary:hover { color: var(--text); border-color: var(--border-high); }
  .reset-view:focus-visible, .shortcut-help summary:focus-visible, .panel-host:focus-visible {
    outline: 2px solid var(--primary);
    outline-offset: 2px;
  }
  .shortcut-help { position: relative; }
  .shortcut-help summary { list-style: none; }
  .shortcut-help summary::-webkit-details-marker { display: none; }
  .shortcut-card {
    position: absolute;
    top: calc(100% + .4rem);
    right: 0;
    z-index: 20;
    display: grid;
    grid-template-columns: 1fr;
    gap: .45rem;
    width: max-content;
    min-width: 15.5rem;
    padding: .75rem;
    border: 1px solid var(--border-high);
    border-radius: 8px;
    background: var(--surface);
    box-shadow: 0 12px 36px rgba(0, 0, 0, .28);
    color: var(--text-2);
    font-size: .66rem;
  }
  .shortcut-card b { color: var(--text); font-size: .7rem; }
  .shortcut-card span { display: block; }
  kbd {
    display: inline-block;
    min-width: 1.25rem;
    padding: .08rem .26rem;
    border: 1px solid var(--border-md);
    border-bottom-color: var(--border-high);
    border-radius: 4px;
    background: var(--surface-2);
    color: var(--text);
    font: inherit;
    font-size: .61rem;
    text-align: center;
  }
  .dock-shell {
    width: 100%;
    height: clamp(620px, 76vh, 880px);
    min-width: 0;
    overflow: hidden;
    border: 1px solid var(--border-md);
    border-radius: 0 0 var(--radius) var(--radius);
    background: var(--surface);
    --dv-group-view-background-color: var(--surface);
    --dv-tabs-and-actions-container-background-color: var(--surface-2);
    --dv-activegroup-visiblepanel-tab-background-color: var(--surface);
    --dv-activegroup-hiddenpanel-tab-background-color: var(--surface-2);
    --dv-inactivegroup-visiblepanel-tab-background-color: var(--surface);
    --dv-inactivegroup-hiddenpanel-tab-background-color: var(--surface-2);
    --dv-activegroup-visiblepanel-tab-color: var(--text);
    --dv-activegroup-hiddenpanel-tab-color: var(--text-2);
    --dv-inactivegroup-visiblepanel-tab-color: var(--text-2);
    --dv-inactivegroup-hiddenpanel-tab-color: var(--text-3);
    --dv-tab-divider-color: var(--border);
    --dv-separator-border: var(--border-md);
    --dv-active-sash-color: var(--primary);
    --dv-sash-color: var(--border);
    --dv-tabs-and-actions-container-height: 34px;
    --dv-drag-over-background-color: var(--primary-dim);
    --dv-drag-over-border-color: var(--primary);
  }
  .component-depot { display: none; }
  .panel-host {
    width: 100%;
    height: 100%;
    min-width: 0;
    min-height: 0;
    overflow: auto;
    background: var(--surface);
  }
  .sr-status {
    position: absolute;
    width: 1px;
    height: 1px;
    padding: 0;
    margin: -1px;
    overflow: hidden;
    clip: rect(0, 0, 0, 0);
    white-space: nowrap;
    border: 0;
  }
  .dock-shell :global(.agentfox-dock-tab) {
    display: flex;
    align-items: center;
    height: 100%;
    padding: 0 .7rem;
    color: inherit;
    font-size: .68rem;
    font-weight: 680;
    letter-spacing: .01em;
    white-space: nowrap;
  }
  .dock-shell :global(.dv-tab.dv-active-tab) { box-shadow: inset 0 2px 0 var(--primary); }
  .dock-shell :global(.dv-sash:hover),
  .dock-shell :global(.dv-sash.dv-active) { background: var(--primary); }
  .dock-shell :global(.panel-host > section) {
    min-height: 100%;
    height: 100%;
    border: 0;
    border-radius: 0;
  }
  .dock-shell :global(.panel-host[data-panel='watchlist'] > .watchlist) {
    contain: size;
  }

  @media (prefers-reduced-motion: reduce) {
    .dock-shell { --dv-transition-duration: 0ms; }
  }
</style>
