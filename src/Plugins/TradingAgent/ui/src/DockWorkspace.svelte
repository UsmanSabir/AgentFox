<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { createDockview, themeDark, themeLight, type DockviewApi, type DockviewGroupPanel, type IContentRenderer } from 'dockview';
  import 'dockview/dist/styles/dockview.css';
  import { Command, PanelsTopLeft, RotateCcw, Maximize, Minimize, Keyboard, Pin, PinOff, X, Fullscreen, Shrink } from 'lucide-svelte';
  import WorkspaceShortcuts from './WorkspaceShortcuts.svelte';
  import { workspacePresetPanels, type WorkspaceCommand, type WorkspaceComposition, type WorkspacePanel, type WorkspaceRegion, type WorkspacePreset } from './workspaceComposition';
  import {
    readWorkspaceLayout, saveWorkspaceLayout,
    type AutoHideEdge, type AutoHideTrays
  } from './workspaceLayout';

  export let panels: WorkspacePanel[];
  export let title = 'Trading workstation';
  export let edition: string;
  export let storageKey: string;
  export let presets: WorkspacePreset[];
  export let onPresetChange: (id: string) => void = () => {};
  export let onExit: () => void;
  /**
   * Open on this preset instead of the saved layout's, when it names one of `presets` — for a link that
   * asks for a particular view. The choice is then saved like any other. Unset (the default) changes nothing.
   */
  export let initialPreset: string | null = null;

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
  let fullscreen = false;
  let fullscreenSupported = false;
  async function toggleFullscreen() {
    if (!fullscreenSupported) {
      notice = 'Page full screen is unavailable here. Use the browser F11 command instead.';
      return;
    }
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await root.requestFullscreen();
    } catch {
      notice = 'The browser blocked page full screen. Use the toolbar button again or the browser F11 command.';
    }
  }
  function syncFullscreen() {
    fullscreen = document.fullscreenElement === root;
  }
  let desktop = true;
  const requestedPreset = presets.some(item => item.id === initialPreset) ? initialPreset : null;
  let preset = requestedPreset ?? presets[0].id;
  // Set when a preset was chosen before the layout was built — by a click on a phone, or by
  // `initialPreset` — so the saved layout is skipped once and the choice is saved instead.
  let mobilePresetChanged = requestedPreset !== null;
  $: selectedPreset = presets.find(item => item.id === preset) ?? presets[0];
  $: availablePanels = workspacePresetPanels(panels, selectedPreset);
  function offeredPanels() { return workspacePresetPanels(panels, presets.find(item => item.id === preset)); }
  function panelRegion(id: string) {
    const columns = presets.find(item => item.id === preset)?.columns;
    const index = columns?.findIndex(column => column.panels.includes(id)) ?? -1;
    if (index >= 0) return index === 0 ? 'left' : index === columns!.length - 1 ? 'right' : 'center';
    return panels.find(panel => panel.id === id)?.region;
  }
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
  let autoHide: AutoHideTrays = {};
  let openEdge: AutoHideEdge | null = null;
  let trayHost: HTMLDivElement;
  let trayShell: HTMLDivElement;
  let leftTrayStrip: HTMLElement;
  let rightTrayStrip: HTMLElement;
  let bottomTrayStrip: HTMLElement;
  let trayReturnFocus: HTMLElement | null = null;
  const nodes = new Map<string, HTMLElement>();
  const containers = new Map<string, HTMLDivElement>();
  const special = new Set(['core-toolbar', 'edition-health', 'core-dialogs']);
  const edges: AutoHideEdge[] = ['left','right','bottom'];

  function trayForPanel(id: string) {
    return edges.find(edge => autoHide[edge]?.ids.includes(id));
  }

  function trayStrip(edge: AutoHideEdge) {
    return edge === 'left' ? leftTrayStrip : edge === 'right' ? rightTrayStrip : bottomTrayStrip;
  }

  function currentTray() {
    return openEdge ? autoHide[openEdge] : undefined;
  }

  function pinOpenTray() {
    if (openEdge) pin(openEdge);
  }

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
    if (!offeredPanels().some(p => p.id === id)) return;
    const region = panelRegion(id);
    if (desktop && region && region !== 'center' && autoHide[region] && !trayForPanel(id) && !api?.getPanel(id)) {
      autoHide = { ...autoHide, [region]:{ ...autoHide[region]!, ids:[...autoHide[region]!.ids,id] } };
    }
    const edge = trayForPanel(id);
    if (desktop && edge) {
      const tray = autoHide[edge]!;
      if (openEdge && (openEdge !== edge || tray.active !== id)) depot.appendChild(container(autoHide[openEdge]!.active));
      if (!openEdge) trayReturnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      autoHide = { ...autoHide, [edge]:{ ...tray, active:id } };
      openEdge = edge;
      scheduleSave();
      void tick().then(() => { if (!disposed && openEdge === edge && autoHide[edge]?.active === id) trayHost.appendChild(container(id)); });
    } else if (api) {
      closeTray(false);
      if (api.hasMaximizedGroup() && !api.getPanel(id)?.api.isMaximized()) api.exitMaximizedGroup();
      let panel = api.getPanel(id);
      if (!panel) {
        const region = panelRegion(id);
        const peer = offeredPanels().find(p => panelRegion(p.id) === region && api?.getPanel(p.id));
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

  /**
   * Places panels the catalogue has gained since this layout was saved.
   *
   * A saved layout records what the operator ARRANGED, not what the product offers, so restoring one
   * verbatim hides every panel added since — for ever, and only from the people who have used the
   * workspace before. `known` is what makes the difference between "hidden on purpose" and "did not
   * exist yet" legible; a record written before that field shipped cannot say, so it is read as having
   * known exactly what it placed. That adopts a deliberately hidden panel once, on the first load after
   * this shipped, and never again — the save below records the catalogue in full.
   *
   * Ids parked in any auto-hide tray are skipped: readWorkspaceLayout refuses a record that has an id
   * in both, so their absence from the layout is a placement rather than a gap.
   */
  function adoptNewPanels(placed: readonly string[], known?: readonly string[]) {
    if (!api) return false;
    const hidden = edges.flatMap(edge => autoHide[edge]?.ids ?? []);
    const seen = new Set(known ?? [...placed, ...hidden]);
    let adopted = false;
    for (const spec of offeredPanels()) {
      if (seen.has(spec.id) || api.getPanel(spec.id) || trayForPanel(spec.id)) continue;
      const peer = offeredPanels().find(p => panelRegion(p.id) === panelRegion(spec.id) && api?.getPanel(p.id));
      addPanel(spec.id, peer?.id);
      adopted = true;
    }
    return adopted;
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
    svg.setAttribute('width','16');
    svg.setAttribute('height','16');
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
    drawHeaderIcon(button,restoring
      ? ['M8 3v3a2 2 0 0 1-2 2H3','M21 8h-3a2 2 0 0 1-2-2V3','M3 16h3a2 2 0 0 1 2 2v3','M16 21v-3a2 2 0 0 1 2-2h3']
      : ['M8 3H5a2 2 0 0 0-2 2v3','M21 8V5a2 2 0 0 0-2-2h-3','M3 16v3a2 2 0 0 0 2 2h3','M16 21h3a2 2 0 0 0 2-2v-3']);
  }

  function drawUnpinIcon(button: HTMLButtonElement) {
    drawHeaderIcon(button,['M12 17v5','M15 9.34V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H7.89','m2 2 20 20','M9 9v1.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h11']);
  }

  function buildDefault(next: string) {
    if (!api) return;
    parkTrays();
    autoHide = {};
    api.exitMaximizedGroup();
    api.clear();
    const selected = presets.find(p => p.id === next) ?? presets[0];
    preset = selected.id;
    if (selected.columns?.length) {
      let previous: string | undefined;
      const totalWeight = selected.columns.reduce((total, column) => total + (column.weight ?? 1), 0);
      for (const column of selected.columns) {
        const first = column.panels[0];
        addPanel(first, previous, previous ? 'right' : 'within');
        for (const id of column.panels.slice(1)) addPanel(id, first);
        api.getPanel(first)?.api.setSize({ width: dockRoot.clientWidth * (column.weight ?? 1) / totalWeight });
        api.getPanel(column.active)?.api.setActive();
        previous = first;
      }
      activeId = selected.columns[0].active;
      api.getPanel(activeId)?.api.setActive();
      return;
    }
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
      localStorage.setItem(storageKey, JSON.stringify(saveWorkspaceLayout(edition, preset, api.toJSON(), Date.now(), autoHide, api.hasMaximizedGroup() ? api.activePanel?.id : undefined, panels.map(p => p.id))));
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
    else { preset = presets[0].id; autoHide = {}; }
    removeSaved();
    storageWarning = '';
    dirty = false;
    suppressSave = false;
    updateMobilePanels();
    onPresetChange(preset);
    notice = 'Default view restored. Trading state and open forms are unchanged.';
  }
  function selectPreset(id: string) {
    if (!api) { preset = id; mobilePresetChanged = true; updateMobilePanels(); onPresetChange(preset); return; }
    clearTimeout(saveTimer);
    suppressSave = true;
    buildDefault(id);
    suppressSave = false;
    scheduleSave();
    onPresetChange(preset);
    notice = 'Layout changed. This mode’s panels are available in Panels.';
  }
  function maximize() {
    if (openEdge) { notice = `Pin the ${openEdge} panels before maximizing their group.`; return; }
    if (!api?.activePanel) return;
    if (api.hasMaximizedGroup()) api.exitMaximizedGroup();
    else api.activePanel.api.maximize();
    maximized = api.hasMaximizedGroup();
    notice = maximized ? 'Group maximized. Escape or Restore group returns to your layout.' : 'Group restored.';
    scheduleSave();
  }
  function resize(width: number, height: number) {
    if (openEdge) {
      const tray = autoHide[openEdge]!;
      const delta = openEdge === 'bottom' ? height : width;
      const minimum = openEdge === 'bottom' ? 130 : 180;
      autoHide = { ...autoHide, [openEdge]:{ ...tray, size:Math.min(900, Math.max(minimum, tray.size + delta)) } };
      scheduleSave(); return;
    }
    const panel = api?.getPanel(activeId);
    if (!panel) return;
    panel.api.setSize({ width: Math.max(180, panel.api.width + width), height: Math.max(100, panel.api.height + height) });
  }
  function move(position: 'left' | 'right' | 'top' | 'bottom') {
    if (openEdge) { notice = `Pin the ${openEdge} panels before moving their docking tabs.`; return; }
    const panel = api?.getPanel(activeId);
    if (!api || !panel) return;
    const direction = position === 'top' ? 'above' : position === 'bottom' ? 'below' : position;
    const group = api.addGroup({ direction });
    panel.api.moveTo({ group });
    focusPanel(panel.id);
  }
  function hideActive() {
    if (openEdge) { closeTray(); return; }
    api?.getPanel(activeId)?.api.close();
    if (api?.activePanel) focusPanel(api.activePanel.id);
    else root.focus();
  }
  function cycleGroup(delta: number) {
    const targets = [autoHide.left?.active, ...((api?.groups ?? []).map(group => group.activePanel?.id)), autoHide.right?.active, autoHide.bottom?.active]
      .filter((id): id is string => !!id);
    if (!targets.length) return;
    const index = Math.max(0, targets.indexOf(activeId));
    focusPanel(targets[(index + delta + targets.length) % targets.length]);
  }
  function cycleTab(delta: number) {
    if (openEdge) {
      const tray = autoHide[openEdge]!;
      const index = tray.ids.indexOf(tray.active);
      focusPanel(tray.ids[(index + delta + tray.ids.length) % tray.ids.length]);
      return;
    }
    const group = api?.activeGroup;
    if (!group?.panels.length) return;
    const index = group.panels.findIndex(p => p.id === group.activePanel?.id);
    focusPanel(group.panels[(index + delta + group.panels.length) % group.panels.length].id);
  }

  // App-owned auto-hide: only docking wrappers are removed. Producer nodes/readers stay mounted.
  // One group per edge; repinning restores that edge without resetting the rest of the grid.
  function parkTray(edge: AutoHideEdge) {
    for (const id of autoHide[edge]?.ids ?? []) if (containers.has(id)) depot.appendChild(container(id));
    if (openEdge === edge) openEdge = null;
  }
  function parkTrays() {
    for (const edge of edges) parkTray(edge);
  }
  function closeTray(restore = true) {
    if (!openEdge) return;
    const edge = openEdge;
    const active = autoHide[edge]?.active;
    parkTray(edge);
    if (restore) {
      const trigger = trayStrip(edge)?.querySelector<HTMLElement>(`[data-tray-id="${active}"]`);
      (trigger ?? (trayReturnFocus?.isConnected ? trayReturnFocus : root)).focus();
    }
  }
  function groupRegion(group: DockviewGroupPanel): AutoHideEdge | null {
    const region = panelRegion(group.panels[0]?.id);
    if (region !== 'left' && region !== 'right' && region !== 'bottom') return null;
    return group.panels.length && group.panels.every(panel => panelRegion(panel.id) === region) ? region : null;
  }
  function unpin(edge: AutoHideEdge, groupId?: string) {
    if (!api || autoHide[edge]) return;
    const eligible = api.groups.filter(group => groupRegion(group) === edge);
    const group = groupId ? eligible.find(g => g.id === groupId) : eligible.includes(api.activeGroup!) ? api.activeGroup! : eligible.sort((a,b) => (b.api.boundingBox?.top ?? 0) - (a.api.boundingBox?.top ?? 0))[0];
    if (!group) { notice = `No ${edge} panel group to unpin. Tab its panels together, or use Reset view.`; return; }
    if (api.hasMaximizedGroup()) api.exitMaximizedGroup();
    const dimension = edge === 'bottom' ? group.api.height : group.api.width;
    autoHide = { ...autoHide, [edge]:{
      ids:group.panels.map(p => p.id), active:group.activePanel!.id,
      size:Math.min(900, Math.max(edge === 'bottom' ? 260 : 240, dimension))
    } };
    suppressSave = true;
    for (const id of autoHide[edge]!.ids) api.getPanel(id)?.api.close();
    suppressSave = false;
    scheduleSave();
    void tick().then(() => trayStrip(edge)?.querySelector<HTMLElement>('button[data-tray-id]')?.focus());
    notice = `${edge[0].toUpperCase() + edge.slice(1)} panels unpinned. Open them from the edge strip or Panels; Escape closes the peek.`;
  }
  function pin(edge: AutoHideEdge) {
    const saved = autoHide[edge];
    if (!api || !saved) return;
    parkTray(edge);
    autoHide = Object.fromEntries(Object.entries(autoHide).filter(([key]) => key !== edge)) as AutoHideTrays;
    suppressSave = true;
    const direction = edge === 'bottom' ? 'below' : edge;
    const first = addPanel(saved.ids[0], undefined, direction);
    for (const id of saved.ids.slice(1)) addPanel(id, saved.ids[0]);
    first?.api.setSize(edge === 'bottom' ? { height:saved.size } : { width:saved.size });
    suppressSave = false;
    focusPanel(saved.active);
    scheduleSave();
    notice = `${edge[0].toUpperCase() + edge.slice(1)} panels pinned. Other groups and trading forms are unchanged.`;
  }
  function outsideTray(event: Event) {
    const target = event.target;
    if (!openEdge || !(target instanceof Node) || trayShell?.contains(target) || trayStrip(openEdge)?.contains(target)
      || (target instanceof Element && target.closest('dialog, [role="dialog"], [role="alertdialog"]'))) return;
    closeTray(false);
  }

  $: panelCommands = availablePanels.map(p => ({ id: 'panel.' + p.id, label: 'Show ' + p.title, run: () => focusPanel(p.id) }));
  $: layoutCommands = [
    { id:'workspace.shortcuts', label:'Show keyboard shortcuts', run:() => shortcutsSheet.open() },
    { id:'workspace.fullscreen', label:fullscreen ? 'Exit page full screen' : 'Enter page full screen', run:toggleFullscreen,
      disabled:() => fullscreenSupported ? null : 'Page full screen is unavailable in this browser or host frame.' },
    ...edges.map(edge => ({ id:'layout.' + edge, label:autoHide[edge] ? `Pin ${edge} panels` : `Unpin ${edge} panels (auto-hide)`, run:() => autoHide[edge] ? pin(edge) : unpin(edge) })),
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
    ...availablePanels.map(p => ({
      id:'layout.tab.' + p.id, label:'Tab active panel with ' + p.title,
      run:() => {
        if (openEdge) { notice = `Pin the ${openEdge} panels before moving their docking tabs.`; return; }
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
    if (event.key === 'Escape' && openEdge) { event.preventDefault(); event.stopPropagation(); closeTray(); return; }
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
    if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && event.code === 'KeyF') {
      event.preventDefault(); event.stopPropagation(); void toggleFullscreen(); return;
    }
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

  function updateMobilePanels() {
    if (desktop || !stack || !depot) return;
    for (const p of panels) depot.appendChild(container(p.id));
    const columns = presets.find(item => item.id === preset)?.columns;
    const ids = columns?.flatMap(column => column.panels) ?? offeredPanels().map(panel => panel.id);
    for (const id of ids) { stack.appendChild(container(id)); place(id); }
  }

  onMount(() => {
    fullscreenSupported = typeof root.requestFullscreen === 'function' && document.fullscreenEnabled !== false;
    syncFullscreen();
    // The host owns appearance for the whole application. Remove the retired per-workstation
    // override once so an older preview preference cannot fight the global theme after upgrade.
    try { localStorage.removeItem(storageKey + '.appearance'); } catch { /* Browser storage is optional. */ }
    const media = window.matchMedia('(min-width:901px)');
    let subscriptions: { dispose(): void }[] = [];
    function disconnect() {
      clearTimeout(saveTimer);
      persist();
      parkTrays();
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
        let raw: string | null = null;
        try { raw = localStorage.getItem(storageKey); } catch { /* Browser storage is optional. */ }
        const saved = readWorkspaceLayout(raw, edition, panels.map(p => p.id), presets.map(p => p.id));
        if (saved && !mobilePresetChanged) preset = saved.preset;
        else if (raw) removeSaved();
        updateMobilePanels();
        onPresetChange(preset);
        return;
      }
      if (api) return;
      api = createDockview(dockRoot, {
        createRightHeaderActionComponent: group => {
          const element = document.createElement('div');
          element.className = 'group-actions';
          const pin = document.createElement('button');
          pin.className = 'group-control group-unpin';
          drawUnpinIcon(pin);
          pin.onclick = () => {
            const edge = groupRegion(group);
            if (edge) unpin(edge, group.id);
          };
          const expand = document.createElement('button'); expand.className = 'group-control';
          expand.onclick = () => { group.activePanel?.api.setActive(); maximize(); };
          element.onpointerdown = event => event.stopPropagation();
          element.append(pin,expand);
          const refresh = () => {
            const edge = groupRegion(group);
            pin.hidden = !edge;
            pin.disabled = !!edge && !!autoHide[edge];
            pin.setAttribute('aria-label', edge ? `Unpin ${edge} panel group` : 'Unpin panel group');
            pin.title = pin.getAttribute('aria-label')!;
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
      const saved = mobilePresetChanged ? null : readWorkspaceLayout(raw, edition, panels.map(p => p.id), presets.map(p => p.id));
      let adopted = false;
      if (saved) {
        try {
          api.fromJSON(saved.layout); preset = saved.preset; autoHide = saved.autoHide ?? {};
          const allowed = new Set(offeredPanels().map(panel => panel.id));
          if (Object.keys(saved.layout.panels).some(id => !allowed.has(id))
            || edges.some(edge => autoHide[edge]?.ids.some(id => !allowed.has(id)))) {
            buildDefault(preset);
            adopted = true;
          }
          adopted = adoptNewPanels(Object.keys(saved.layout.panels), saved.known) || adopted;
          if(saved.maximized) api.getPanel(saved.maximized)?.api.maximize();
        }
        catch { removeSaved(); buildDefault(presets[0].id); }
      } else if (raw) removeSaved();
      suppressSave = false;
      dirty = false;
      if (mobilePresetChanged) { mobilePresetChanged = false; dirty = true; persist(); }
      // Written back immediately when a panel was adopted, so the record gains its catalogue list even
      // if the operator never moves anything. Without it the adoption is harmless but endless, and a
      // panel they hide right afterwards would come back on the next load.
      if (adopted) { dirty = true; persist(); }
      activeId = api.activePanel?.id ?? activeId;
      maximized = api.hasMaximizedGroup();
      subscriptions.push(api.onDidLayoutChange(scheduleSave));
      subscriptions.push(api.onDidMaximizedGroupChange(() => { maximized = api?.hasMaximizedGroup() ?? false; scheduleSave(); }));
      subscriptions.push(api.onDidActivePanelChange(() => activeId = api?.activePanel?.id ?? activeId));
      onPresetChange(preset);
    }
    for (const id of nodes.keys()) place(id);
    connect();
    media.addEventListener('change', connect);
    const theme = () => {
      const incoming = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
      api?.updateOptions({ theme:incoming === 'light' ? themeLight : themeDark });
    };
    window.addEventListener('agentfox:themechange', theme);
    window.addEventListener('keydown', shortcut, true);
    document.addEventListener('fullscreenchange', syncFullscreen);
    window.addEventListener('pagehide', persist);
    window.addEventListener('pointerdown', outsideTray);
    window.addEventListener('focusin', outsideTray);
    return () => {
      disposed = true;
      disconnect();
      media.removeEventListener('change', connect);
      window.removeEventListener('agentfox:themechange', theme);
      window.removeEventListener('keydown', shortcut, true);
      document.removeEventListener('fullscreenchange', syncFullscreen);
      window.removeEventListener('pagehide', persist);
      window.removeEventListener('pointerdown', outsideTray);
      window.removeEventListener('focusin', outsideTray);
    };
  });
</script>

<section class="workstation" class:mobile={!desktop} aria-label={selectedPreset.title ?? title} tabindex="-1" bind:this={root}>
  <header class="workstation-bar">
    <div class="workstation-brand"><PanelsTopLeft size={18}/><strong>{selectedPreset.title ?? title}</strong><span class="preview">PREVIEW</span></div>
    <nav aria-label="Workspace layout">
      {#each presets as item}
        <button class:chosen={preset === item.id} aria-pressed={preset === item.id} on:click={() => selectPreset(item.id)}>{item.label}</button>
      {/each}
      <button on:click={() => openPalette(true)}><PanelsTopLeft size={14}/> Panels</button>
      <button on:click={() => openPalette()} title="Search commands (Ctrl+K)"><Command size={14}/> Commands <kbd>Ctrl K</kbd></button>
      <button class="icon-control" on:click={maximize} disabled={!desktop} aria-pressed={maximized}
              aria-label={maximized ? 'Restore active group' : 'Maximize active group'}
              title={`${maximized ? 'Restore' : 'Maximize'} active group (Ctrl+Shift+Space)`}>
        {#if maximized}<Minimize size={16}/>{:else}<Maximize size={16}/>{/if}
      </button>
      <button class="icon-control" on:click={toggleFullscreen} disabled={!desktop || !fullscreenSupported}
              aria-pressed={fullscreen} aria-label={fullscreen ? 'Exit page full screen' : 'Page full screen'}
              title={fullscreen ? 'Exit page full screen (Escape)' : 'Page full screen (Ctrl+Shift+F; browser F11 is separate)'}>
        {#if fullscreen}<Shrink size={16}/>{:else}<Fullscreen size={16}/>{/if}
      </button>
      <button on:click={() => shortcutsSheet.open()} title="Keyboard shortcuts (Ctrl+/)"><Keyboard size={14}/> Shortcuts</button>
      {#each edges as edge}
        <button on:click={() => autoHide[edge] ? pin(edge) : unpin(edge)} disabled={!desktop}
          title={`Auto-hide or pin the ${edge} panel group without closing its contents`}>
          {#if autoHide[edge]}<Pin size={14}/> Pin {edge}{:else}<PinOff size={14}/> Unpin {edge}{/if}
        </button>
      {/each}
      <button on:click={resetView}><RotateCcw size={14}/> Reset view</button>
      <button on:click={onExit}>Classic view</button>
    </nav>
  </header>
  {#if storageWarning}<p class="storage-warning" role="status">{storageWarning}</p>{/if}
  <div class="workspace-operational-row">
    <div class="core-toolbar" class:hidden={selectedPreset.showToolbar === false} bind:this={toolbar}></div>
    <div class="edition-health" bind:this={health}></div>
  </div>
  <div class="dock-area" class:hidden={!desktop} bind:this={dockArea}>
    {#if autoHide.left}
      <nav class="tray-strip side left" aria-label="Auto-hidden left panels" bind:this={leftTrayStrip}>
        {#each autoHide.left.ids as id}
          <button data-tray-id={id} aria-expanded={openEdge === 'left' && autoHide.left.active === id} class:chosen={openEdge === 'left' && autoHide.left.active === id}
            on:click={() => openEdge === 'left' && autoHide.left?.active === id ? closeTray() : focusPanel(id)}>{panels.find(p => p.id === id)?.title}</button>
        {/each}
      </nav>
    {/if}
    <div class="workspace-dock" bind:this={dockRoot}></div>
    {#if autoHide.right}
      <nav class="tray-strip side right" aria-label="Auto-hidden right panels" bind:this={rightTrayStrip}>
        {#each autoHide.right.ids as id}
          <button data-tray-id={id} aria-expanded={openEdge === 'right' && autoHide.right.active === id} class:chosen={openEdge === 'right' && autoHide.right.active === id}
            on:click={() => openEdge === 'right' && autoHide.right?.active === id ? closeTray() : focusPanel(id)}>{panels.find(p => p.id === id)?.title}</button>
        {/each}
      </nav>
    {/if}
    {#if openEdge}
      <div class="tray-peek" class:left={openEdge === 'left'} class:right={openEdge === 'right'} class:bottom={openEdge === 'bottom'} bind:this={trayShell}
        style:width={openEdge === 'bottom' ? undefined : currentTray()?.size + 'px'}
        style:height={openEdge === 'bottom' ? currentTray()?.size + 'px' : undefined}>
        <div class="tray-heading"><strong>{panels.find(p => p.id === currentTray()?.active)?.title}</strong><span>Auto-hidden · Esc to close</span>
          {#if openEdge === 'bottom'}
            <button aria-label="Make bottom peek shorter" on:click={() => resize(0,-80)}>−</button><button aria-label="Make bottom peek taller" on:click={() => resize(0,80)}>+</button>
          {:else}
            <button aria-label={`Make ${openEdge} peek narrower`} on:click={() => resize(-80,0)}>−</button><button aria-label={`Make ${openEdge} peek wider`} on:click={() => resize(80,0)}>+</button>
          {/if}
          <button on:click={pinOpenTray}><Pin size={14}/> Pin {openEdge}</button><button aria-label={`Close ${openEdge} peek`} on:click={() => closeTray()}><X size={14}/></button>
        </div>
        <div class="tray-content" bind:this={trayHost}></div>
      </div>
    {/if}
  </div>
  {#if desktop && autoHide.bottom}
    <nav class="tray-strip bottom" aria-label="Auto-hidden bottom panels" bind:this={bottomTrayStrip}>
      {#each autoHide.bottom.ids as id}
        <button data-tray-id={id} aria-expanded={openEdge === 'bottom' && autoHide.bottom.active === id} class:chosen={openEdge === 'bottom' && autoHide.bottom.active === id}
          on:click={() => openEdge === 'bottom' && autoHide.bottom?.active === id ? closeTray() : focusPanel(id)}>{panels.find(p => p.id === id)?.title}</button>
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
  .workspace-operational-row { display:flex; align-items:center; flex:none; min-width:0; border-bottom:1px solid var(--border); background:var(--surface); }
  .core-toolbar { flex:1 1 auto; min-width:0; }
  .edition-health { flex:0 0 auto; min-width:0; padding:0 .55rem; }
  .edition-health :global(.price-status) { margin:0; }
  .storage-warning { margin:0; padding:.3rem .7rem; font-size:.7rem; color:var(--warning); background:var(--surface-2); }
  .edition-health { padding:0 .7rem; }
  .workspace-dock { flex:1 1 0; min-height:0; min-width:0; overflow:hidden; }
  .dock-area { position:relative; display:flex; flex:1 1 0; min-height:0; overflow:hidden; }
  .tray-peek { position:absolute; min-height:0; min-width:0; display:flex; flex-direction:column; background:var(--surface); border:1px solid var(--primary); z-index:5; }
  .tray-peek.bottom { inset:auto 0 0; max-height:90%; box-shadow:0 -8px 28px #0005; }
  .tray-peek.left { inset:0 auto 0 30px; max-width:90%; box-shadow:8px 0 28px #0005; }
  .tray-peek.right { inset:0 30px 0 auto; max-width:90%; box-shadow:-8px 0 28px #0005; }
  .tray-heading { display:flex; align-items:center; gap:.5rem; padding:.25rem .6rem; flex:none; background:var(--surface-2); font-size:.75rem; }
  .tray-heading span { flex:1; color:var(--text-2); font-size:.65rem; }
  .tray-content { flex:1; min-height:0; overflow:hidden; }
  .tray-content :global(.workstation-panel) { height:100%; overflow:auto; padding:.65rem; box-sizing:border-box; }
  .tray-content :global(.mobile-panel-title) { display:none; }
  .tray-content :global(.workstation-panel:focus-visible) { outline:2px solid var(--primary); outline-offset:-2px; }
  .tray-content :global([data-workspace-panel='watchlist'] > div) { height:100%; min-height:0; }
  .tray-content :global([data-workspace-panel='watchlist'] .watchlist) { height:100%; min-height:320px; contain:size; border:0; padding:0; }
  .tray-content :global([data-workspace-panel='watchlist'] .filter-row) { flex-wrap:wrap; }
  .tray-content :global([data-workspace-panel='watchlist'] .search-row) { flex-basis:100%; }
  .tray-strip { flex:none; flex-wrap:nowrap; gap:0; background:var(--surface-2); }
  .tray-strip.bottom { overflow-x:auto; border-top:1px solid var(--border-md); }
  .tray-strip.side { width:30px; overflow-y:auto; overflow-x:hidden; flex-direction:column; align-items:stretch; border-color:var(--border-md); }
  .tray-strip.side.left { border-right:1px solid var(--border-md); }
  .tray-strip.side.right { border-left:1px solid var(--border-md); }
  .tray-strip.side button { min-width:29px; min-height:72px; padding:.5rem .25rem; writing-mode:vertical-rl; text-orientation:mixed; justify-content:flex-start; }
  .tray-strip.side.left button { transform:rotate(180deg); }
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
  @media (max-width:900px) {
    .workspace-operational-row { align-items:flex-start; flex-wrap:wrap; }
    .core-toolbar { flex-basis:100%; }
    .edition-health { padding-bottom:.25rem; }
  }
</style>
