<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { createDockview, themeDark, themeLight, type DockviewApi, type IContentRenderer } from 'dockview';
  import 'dockview/dist/styles/dockview.css';
  import { Command, PanelsTopLeft, RotateCcw, Maximize2, Minimize2, Sun, Moon, Keyboard, Pin, PinOff, X } from 'lucide-svelte';
  import WorkspaceShortcuts from './WorkspaceShortcuts.svelte';
  import { readWorkspaceAppearance, saveWorkspaceAppearance, type WorkspaceTheme } from './workspaceAppearance';
  import type { WorkspaceCommand, WorkspaceComposition, WorkspacePanel, WorkspaceRegion } from './workspaceComposition';
  import { readWorkspaceLayout, saveWorkspaceLayout, type AutoHideTray } from './workspaceLayout';

  export let panels: WorkspacePanel[];
  export let title = 'Trading workstation';
  export let edition: string;
  export let storageKey: string;
  export let presets: { id: string; label: string; active: Partial<Record<WorkspaceRegion, string>> }[];
  export let onExit: () => void;

  let root: HTMLElement;
  let dockRoot: HTMLDivElement;
  let dockArea: HTMLDivElement;
  let depot: HTMLDivElement;
  let toolbar: HTMLDivElement;
  let health: HTMLDivElement;
  let overlays: HTMLDivElement;
  let stack: HTMLDivElement;
  let palette: HTMLDialogElement;
  let search: HTMLInputElement;
  let api: DockviewApi | null = null;
  let shortcutsSheet: WorkspaceShortcuts;
  let maximized = false;
  let localTheme: WorkspaceTheme | null = null;
  let currentTheme: WorkspaceTheme = 'dark';
  let hostTheme: WorkspaceTheme = 'dark';
  let applyingWorkspaceTheme = false;
  let appearanceWarning = '';
  const appearanceKey = storageKey + '.appearance';
  function applyTheme(theme: WorkspaceTheme) {
    currentTheme = theme;
    applyingWorkspaceTheme = true;
    document.documentElement.dataset.theme = theme;
    window.dispatchEvent(new CustomEvent('agentfox:themechange',{detail:theme}));
    applyingWorkspaceTheme = false;
  }
  function toggleTheme() {
    localTheme = currentTheme === 'dark' ? 'light' : 'dark';
    applyTheme(localTheme);
    try { localStorage.setItem(appearanceKey,saveWorkspaceAppearance(localTheme)); appearanceWarning = ''; }
    catch { appearanceWarning = 'Theme changed for this session; this browser could not save it.'; }
  }
  function followHostTheme() {
    localTheme = null;
    try { localStorage.removeItem(appearanceKey); appearanceWarning = ''; } catch { appearanceWarning = 'Could not clear the saved theme preference.'; }
    applyTheme(hostTheme);
  }
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
  let bottomTray: AutoHideTray | undefined;
  let trayOpen = false;
  let trayHost: HTMLDivElement;
  let trayShell: HTMLDivElement;
  let trayStrip: HTMLElement;
  let trayReturnFocus: HTMLElement | null = null;
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
    if (desktop && bottomTray && !bottomTray.ids.includes(id) && !api?.getPanel(id)
      && panels.find(p => p.id === id)?.region === 'bottom') bottomTray = { ...bottomTray, ids:[...bottomTray.ids,id] };
    if (desktop && bottomTray?.ids.includes(id)) {
      if (trayOpen && bottomTray.active !== id) depot.appendChild(container(bottomTray.active));
      if (!trayOpen) trayReturnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      bottomTray = { ...bottomTray, active:id };
      trayOpen = true;
      scheduleSave();
      void tick().then(() => { if (!disposed && trayOpen && bottomTray?.active === id) trayHost.appendChild(container(id)); });
    } else if (api) {
      closeTray(false);
      if (api.hasMaximizedGroup() && !api.getPanel(id)?.api.isMaximized()) api.exitMaximizedGroup();
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

  /** Dockview exposes the group action seam/API; its demo's header glyphs are app-supplied. */
  function drawHeaderIcon(button: HTMLButtonElement, paths: string[]) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg','svg');
    svg.setAttribute('viewBox','0 0 24 24');
    svg.setAttribute('width','14');
    svg.setAttribute('height','14');
    svg.setAttribute('fill','none');
    svg.setAttribute('stroke','currentColor');
    svg.setAttribute('stroke-width','2');
    svg.setAttribute('stroke-linecap','round');
    svg.setAttribute('stroke-linejoin','round');
    svg.setAttribute('aria-hidden','true');
    for (const d of paths) {
      const path = document.createElementNS('http://www.w3.org/2000/svg','path');
      path.setAttribute('d',d); svg.appendChild(path);
    }
    button.replaceChildren(svg);
  }

  function drawMaximizeIcon(button: HTMLButtonElement, restoring: boolean) {
    drawHeaderIcon(button,[restoring
      ? 'm14 10 7-7M20 10h-6V4M3 21l7-7M4 14h6v6'
      : 'm15 3 6 6M21 3h-6v6M9 21l-6-6M3 21v-6h6']);
  }

  function drawUnpinIcon(button: HTMLButtonElement) {
    drawHeaderIcon(button,['M12 17v5','M15 9.34V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H7.89','m2 2 20 20','M9 9v1.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h11']);
  }

  function buildDefault(next: string) {
    if (!api) return;
    parkTray();
    bottomTray = undefined;
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
      localStorage.setItem(storageKey, JSON.stringify(saveWorkspaceLayout(edition, preset, api.toJSON(), Date.now(), bottomTray, api.hasMaximizedGroup() ? api.activePanel?.id : undefined)));
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
    else { preset = presets[0].id; bottomTray = undefined; }
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
    if (trayOpen) { notice = 'Pin the bottom tools before maximizing their group.'; return; }
    if (!api?.activePanel) return;
    if (api.hasMaximizedGroup()) api.exitMaximizedGroup();
    else api.activePanel.api.maximize();
    maximized = api.hasMaximizedGroup();
    notice = maximized ? 'Group maximized. Escape or Restore group returns to your layout.' : 'Group restored.';
    scheduleSave();
  }
  function resize(width: number, height: number) {
    if (trayOpen && bottomTray) {
      bottomTray = { ...bottomTray, height:Math.min(900, Math.max(130, bottomTray.height + height)) };
      scheduleSave(); return;
    }
    const panel = api?.getPanel(activeId);
    if (!panel) return;
    panel.api.setSize({ width: Math.max(180, panel.api.width + width), height: Math.max(100, panel.api.height + height) });
  }
  function move(position: 'left' | 'right' | 'top' | 'bottom') {
    if (trayOpen) { notice = 'Pin bottom tools before moving their docking tabs.'; return; }
    const panel = api?.getPanel(activeId);
    if (!api || !panel) return;
    const direction = position === 'top' ? 'above' : position === 'bottom' ? 'below' : position;
    const group = api.addGroup({ direction });
    panel.api.moveTo({ group });
    focusPanel(panel.id);
  }
  function hideActive() {
    if (trayOpen) { closeTray(); return; }
    api?.getPanel(activeId)?.api.close();
    if (api?.activePanel) focusPanel(api.activePanel.id);
    else root.focus();
  }
  function cycleGroup(delta: number) {
    if (trayOpen) {
      closeTray(false);
      const group = delta > 0 ? api?.groups[0] : api?.groups.at(-1);
      if (group?.activePanel) focusPanel(group.activePanel.id);
      return;
    }
    if (!api?.groups.length) return;
    const groups = api.groups;
    const index = groups.findIndex(g => g === api?.activeGroup);
    if (bottomTray && (index + delta < 0 || index + delta >= groups.length)) { focusPanel(bottomTray.active); return; }
    const next = groups[(index + delta + groups.length) % groups.length].activePanel;
    if (next) focusPanel(next.id);
  }
  function cycleTab(delta: number) {
    if (trayOpen && bottomTray) {
      const index = bottomTray.ids.indexOf(bottomTray.active);
      focusPanel(bottomTray.ids[(index + delta + bottomTray.ids.length) % bottomTray.ids.length]);
      return;
    }
    const group = api?.activeGroup;
    if (!group?.panels.length) return;
    const index = group.panels.findIndex(p => p.id === group.activePanel?.id);
    focusPanel(group.panels[(index + delta + group.panels.length) % group.panels.length].id);
  }

  // App-owned auto-hide: only docking wrappers are removed. Producer nodes/readers stay mounted.
  // One tool group at a time; repinning puts it at the bottom without resetting the main grid.
  function parkTray() {
    for (const id of bottomTray?.ids ?? []) if (containers.has(id)) depot.appendChild(container(id));
    trayOpen = false;
  }
  function closeTray(restore = true) {
    if (!trayOpen) return;
    parkTray();
    if (restore) {
      const trigger = trayStrip?.querySelector<HTMLElement>(`[data-tray-id="${bottomTray?.active}"]`);
      (trigger ?? (trayReturnFocus?.isConnected ? trayReturnFocus : root)).focus();
    }
  }
  function unpinBottom(groupId?: string) {
    if (!api || bottomTray) return;
    const eligible = api.groups.filter(g => g.panels.length && g.panels.every(p => panels.find(s => s.id === p.id)?.region === 'bottom'));
    const group = groupId ? eligible.find(g => g.id === groupId) : eligible.includes(api.activeGroup!) ? api.activeGroup! : eligible.sort((a,b) => (b.api.boundingBox?.top ?? 0) - (a.api.boundingBox?.top ?? 0))[0];
    if (!group) { notice = 'No bottom tool group to unpin. Tab bottom tools together, or use Reset view.'; return; }
    if (api.hasMaximizedGroup()) api.exitMaximizedGroup();
    bottomTray = { ids:group.panels.map(p => p.id), active:group.activePanel!.id, height:Math.min(900, Math.max(260, group.api.height)) };
    suppressSave = true;
    for (const id of bottomTray.ids) api.getPanel(id)?.api.close();
    suppressSave = false;
    scheduleSave();
    void tick().then(() => trayStrip?.querySelector<HTMLElement>('button[data-tray-id]')?.focus());
    notice = 'Bottom tools unpinned. Open from the bottom strip or Panels; Escape closes the peek.';
  }
  function pinBottom() {
    if (!api || !bottomTray) return;
    const saved = bottomTray;
    parkTray();
    bottomTray = undefined;
    suppressSave = true;
    const first = addPanel(saved.ids[0], undefined, 'below');
    for (const id of saved.ids.slice(1)) addPanel(id, saved.ids[0]);
    first?.api.setSize({ height:saved.height });
    suppressSave = false;
    focusPanel(saved.active);
    scheduleSave();
    notice = 'Bottom tools pinned. Other groups and trading forms are unchanged.';
  }
  function outsideTray(event: Event) {
    const target = event.target;
    if (!trayOpen || !(target instanceof Node) || trayShell?.contains(target) || trayStrip?.contains(target)
      || (target instanceof Element && target.closest('dialog, [role="dialog"], [role="alertdialog"]'))) return;
    closeTray(false);
  }

  $: panelCommands = panels.map(p => ({ id: 'panel.' + p.id, label: 'Show ' + p.title, run: () => focusPanel(p.id) }));
  $: layoutCommands = [
    { id:'workspace.shortcuts', label:'Show keyboard shortcuts', run:() => shortcutsSheet.open() },
    { id:'workspace.theme', label:'Toggle light / dark theme', run:toggleTheme },
    { id:'workspace.host-theme', label:'Use host theme (clear workspace theme override)', run:followHostTheme },
    { id:'layout.bottom', label:bottomTray ? 'Pin bottom tools' : 'Unpin bottom tools (auto-hide)', run:() => bottomTray ? pinBottom() : unpinBottom() },
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
        if (trayOpen) { notice = 'Pin bottom tools before moving their docking tabs.'; return; }
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
    if (event.key === 'Escape' && trayOpen) { event.preventDefault(); event.stopPropagation(); closeTray(); return; }
    if (event.ctrlKey && !event.altKey && !event.shiftKey && event.code === 'Slash') {
      event.preventDefault(); event.stopPropagation(); void shortcutsSheet.open(); return;
    }
    if ((event.ctrlKey || event.metaKey) && !event.altKey && event.code === 'KeyK') {
      event.preventDefault(); event.stopPropagation(); void openPalette(); return;
    }
    if (event.key === 'F6' && !event.ctrlKey && !event.metaKey && !event.altKey) {
      event.preventDefault(); event.stopPropagation(); cycleGroup(event.shiftKey ? -1 : 1); return;
    }
    if (target?.closest('input, textarea, select, [contenteditable="true"]')) return;
    if (event.key === 'Escape' && api?.hasMaximizedGroup()) { event.preventDefault(); event.stopPropagation(); maximize(); return; }
    if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && event.code === 'Space') { event.preventDefault(); event.stopPropagation(); maximize(); return; }
    const direct: Record<string,string> = { Digit1:'watchlist', Digit2:'chart', Digit3:'plan', Digit4:'ticket', Digit5:'order-logs', Digit6:'portfolio', Digit7:'persistent', Digit8:'armed' };
    if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && direct[event.code]) focusPanel(direct[event.code]);
    else if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && event.code === 'Digit0') resetView();
    else if (event.ctrlKey && !event.shiftKey && event.code === 'BracketRight') cycleTab(1);
    else if (event.ctrlKey && !event.shiftKey && event.code === 'BracketLeft') cycleTab(-1);
    else return;
    event.preventDefault(); event.stopPropagation();
  }

  onMount(() => {
    hostTheme = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
    try {
      const raw = localStorage.getItem(appearanceKey);
      localTheme = readWorkspaceAppearance(raw);
      if (raw && !localTheme) localStorage.removeItem(appearanceKey);
    } catch { /* Browser storage is optional. */ }
    applyTheme(localTheme ?? hostTheme);
    const media = window.matchMedia('(min-width:901px)');
    let subscriptions: { dispose(): void }[] = [];
    function disconnect() {
      clearTimeout(saveTimer);
      persist();
      parkTray();
      for (const s of subscriptions) s.dispose();
      subscriptions = [];
      api?.dispose();
      api = null;
      maximized = false;
    }
    function connect() {
      desktop = media.matches;
      // matchMedia fires before Svelte flushes the visibility classes on a breakpoint transition.
      // Set these immediately so Dockview measures the real host, not a display:none zero rectangle.
      dockRoot.classList.toggle('hidden', !desktop);
      dockArea.classList.toggle('hidden', !desktop);
      stack.classList.toggle('hidden', desktop);
      if (!desktop) {
        disconnect();
        for (const p of panels) { stack.appendChild(container(p.id)); place(p.id); }
        return;
      }
      if (api) return;
      api = createDockview(dockRoot, {
        createRightHeaderActionComponent: group => {
          const element = document.createElement('div');
          element.className = 'group-actions';
          const pin = document.createElement('button');
          pin.className = 'group-control group-unpin';
          pin.title = 'Auto-hide this bottom tool group';
          pin.setAttribute('aria-label','Auto-hide this bottom tool group');
          drawUnpinIcon(pin);
          pin.onclick = () => unpinBottom(group.id);
          const expand = document.createElement('button'); expand.className = 'group-control';
          expand.onclick = () => { group.activePanel?.api.setActive(); maximize(); };
          element.onpointerdown = event => event.stopPropagation();
          element.append(pin,expand);
          const refresh = () => {
            pin.hidden = !group.panels.length || !group.panels.every(p => panels.find(s => s.id === p.id)?.region === 'bottom');
            pin.disabled = !!bottomTray;
            const restoring = group.api.isMaximized();
            drawMaximizeIcon(expand,restoring);
            expand.setAttribute('aria-label',(restoring ? 'Restore ' : 'Maximize ') + (group.activePanel?.title ?? 'group'));
            expand.title = expand.getAttribute('aria-label')!;
          };
          let listeners: {dispose():void}[] = [];
          return { element, init() { refresh(); if(api) listeners = [api.onDidLayoutChange(refresh),api.onDidMaximizedGroupChange(refresh)]; }, dispose() { listeners.forEach(s => s.dispose()); } };
        },
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
        try { api.fromJSON(saved.layout); preset = saved.preset; bottomTray = saved.bottomTray; if(saved.maximized) api.getPanel(saved.maximized)?.api.maximize(); }
        catch { removeSaved(); buildDefault(presets[0].id); }
      } else if (raw) removeSaved();
      suppressSave = false;
      dirty = false;
      activeId = api.activePanel?.id ?? activeId;
      maximized = api.hasMaximizedGroup();
      subscriptions.push(api.onDidLayoutChange(scheduleSave));
      subscriptions.push(api.onDidMaximizedGroupChange(() => { maximized = api?.hasMaximizedGroup() ?? false; scheduleSave(); }));
      subscriptions.push(api.onDidActivePanelChange(() => activeId = api?.activePanel?.id ?? activeId));
    }
    for (const id of nodes.keys()) place(id);
    connect();
    media.addEventListener('change', connect);
    const theme = () => {
      const incoming = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
      if (!applyingWorkspaceTheme) hostTheme = incoming;
      if (localTheme && incoming !== localTheme) { applyTheme(localTheme); return; }
      currentTheme = incoming;
      api?.updateOptions({ theme:incoming === 'light' ? themeLight : themeDark });
    };
    window.addEventListener('agentfox:themechange', theme);
    window.addEventListener('keydown', shortcut, true);
    window.addEventListener('pagehide', persist);
    window.addEventListener('pointerdown', outsideTray);
    window.addEventListener('focusin', outsideTray);
    return () => {
      disposed = true;
      disconnect();
      media.removeEventListener('change', connect);
      window.removeEventListener('agentfox:themechange', theme);
      window.removeEventListener('keydown', shortcut, true);
      window.removeEventListener('pagehide', persist);
      window.removeEventListener('pointerdown', outsideTray);
      window.removeEventListener('focusin', outsideTray);
      document.documentElement.dataset.theme = hostTheme;
      window.dispatchEvent(new CustomEvent('agentfox:themechange',{detail:hostTheme}));
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
      <button class="icon-control" on:click={maximize} disabled={!desktop} aria-pressed={maximized}
              aria-label={maximized ? 'Restore active group' : 'Maximize active group'}
              title={`${maximized ? 'Restore' : 'Maximize'} active group (Ctrl+Shift+Space)`}>
        {#if maximized}<Minimize2 size={14}/>{:else}<Maximize2 size={14}/>{/if}
      </button>
      <button on:click={toggleTheme} title={currentTheme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}>{#if currentTheme === 'dark'}<Sun size={14}/> Light theme{:else}<Moon size={14}/> Dark theme{/if}</button>
      <button on:click={() => shortcutsSheet.open()} title="Keyboard shortcuts (Ctrl+/)"><Keyboard size={14}/> Shortcuts</button>
      <button on:click={() => bottomTray ? pinBottom() : unpinBottom()} disabled={!desktop} title="Auto-hide a bottom tool group without closing its contents">
        {#if bottomTray}<Pin size={14}/> Pin bottom{:else}<PinOff size={14}/> Unpin bottom{/if}
      </button>
      <button on:click={resetView}><RotateCcw size={14}/> Reset view</button>
      <button on:click={onExit}>Classic view</button>
    </nav>
  </header>
  {#if storageWarning}<p class="storage-warning" role="status">{storageWarning}</p>{/if}
  {#if appearanceWarning}<p class="storage-warning" role="status">{appearanceWarning}</p>{/if}
  <div class="core-toolbar" bind:this={toolbar}></div>
  <div class="edition-health" bind:this={health}></div>
  <div class="dock-area" class:hidden={!desktop} bind:this={dockArea}>
    <div class="workspace-dock" bind:this={dockRoot}></div>
    <div class="tray-peek" class:hidden={!trayOpen} bind:this={trayShell} style:height={bottomTray?.height + 'px'}>
      <div class="tray-heading"><strong>{panels.find(p => p.id === bottomTray?.active)?.title}</strong><span>Auto-hidden · Esc to close</span>
        <button aria-label="Make bottom peek shorter" on:click={() => resize(0,-80)}>−</button><button aria-label="Make bottom peek taller" on:click={() => resize(0,80)}>+</button>
        <button on:click={pinBottom}><Pin size={14}/> Pin bottom</button><button aria-label="Close bottom peek" on:click={() => closeTray()}><X size={14}/></button>
      </div>
      <div class="tray-content" bind:this={trayHost}></div>
    </div>
  </div>
  {#if desktop && bottomTray}
    <nav class="tray-strip" aria-label="Auto-hidden bottom panels" bind:this={trayStrip}>
      {#each bottomTray.ids as id}
        <button data-tray-id={id} aria-expanded={trayOpen && bottomTray.active === id} class:chosen={trayOpen && bottomTray.active === id}
          on:click={() => trayOpen && bottomTray?.active === id ? closeTray() : focusPanel(id)}>{panels.find(p => p.id === id)?.title}</button>
      {/each}
    </nav>
  {/if}
  <div class="workspace-stack" class:hidden={desktop} bind:this={stack}></div>
  <footer><span>Focus: {panels.find(p => p.id === activeId)?.title ?? 'Workspace'}</span><span>F6 panels · Ctrl [ / ] tabs · Ctrl Shift 1/2/3/4/5 Watchlist / Chart / Plan / Ticket / Logs</span></footer>
  <p class="sr-only" role="status">{notice}</p>
  <div hidden bind:this={depot}><slot {workspace}/></div>
</section>
<div class="workstation-overlays" bind:this={overlays}></div>
<WorkspaceShortcuts bind:this={shortcutsSheet}/>

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
  .dock-area { position:relative; display:flex; flex:1 1 0; min-height:0; overflow:hidden; }
  .tray-peek { position:absolute; inset:auto 0 0; max-height:90%; min-height:0; display:flex; flex-direction:column; background:var(--surface); border:1px solid var(--primary); box-shadow:0 -8px 28px #0005; z-index:5; }
  .tray-heading { display:flex; align-items:center; gap:.5rem; padding:.25rem .6rem; flex:none; background:var(--surface-2); font-size:.75rem; }
  .tray-heading span { flex:1; color:var(--text-2); font-size:.65rem; }
  .tray-content { flex:1; min-height:0; overflow:hidden; }
  .tray-content :global(.workstation-panel) { height:100%; overflow:auto; padding:.65rem; box-sizing:border-box; }
  .tray-content :global(.mobile-panel-title) { display:none; }
  .tray-content :global(.workstation-panel:focus-visible) { outline:2px solid var(--primary); outline-offset:-2px; }
  .tray-strip { flex:none; flex-wrap:nowrap; overflow-x:auto; gap:0; border-top:1px solid var(--border-md); background:var(--surface-2); }
  .tray-strip button { flex:none; border-radius:0; border-color:transparent; }
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
  .workspace-dock :global(.group-actions) { display:flex; align-items:center; }
  .workspace-dock :global(.group-control) { align-self:center; display:inline-flex; align-items:center; justify-content:center; cursor:pointer; margin:0 .05rem; min-width:26px; min-height:26px; padding:.25rem; border:1px solid transparent; border-radius:3px; background:transparent; color:var(--text-2); font-size:.65rem; transition:color 150ms ease,background-color 150ms ease; }
  .workspace-dock :global(.group-control[hidden]) { display:none; }
  .workspace-dock :global(.group-control:hover) { color:var(--text); background:var(--surface-3); }
  .workspace-dock :global(.group-control:focus-visible) { outline:2px solid var(--primary); outline-offset:-2px; }
  .workspace-dock :global(.group-control:disabled) { opacity:.5; cursor:default; }
  .workspace-dock :global(.dv-tab.dv-active-tab) { box-shadow:inset 0 2px var(--primary); }
  .workspace-dock { --dv-group-view-background-color:var(--surface); --dv-tabs-and-actions-container-background-color:var(--surface-2); --dv-activegroup-visiblepanel-tab-background-color:var(--surface); --dv-activegroup-visiblepanel-tab-color:var(--text); --dv-separator-border:var(--border-md); --dv-active-sash-color:var(--primary); --dv-tabs-and-actions-container-height:32px; }
  .icon-control { min-width:30px; justify-content:center; padding-inline:.4rem; }
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
