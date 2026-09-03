<script lang="ts">
  import { onMount } from 'svelte';

  export let label = 'Resizable workspace';
  export let leftLabel = 'Left panel';
  export let rightLabel = 'Right panel';
  export let storageKey = 'trading.workspace.split.v1';
  export let defaultLeft = 30;
  export let minLeft = 18;
  export let maxLeft = 48;
  export let compactLeft = false;
  export let expanded = false;

  const RETENTION_MS = 180 * 24 * 60 * 60 * 1000;

  let root: HTMLElement;
  let leftPercent = defaultLeft;
  let resizing = false;

  $: presets = [
    { label: 'Chart focus', value: Math.min(maxLeft, minLeft + 4) },
    { label: 'Balanced', value: Math.min(maxLeft, Math.max(minLeft, defaultLeft)) },
    { label: 'List focus', value: Math.max(minLeft, maxLeft - 4) }
  ];

  function clamp(value: number) {
    return Math.min(maxLeft, Math.max(minLeft, value));
  }

  function persist() {
    try {
      localStorage.setItem(storageKey, JSON.stringify({
        version: 1,
        leftPercent,
        savedAt: Date.now()
      }));
    } catch {
      // A blocked browser store must not make the workspace unusable.
    }
  }

  function setLeft(value: number, save = true) {
    leftPercent = clamp(value);
    if (save) persist();
  }

  function reset() {
    leftPercent = clamp(defaultLeft);
    try { localStorage.removeItem(storageKey); } catch { /* browser storage is optional */ }
  }

  function startResize(event: PointerEvent) {
    if (expanded || compactLeft) return;
    resizing = true;
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    event.preventDefault();
  }

  function resize(event: PointerEvent) {
    if (!resizing) return;
    const bounds = root.getBoundingClientRect();
    if (bounds.width <= 0) return;
    setLeft(((event.clientX - bounds.left) / bounds.width) * 100, false);
  }

  function finishResize(event: PointerEvent) {
    if (!resizing) return;
    resizing = false;
    const target = event.currentTarget as HTMLElement;
    if (target.hasPointerCapture(event.pointerId)) target.releasePointerCapture(event.pointerId);
    persist();
  }

  function resizeWithKeyboard(event: KeyboardEvent) {
    if (expanded || compactLeft) return;
    const step = event.shiftKey ? 5 : 2;
    let next: number | null = null;
    if (event.key === 'ArrowLeft') next = leftPercent - step;
    if (event.key === 'ArrowRight') next = leftPercent + step;
    if (event.key === 'Home') next = minLeft;
    if (event.key === 'End') next = maxLeft;
    if (next == null) return;
    event.preventDefault();
    setLeft(next);
  }

  onMount(() => {
    try {
      const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as {
        version?: number;
        leftPercent?: number;
        savedAt?: number;
      } | null;
      const age = typeof saved?.savedAt === 'number' ? Date.now() - saved.savedAt : Number.NaN;
      const fresh = saved?.version === 1
        && typeof saved.leftPercent === 'number'
        && Number.isFinite(age)
        && age >= 0
        && age <= RETENTION_MS;
      if (fresh) leftPercent = clamp(saved!.leftPercent!);
      else if (saved) localStorage.removeItem(storageKey);
    } catch {
      try { localStorage.removeItem(storageKey); } catch { /* browser storage is optional */ }
    }
  });
</script>

<section
  class="workspace-split"
  class:expanded
  class:compact-left={compactLeft}
  class:resizing
  aria-label={label}
  bind:this={root}
  style={`--workspace-left:${leftPercent}%`}
>
  <div class="workspace-controls">
    <span class="workspace-label">Layout</span>
    <div class="preset-group" aria-label={`${label} presets`}>
      {#each presets as preset}
        <button
          type="button"
          class:active={Math.abs(leftPercent - preset.value) < 0.5}
          aria-pressed={Math.abs(leftPercent - preset.value) < 0.5}
          on:click={() => setLeft(preset.value)}
        >{preset.label}</button>
      {/each}
      <button type="button" class="reset" on:click={reset}>Reset</button>
    </div>
  </div>

  <div class="workspace-grid">
    <div class="pane left-pane" aria-label={leftLabel}>
      <slot name="left"></slot>
    </div>

    <!-- An adjustable ARIA separator is keyboard-focusable by definition; Svelte's static checker
         does not distinguish it from a passive separator. -->
    <!-- svelte-ignore a11y_no_noninteractive_tabindex a11y_no_noninteractive_element_interactions -->
    <div
      class="splitter"
      role="separator"
      aria-label={`Resize ${leftLabel} and ${rightLabel}`}
      aria-orientation="vertical"
      aria-valuemin={minLeft}
      aria-valuemax={maxLeft}
      aria-valuenow={Math.round(leftPercent)}
      aria-valuetext={`${Math.round(leftPercent)} percent for ${leftLabel}`}
      tabindex="0"
      title="Drag to resize. Arrow keys resize by 2%; hold Shift for 5%."
      on:pointerdown={startResize}
      on:pointermove={resize}
      on:pointerup={finishResize}
      on:pointercancel={finishResize}
      on:keydown={resizeWithKeyboard}
    ></div>

    <div class="pane right-pane" aria-label={rightLabel}>
      <slot name="right"></slot>
    </div>
  </div>
</section>

<style>
  .workspace-split { min-width: 0; margin-bottom: .75rem; }
  .workspace-split.resizing { user-select: none; }
  .workspace-controls {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: .55rem;
    min-height: 2.15rem;
    margin-bottom: .45rem;
  }
  .workspace-label {
    color: var(--text-3);
    font-size: .61rem;
    font-weight: 750;
    letter-spacing: .08em;
    text-transform: uppercase;
  }
  .preset-group {
    display: inline-flex;
    align-items: center;
    padding: .18rem;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--surface);
  }
  .preset-group button {
    min-height: 1.75rem;
    padding: .28rem .55rem;
    border: 0;
    border-radius: 5px;
    background: transparent;
    color: var(--text-3);
    font: inherit;
    font-size: .65rem;
    font-weight: 650;
    cursor: pointer;
    transition: color .15s ease, background-color .15s ease;
  }
  .preset-group button:hover { color: var(--text); background: var(--surface-2); }
  .preset-group button.active {
    color: var(--primary);
    background: var(--primary-dim);
  }
  .preset-group button.reset { margin-left: .15rem; border-left: 1px solid var(--border); border-radius: 0 5px 5px 0; }
  .preset-group button:focus-visible,
  .splitter:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }

  .workspace-grid {
    display: grid;
    grid-template-columns: minmax(0, var(--workspace-left)) .7rem minmax(0, 1fr);
    min-height: 22rem;
    align-items: stretch;
  }
  .pane { min-width: 0; }
  /* WatchlistPanel deliberately uses size containment so a long list cannot size this grid row.
     The pane wrapper must therefore pass the chart-established row height through to its child. */
  .left-pane { display: grid; min-height: 0; }
  .splitter {
    position: relative;
    z-index: 2;
    min-width: .7rem;
    padding: 0;
    border: 0;
    background: transparent;
    cursor: col-resize;
    touch-action: none;
  }
  .splitter::before {
    content: '';
    position: absolute;
    inset: .65rem auto .65rem 50%;
    width: 2px;
    border-radius: 999px;
    background: var(--border-md);
    transform: translateX(-50%);
    transition: width .15s ease, background-color .15s ease;
  }
  .splitter:hover::before,
  .splitter:focus-visible::before,
  .resizing .splitter::before { width: 4px; background: var(--primary); }

  .compact-left .workspace-grid { grid-template-columns: minmax(100px, 116px) .7rem minmax(0, 1fr); }
  .compact-left .workspace-controls { display: none; }

  .expanded .workspace-controls,
  .expanded .splitter { display: none; }
  .expanded .workspace-grid { grid-template-columns: minmax(0, 1fr); gap: .75rem; }
  .expanded .left-pane { order: 2; }
  .expanded .right-pane { order: 1; }
  .expanded .left-pane :global(> section) { height: auto; contain: none; overflow: visible; }
  .expanded .left-pane :global(.rows) { flex: none; max-height: min(52vh, 420px); }

  @media (max-width: 900px) {
    .workspace-controls, .splitter { display: none; }
    .workspace-grid,
    .compact-left .workspace-grid { grid-template-columns: minmax(0, 1fr); gap: .75rem; min-height: 0; }
  }

  @media (max-width: 640px) {
    .workspace-grid { gap: .6rem; }
  }

  @media (prefers-reduced-motion: reduce) {
    .preset-group button, .splitter::before { transition: none; }
  }
</style>
