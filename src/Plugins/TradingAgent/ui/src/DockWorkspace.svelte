<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { createDockview, themeDark, themeLight, type DockviewApi, type IContentRenderer } from 'dockview';
  import 'dockview/dist/styles/dockview.css';
  import { Command, PanelsTopLeft, RotateCcw, Maximize2, X } from 'lucide-svelte';
  import type { WorkspaceCommand, WorkspaceComposition, WorkspacePanel, WorkspaceRegion } from './workspaceComposition';
  import { readWorkspaceLayout, saveWorkspaceLayout } from './workspaceLayout';

  export let panels: WorkspacePanel[];
  export let title = 'Trading workstation';
  export let edition: string;
  export let storageKey: string;
  export let presets: { id: string; label: string; active: Partial<Record<WorkspaceRegion, string>> }[];
  export let onExit: () => void;

  let root: HTMLElement;
  let dockRoot: HTMLDivElement;
  let depot: HTMLDivElement;
  let toolbar: HTMLDivElement;
  let health: HTMLDivElement;
  let overlays: HTMLDivElement;
  let stack: HTMLDivElement;
  let palette: HTMLDialogElement;
  let search: HTMLInputElement;
  let api: DockviewApi | null = null;
  let desktop = true;
  let preset = presets[0].id;
  let commands: WorkspaceCommand[] = [];
  let activeId = 'chart';
  let notice = 'Workspace preview · existing trading controls retained';
  let storageWarning = '';
  let query = '';
  let panelsOnly = false;
  let resultIndex = 0;
  let previousFocus: HTMLElement | null = null;
  let suppressSave = false;
  let saveTimer: ReturnType<typeof setTimeout> | undefined;
  let dirty = false;
  let disposed = false;
  const nodes = new Map<string, HTMLElement>();
  const containers = new Map<string, HTMLDivElement>();
  const special = new Set(['core-toolbar', 'edition-health', 'core-dialogs']);

  function container(id: string) {
    let element = containers.get(id);
    if (!element) {
      const spec = panels.find(p => p.id === id);
      if (!spec) throw new Error('Unregistered workspace panel: ' + id);
      element = document.createElement('div');
      element.className = 'workstation-panel';
      element.dataset.workspacePanel = id;
      element.tabIndex = -1;
      element.setAttribute('aria-label', spec.title);
      const heading = document.createElement('h2');
      heading.className = 'mobile-panel-title';
      heading.textContent = spec.title;
      element.appendChild(heading);
      const placeholder = document.createElement('p');
      placeholder.className = 'panel-placeholder';
      placeholder.textContent = 'Waiting for ' + spec.title.toLowerCase() + ' data. Use Refresh or check System status if it remains unavailable.';
      element.appendChild(placeholder);
      containers.set(id, element);
    }
    return element;
  }

  function destination(id: string) {
    if (id === 'core-toolbar') return toolbar;
    if (id === 'edition-health') return health;
    if (id === 'core-dialogs') return overlays;
    return container(id);
  }

  function place(id: string) {
    const node = nodes.get(id);
    const target = destination(id);
    if (!node || !target) return;
    target.appendChild(node);
    const placeholder = target.querySelector<HTMLElement>(':scope > .panel-placeholder');
    if (placeholder) placeholder.hidden = true;
  }

  const workspace: WorkspaceComposition = {
    attachPanel(id, node) {
      if (!special.has(id) && !panels.some(p => p.id === id)) throw new Error('Unknown panel: ' + id);
      if (nodes.has(id)) throw new Error('Duplicate workspace owner: ' + id);
      const marker = document.createComment('workspace-source:' + id);
      node.before(marker);
      nodes.set(id, node);
      place(id);
      return () => {
        nodes.delete(id);
        if (marker.parentNode) marker.replaceWith(node);
        const placeholder = containers.get(id)?.querySelector<HTMLElement>(':scope > .panel-placeholder');
        if (placeholder) placeholder.hidden = false;
      };
    },
    registerCommand(command) {
      if (commands.some(c => c.id === command.id)) throw new Error('Duplicate workspace command: ' + command.id);
      commands = [...commands, command];
      return () => commands = commands.filter(c => c !== command);
    },
    focusPanel
  };

  function focusPanel(id: string) {
    if (!panels.some(p => p.id === id)) return;
    if (api) {
      if (api.hasMaximizedGroup()) api.exitMaximizedGroup();
      let panel = api.getPanel(id);
      if (!panel) {
        const region = panels.find(p => p.id === id)?.region;
        const peer = panels.find(p => p.region === region && api?.getPanel(p.id));
        panel = addPanel(id, peer?.id);
      }
      panel?.api.setActive();
    }
    activeId = id;
    requestAnimationFrame(() => {
      if (disposed) return;
      container(id).focus({ preventScroll: desktop });
      if (!desktop) container(id).scrollIntoView({ block: 'start' });
    });
  }

  function addPanel(id: string, reference?: string, direction: 'left' | 'right' | 'above' | 'below' | 'within' = 'within') {
    const spec = panels.find(p => p.id === id)!;
    return api?.addPanel({
      id, component: id, title: spec.title, renderer: 'always',
      minimumWidth: 180, minimumHeight: 100,
      position: reference ? { referencePanel: reference, direction } : direction !== 'within' ? { direction } : undefined
    });
  }

  function buildDefault(next: string) {
    if (!api) return;
    api.exitMaximizedGroup();
    api.clear();
    const primary = panels.find(p => p.region === 'center')!;
    addPanel(primary.id);
    const regions = ['left', 'right', 'bottom'] as const;
    const leaders: Partial<Record<WorkspaceRegion, string>> = { center: primary.id };
    for (const region of regions) {
      const first = panels.find(p => p.region === region);
      if (!first) continue;
      leaders[region] = first.id;
      if (region === 'right' && dockRoot.clientWidth < 1200) addPanel(first.id, primary.id);
      else addPanel(first.id, region === 'bottom' ? undefined : primary.id, region === 'bottom' ? 'below' : region);
    }
    for (const spec of panels) {
      if (!api.getPanel(spec.id)) addPanel(spec.id, leaders[spec.region] ?? primary.id);
    }
    api.getPanel(leaders.left ?? '')?.api.setSize({ width: Math.max(250, Math.min(280, dockRoot.clientWidth * .2)) });
    if (dockRoot.clientWidth >= 1200) api.getPanel(leaders.right ?? '')?.api.setSize({ width: Math.max(310, Math.min(360, dockRoot.clientWidth * .25)) });
    api.getPanel(leaders.bottom ?? '')?.api.setSize({ height: Math.max(130, dockRoot.clientHeight * .24) });
    const selected = presets.find(p => p.id === next) ?? presets[0];
    for (const id of Object.values(selected.active)) api.getPanel(id)?.api.setActive();
    const activeCenter = selected.active.center ?? primary.id;
    api.getPanel(activeCenter)?.api.setActive();
    preset = selected.id;
    activeId = activeCenter;
  }

  function removeSaved() {
    try { localStorage.removeItem(storageKey); } catch { /* Optional browser storage. */ }
  }
  function persist() {
    if (!api || suppressSave || !dirty) return;
    try {
      localStorage.setItem(storageKey, JSON.stringify(saveWorkspaceLayout(edition, preset, api.toJSON())));
      dirty = false;
      storageWarning = '';
    } catch { storageWarning = 'Layout could not be saved in this browser; trading is unaffected.'; }
  }
  function scheduleSave() {
    if (suppressSave) return;
    dirty = true;
    clearTimeout(saveTimer);
    saveTimer = setTimeout(persist, 250);
  }
  function resetView() {
    clearTimeout(saveTimer);
    suppressSave = true;
    if (api) buildDefault(presets[0].id);
    else preset = presets[0].id;
    removeSaved();
    storageWarning = '';
    dirty = false;
    suppressSave = false;
    notice = 'Default view restored. Trading state and open forms are unchanged.';
  }
  function selectPreset(id: string) {
    if (!api) { preset = id; return; }
    clearTimeout(saveTimer);
    suppressSave = true;
    buildDefault(id);
    suppressSave = false;
    scheduleSave();
    notice = 'Layout changed. Every panel remains available in Panels.';
  }
  function maximize() {
    if (!api?.activePanel) return;
    if (api.hasMaximizedGroup()) api.exitMaximizedGroup();
    else api.activePanel.api.maximize();
  }
  function resize(width: number, height: number) {
    const panel = api?.getPanel(activeId);
    if (!panel) return;
    panel.api.setSize({ width: Math.max(180, panel.api.width + width), height: Math.max(100, panel.api.height + height) });
  }
  function move(position: 'left' | 'right' | 'top' | 'bottom') {
    const panel = api?.getPanel(activeId);
    if (!api || !panel) return;
    const direction = position === 'top' ? 'above' : position === 'bottom' ? 'below' : position;
    const group = api.addGroup({ direction });
    panel.api.moveTo({ group });
    focusPanel(panel.id);
  }
  function hideActive() {
    api?.getPanel(activeId)?.api.close();
    if (api?.activePanel) focusPanel(api.activePanel.id);
    else root.focus();
  }
  function cycleGroup(delta: number) {
    if (!api?.groups.length) return;
    const groups = api.groups;
    const index = groups.findIndex(g => g === api?.activeGroup);
    const next = groups[(index + delta + groups.length) % groups.length].activePanel;
    if (next) focusPanel(next.id);
  }
  function cycleTab(delta: number) {
    const group = api?.activeGroup;
    if (!group?.panels.length) return;
    const index = group.panels.findIndex(p => p.id === group.activePanel?.id);
    focusPanel(group.panels[(index + delta + group.panels.length) % group.panels.length].id);
  }

  $: panelCommands = panels.map(p => ({ id: 'panel.' + p.id, label: 'Show ' + p.title, run: () => focusPanel(p.id) }));
  $: layoutCommands = [
    { id:'layout.reset', label:'Reset view to default', run:resetView },
    { id:'layout.maximize', label:'Maximize / restore active group', run:maximize },
    { id:'layout.hide', label:'Hide active panel (restore from Panels)', run:hideActive },
    { id:'layout.wider', label:'Make active panel wider', run:() => resize(80, 0) },
    { id:'layout.narrower', label:'Make active panel narrower', run:() => resize(-80, 0) },
    { id:'layout.taller', label:'Make active panel taller', run:() => resize(0, 80) },
    { id:'layout.shorter', label:'Make active panel shorter', run:() => resize(0, -80) },
    ...(['left', 'right', 'top', 'bottom'] as const).map(position => ({
      id:'layout.move.' + position, label:'Move active panel to ' + position + ' edge', run:() => move(position)
    })),
    ...panels.map(p => ({
      id:'layout.tab.' + p.id, label:'Tab active panel with ' + p.title,
      run:() => {
        const current = api?.getPanel(activeId);
        const target = api?.getPanel(p.id);
        if (current && target && current !== target) current.api.moveTo({ group:target.group });
      }
    }))
  ];
  $: allCommands = [...panelCommands, ...commands, ...layoutCommands];
  $: results = (panelsOnly ? panelCommands : allCommands).filter(c => c.label.toLowerCase().includes(query.toLowerCase()));
  $: if (resultIndex >= results.length) resultIndex = Math.max(0, results.length - 1);

  async function openPalette(onlyPanels = false) {
    previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    panelsOnly = onlyPanels;
    query = '';
    resultIndex = 0;
    palette.showModal();
    await tick();
    search.focus();
  }
  function closePalette() {
    palette.close();
    if (previousFocus?.isConnected) previousFocus.focus();
  }
  async function execute(command: WorkspaceCommand | undefined) {
    if (!command) return;
    const reason = command.disabled?.();
    if (reason) { notice = reason; return; }
    closePalette();
    await tick();
    try { await command.run(); }
    catch (e) { notice = e instanceof Error ? e.message : String(e); }
  }
  function paletteKey(event: KeyboardEvent) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      resultIndex = (resultIndex + (event.key === 'ArrowDown' ? 1 : -1) + Math.max(1, results.length)) % Math.max(1, results.length);
      document.getElementById('workspace-result-' + resultIndex)?.scrollIntoView({ block:'nearest' });
    } else if (event.key === 'Enter') {
      event.preventDefault();
      if (!event.repeat) void execute(results[resultIndex]);
    }
  }
  function shortcut(event: KeyboardEvent) {
    if (event.defaultPrevented || event.isComposing || event.repeat) return;
    const target = event.target instanceof HTMLElement ? event.target : null;
    if (palette?.open || target?.closest('dialog, [role="dialog"], [role="alertdialog"]')) return;
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.code === 'KeyK') {
      event.preventDefault(); event.stopPropagation(); void openPalette(); return;
    }
    if (target?.closest('input, textarea, select, [contenteditable="true"]')) return;
    const direct: Record<string,string> = { Digit1:'watchlist', Digit2:'chart', Digit3:'plan', Digit4:'ticket' };
    if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && direct[event.code]) focusPanel(direct[event.code]);
    else if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && event.code === 'Digit0') resetView();
    else if (event.key === 'F6' && !event.ctrlKey && !event.metaKey && !event.altKey) cycleGroup(event.shiftKey ? -1 : 1);
    else if (event.ctrlKey && !event.shiftKey && event.code === 'BracketRight') cycleTab(1);
    else if (event.ctrlKey && !event.shiftKey && event.code === 'BracketLeft') cycleTab(-1);
    else return;
    event.preventDefault(); event.stopPropagation();
  }

  onMount(() => {
    const media = window.matchMedia('(min-width:901px)');
    let subscriptions: { dispose(): void }[] = [];
    function disconnect() {
      clearTimeout(saveTimer);
      persist();
      for (const s of subscriptions) s.dispose();
      subscriptions = [];
      api?.dispose();
      api = null;
    }
    function connect() {
      desktop = media.matches;
      // matchMedia fires before Svelte flushes the visibility classes on a breakpoint transition.
      // Set these immediately so Dockview measures the real host, not a display:none zero rectangle.
      dockRoot.classList.toggle('hidden', !desktop);
      stack.classList.toggle('hidden', desktop);
      if (!desktop) {
        disconnect();
        for (const p of panels) { stack.appendChild(container(p.id)); place(p.id); }
        return;
      }
      if (api) return;
      api = createDockview(dockRoot, {
        createComponent: ({ id }): IContentRenderer => ({
          element: container(id), init() { place(id); },
          dispose() { if (depot?.isConnected) depot.appendChild(container(id)); }
        }),
        theme: document.documentElement.dataset.theme === 'light' ? themeLight : themeDark,
        keyboardNavigation: false, disableFloatingGroups: true,
        scrollbars: 'native', noPanelsOverlay:'emptyGroup'
      });
      // Initialize geometry before setting pane proportions; the library's first observer callback
      // otherwise redistributes a zero-size grid into equal columns on initial mount.
      api.layout(dockRoot.clientWidth, dockRoot.clientHeight);
      suppressSave = true;
      buildDefault(preset);
      let raw: string | null = null;
      try { raw = localStorage.getItem(storageKey); } catch { /* Storage may be disabled. */ }
      const saved = readWorkspaceLayout(raw, edition, panels.map(p => p.id), presets.map(p => p.id));
      if (saved) {
        try { api.fromJSON(saved.layout); preset = saved.preset; }
        catch { removeSaved(); buildDefault(presets[0].id); }
      } else if (raw) removeSaved();
      suppressSave = false;
      dirty = false;
      activeId = api.activePanel?.id ?? activeId;
      subscriptions.push(api.onDidLayoutChange(scheduleSave));
      subscriptions.push(api.onDidActivePanelChange(() => activeId = api?.activePanel?.id ?? activeId));
    }
    for (const id of nodes.keys()) place(id);
    connect();
    media.addEventListener('change', connect);
    const theme = () => api?.updateOptions({ theme:document.documentElement.dataset.theme === 'light' ? themeLight : themeDark });
    window.addEventListener('agentfox:themechange', theme);
    window.addEventListener('keydown', shortcut, true);
    window.addEventListener('pagehide', persist);
    return () => {
      disposed = true;
      disconnect();
      media.removeEventListener('change', connect);
      window.removeEventListener('agentfox:themechange', theme);
      window.removeEventListener('keydown', shortcut, true);
      window.removeEventListener('pagehide', persist);
    };
  });
</script>

<section class="workstation" class:mobile={!desktop} aria-label={title} tabindex="-1" bind:this={root}>
  <header class="workstation-bar">
    <div class="workstation-brand"><PanelsTopLeft size={18}/><strong>{title}</strong><span class="preview">PREVIEW</span></div>
    <nav aria-label="Workspace layout">
      {#each presets as item}
        <button class:chosen={preset === item.id} aria-pressed={preset === item.id} on:click={() => selectPreset(item.id)}>{item.label}</button>
      {/each}
      <button on:click={() => openPalette(true)}><PanelsTopLeft size={14}/> Panels</button>
      <button on:click={() => openPalette()} title="Search commands (Ctrl+K)"><Command size={14}/> Commands <kbd>Ctrl K</kbd></button>
      <button on:click={maximize} disabled={!desktop} title="Maximize or restore the active group"><Maximize2 size={14}/><span class="sr-only">Maximize / restore</span></button>
      <button on:click={resetView}><RotateCcw size={14}/> Reset view</button>
      <button on:click={onExit}>Classic view</button>
    </nav>
  </header>
  {#if storageWarning}<p class="storage-warning" role="status">{storageWarning}</p>{/if}
  <div class="core-toolbar" bind:this={toolbar}></div>
  <div class="edition-health" bind:this={health}></div>
  <div class="workspace-dock" class:hidden={!desktop} bind:this={dockRoot}></div>
  <div class="workspace-stack" class:hidden={desktop} bind:this={stack}></div>
  <footer><span>Focus: {panels.find(p => p.id === activeId)?.title ?? 'Workspace'}</span><span>F6 panels · Ctrl [ / ] tabs · Ctrl Shift 1/2/3/4 Watchlist / Chart / Plan / Ticket</span></footer>
  <p class="sr-only" role="status">{notice}</p>
  <div hidden bind:this={depot}><slot {workspace}/></div>
</section>
<div class="workstation-overlays" bind:this={overlays}></div>

<dialog class="command-palette" bind:this={palette} on:cancel={() => previousFocus?.focus()} on:keydown={paletteKey} aria-label={panelsOnly ? 'Panels' : 'Workspace commands'}>
  <div class="palette-heading"><strong>{panelsOnly ? 'All panels' : 'Workspace commands'}</strong><button on:click={closePalette} aria-label="Close commands"><X size={16}/></button></div>
  <input bind:this={search} bind:value={query} on:input={() => resultIndex = 0} aria-label="Search commands" placeholder={panelsOnly ? 'Find a panel…' : 'Find an action or panel…'} role="combobox" aria-expanded="true" aria-controls="workspace-results" aria-activedescendant={results.length ? 'workspace-result-' + resultIndex : undefined} autocomplete="off"/>
  <div class="command-results" id="workspace-results" role="listbox" aria-label="Matching commands">
    {#each results as command, index (command.id)}
      <button id={'workspace-result-' + index} role="option" aria-selected={index === resultIndex} tabindex="-1" class:highlighted={index === resultIndex} on:click={() => execute(command)}>
        {command.label}
      </button>
    {:else}<p>No matching commands.</p>{/each}
  </div>
  <small>↑ ↓ select · Enter run · Esc return. Layout actions do not change trading state.</small>
</dialog>

<style>
  .workstation { height:100dvh; min-width:0; min-height:0; display:flex; flex-direction:column; overflow:hidden; background:var(--bg); color:var(--text); }
  .workstation-bar { display:flex; align-items:center; justify-content:space-between; flex-wrap:wrap; gap:.5rem; padding:.55rem .75rem; flex:none; border-bottom:1px solid var(--border-md); background:var(--surface-2); }
  .workstation-brand, nav, button { display:flex; align-items:center; gap:.4rem; }
  .workstation-brand { font-size:.85rem; }
  .preview { color:var(--warning); font-size:.55rem; letter-spacing:.1em; }
  nav { flex-wrap:wrap; }
  button { min-height:30px; padding:.25rem .5rem; font:inherit; font-size:.7rem; border:1px solid var(--border-md); border-radius:4px; background:var(--surface); color:var(--text-2); cursor:pointer; }
  button:hover, button.chosen { color:var(--text); border-color:var(--primary); background:var(--primary-dim); }
  button:disabled { opacity:.45; cursor:default; }
  button:focus-visible, input:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  kbd { font-size:.58rem; color:var(--text-3); }
  .core-toolbar, .edition-health { flex:none; min-width:0; }
  .storage-warning { margin:0; padding:.3rem .7rem; font-size:.7rem; color:var(--warning); background:var(--surface-2); }
  .edition-health { padding:0 .7rem; }
  .workspace-dock { flex:1 1 0; min-height:0; min-width:0; overflow:hidden; }
  .hidden { display:none; }
  footer { display:flex; justify-content:space-between; gap:1rem; padding:.25rem .7rem; border-top:1px solid var(--border); font-size:.61rem; color:var(--text-3); flex:none; }
  .workspace-dock :global(.workstation-panel) { height:100%; width:100%; min-height:0; min-width:0; overflow:auto; overscroll-behavior:contain; background:var(--surface); padding:.65rem; box-sizing:border-box; }
  .workspace-dock :global(.workstation-panel:focus-visible) { outline:2px solid var(--primary); outline-offset:-2px; }
  .workspace-dock :global(.mobile-panel-title) { display:none; }
  .workspace-dock :global(.panel-placeholder) { color:var(--text-3); font-size:.75rem; padding:1rem; }
  .workspace-dock :global([data-workspace-panel='watchlist'] > div) { height:100%; min-height:0; }
  .workspace-dock :global([data-workspace-panel='watchlist'] .watchlist) { height:100%; min-height:320px; contain:size; border:0; padding:0; }
  .workspace-dock :global([data-workspace-panel='watchlist'] .filter-row) { flex-wrap:wrap; }
  .workspace-dock :global([data-workspace-panel='watchlist'] .search-row) { flex-basis:100%; }
  .workspace-dock :global([data-workspace-panel='chart']) { container-type:size; }
  .workspace-dock :global([data-workspace-panel='chart'] .chart-card) { border:0; border-radius:0; padding:0; }
  /* Keep a usable price canvas at short heights. Details remain below it in the panel's scroll
     area; neither flex shrink nor a percentage-height chain can flatten the candles. */
  .workspace-dock :global([data-workspace-panel='chart'] .plot) { height:clamp(300px, calc(100cqh - 110px), 900px); }
  .workspace-dock :global(.dv-tab) { font-size:.7rem; }
  .workspace-dock :global(.dv-tab.dv-active-tab) { box-shadow:inset 0 2px var(--primary); }
  .workspace-dock { --dv-group-view-background-color:var(--surface); --dv-tabs-and-actions-container-background-color:var(--surface-2); --dv-activegroup-visiblepanel-tab-background-color:var(--surface); --dv-activegroup-visiblepanel-tab-color:var(--text); --dv-separator-border:var(--border-md); --dv-active-sash-color:var(--primary); --dv-tabs-and-actions-container-height:32px; }
  .command-palette { width:min(580px,calc(100vw - 2rem)); max-height:75dvh; padding:1rem; margin:10dvh auto auto; border:1px solid var(--border-high); border-radius:10px; background:var(--surface); color:var(--text); box-shadow:0 20px 70px #0008; }
  .command-palette::backdrop { background:#0008; }
  .palette-heading { display:flex; align-items:center; justify-content:space-between; margin-bottom:.75rem; }
  .command-palette input { width:100%; box-sizing:border-box; padding:.7rem; background:var(--surface-2); border:1px solid var(--border-md); color:var(--text); border-radius:5px; }
  .command-results { max-height:48dvh; overflow:auto; margin:.5rem 0; }
  .command-results button { width:100%; border-color:transparent; text-align:left; padding:.6rem; }
  .command-results button.highlighted { background:var(--primary-dim); border-color:var(--primary); color:var(--text); }
  .command-palette small { font-size:.65rem; color:var(--text-3); }
  .sr-only { position:absolute; width:1px; height:1px; overflow:hidden; clip-path:inset(50%); white-space:nowrap; }
  .mobile { height:auto; overflow:visible; }
  .workspace-stack :global(.workstation-panel) { padding:.75rem; min-width:0; overflow:auto; border-bottom:1px solid var(--border); }
  .workspace-stack :global(.mobile-panel-title) { font-size:1rem; margin:.4rem 0; }
  .mobile footer { display:none; }
</style>
