<script lang="ts">
  import { onDestroy, tick } from 'svelte';
  let dialog: HTMLDialogElement;
  let back: HTMLButtonElement;
  let message = '';
  let confirmLabel = 'Confirm action';
  let resolve: ((accepted: boolean) => void) | null = null;
  let previous: HTMLElement | null = null;
  export async function ask(text: string, nextConfirmLabel = 'Confirm action'): Promise<boolean> {
    if (resolve) return false;
    message = text;
    confirmLabel = nextConfirmLabel;
    previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const answer = new Promise<boolean>(done => resolve = done);
    dialog.showModal(); await tick(); back.focus();
    return answer;
  }
  function finish(accepted: boolean) {
    const done = resolve; resolve = null;
    dialog.close();
    if (previous?.isConnected) previous.focus();
    done?.(accepted);
  }
  onDestroy(() => resolve?.(false));
</script>
<dialog bind:this={dialog} aria-label="Review order management action" on:cancel={event => {event.preventDefault();finish(false);}}
  on:keydown={event => {if ((event.key === 'Enter' || event.key === ' ') && (event.repeat || event.ctrlKey || event.metaKey)) event.preventDefault();}}>
  <h2>Review order management action</h2>
  <p>{message}</p>
  <small>Review the exact target before confirming. Closing this review makes no change.</small>
  <footer><button bind:this={back} on:click={() => finish(false)}>Back — no change</button><button on:click={() => finish(true)}>{confirmLabel}</button></footer>
</dialog>
<style>
  dialog { width:min(530px,calc(100vw - 2rem)); max-height:80dvh; overflow:auto; padding:1rem; border:1px solid var(--border-md); border-radius:8px; background:var(--surface); color:var(--text); }
  dialog::backdrop { background:#0009; } h2 { font-size:1rem; margin-top:0; } p { white-space:pre-wrap; overflow-wrap:anywhere; font-size:.85rem; line-height:1.5; } small { color:var(--text-2); }
  footer { display:flex; justify-content:flex-end; flex-wrap:wrap; gap:.6rem; margin-top:1rem; } button { padding:.5rem .75rem; border:1px solid var(--border-md); border-radius:4px; background:var(--surface-2); color:var(--text); cursor:pointer; } button:focus-visible { outline:2px solid var(--primary); }
</style>
