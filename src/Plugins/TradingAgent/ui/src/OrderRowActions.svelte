<script lang="ts">
  import { onDestroy, tick } from 'svelte';
  import type { PersistentActionChoice, PersistentActionId } from './persistentOrderUi';
  let dialog: HTMLDialogElement;
  let actionList: HTMLDivElement;
  let title = '';
  let actions: PersistentActionChoice[] = [];
  let resolve: ((choice: PersistentActionId | null) => void) | null = null;
  let previous: HTMLElement | null = null;

  export async function ask(nextTitle: string, nextActions: PersistentActionChoice[]): Promise<PersistentActionId | null> {
    if (resolve || !nextActions.length) return null;
    title = nextTitle; actions = nextActions;
    previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const answer = new Promise<PersistentActionId | null>(done => resolve = done);
    dialog.showModal(); await tick();
    actionList.querySelector<HTMLButtonElement>('button[data-menu-action]')?.focus();
    return answer;
  }
  function finish(choice: PersistentActionId | null) {
    const done = resolve; resolve = null; dialog.close();
    if (previous?.isConnected) previous.focus();
    done?.(choice);
  }
  function key(event: KeyboardEvent) {
    if (event.repeat || event.isComposing) { event.preventDefault(); return; }
    if (!['ArrowDown','ArrowUp','Home','End'].includes(event.key)) return;
    const buttons = [...actionList.querySelectorAll<HTMLButtonElement>('button[data-menu-action]')];
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
      : event.key === 'ArrowDown' ? Math.min(buttons.length - 1,index + 1) : Math.max(0,index - 1);
    event.preventDefault(); buttons[next]?.focus();
  }
  onDestroy(() => resolve?.(null));
</script>

<dialog bind:this={dialog} aria-label={title} on:cancel={event => {event.preventDefault();finish(null);}} on:keydown={key}>
  <header><h2>{title}</h2><button on:click={() => finish(null)}>Close actions</button></header>
  <p>Selecting an action opens its existing workflow. It does not execute an order-management action directly.</p>
  <div class="action-list" bind:this={actionList} role="menu">
    {#each actions as action}
      <button data-menu-action role="menuitem" class:danger={action.danger} on:click={() => finish(action.id)}>
        <b>{action.label}</b><span>{action.description}</span>
      </button>
    {/each}
  </div>
</dialog>

<style>
  dialog { width:min(470px,calc(100vw - 2rem)); max-height:80dvh; overflow:auto; padding:1rem; border:1px solid var(--border-md); border-radius:8px; background:var(--surface); color:var(--text); }
  dialog::backdrop { background:#0009; } header { display:flex; justify-content:space-between; align-items:center; gap:1rem; } h2 { margin:0; font-size:1rem; } p { color:var(--text-2); font-size:.75rem; line-height:1.45; }
  button { border:1px solid var(--border-md); border-radius:5px; background:var(--surface-2); color:var(--text); cursor:pointer; }
  header button { padding:.4rem .6rem; white-space:nowrap; }
  .action-list { display:flex; flex-direction:column; gap:.4rem; }
  .action-list button { padding:.65rem .75rem; text-align:left; display:flex; flex-direction:column; gap:.18rem; }
  .action-list button:hover,.action-list button:focus-visible { border-color:var(--primary); background:var(--primary-dim); }
  .action-list button:focus-visible,header button:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .action-list b { font-size:.78rem; }.action-list span { color:var(--text-2); font-size:.68rem; line-height:1.35; }
  .action-list .danger b { color:var(--danger); }
</style>
