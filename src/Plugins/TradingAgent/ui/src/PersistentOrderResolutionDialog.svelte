<script lang="ts">
  import { onDestroy, tick } from 'svelte';
  import { validateAttentionResolution, type AttentionResolution } from './persistentOrderUi';
  interface ResolutionTarget { intentId:string; symbol:string; action:string; quantity:number; lastAttemptSessionDate:string|null }
  let dialog: HTMLDialogElement;
  let back: HTMLButtonElement;
  let confirm: HTMLButtonElement;
  let target: ResolutionTarget = {intentId:'',symbol:'',action:'',quantity:0,lastAttemptSessionDate:null};
  let resolution = '';
  let quantityText: string | number = '';
  let note = '';
  let error = '';
  let resolve: ((value: AttentionResolution | null) => void) | null = null;
  let previous: HTMLElement | null = null;

  export async function ask(next: ResolutionTarget): Promise<AttentionResolution | null> {
    if (resolve) return null;
    target = {...next}; resolution = ''; quantityText = ''; note = ''; error = '';
    previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const answer = new Promise<AttentionResolution | null>(done => resolve = done);
    dialog.showModal(); await tick(); back.focus(); return answer;
  }
  function finish(value: AttentionResolution | null) {
    const done = resolve; resolve = null; dialog.close();
    if (previous?.isConnected) previous.focus(); done?.(value);
  }
  function submit() {
    const result = validateAttentionResolution(resolution,quantityText,note,target.quantity);
    error = result.error ?? '';
    if (result.value) finish(result.value);
    else void tick().then(() => {
      const invalid = dialog.querySelector<HTMLElement>('[aria-invalid="true"]');
      const firstChoice = dialog.querySelector<HTMLInputElement>('input[type="radio"]');
      (invalid ?? firstChoice)?.focus();
    });
  }
  function key(event: KeyboardEvent) {
    if (event.key === 'Enter' && (event.target !== confirm || event.repeat || event.isComposing)) event.preventDefault();
  }
  onDestroy(() => resolve?.(null));
</script>

<dialog bind:this={dialog} aria-label="Resolve persistent order from broker check"
  on:cancel={event => {event.preventDefault();finish(null);}} on:keydown={key}>
  <h2>Resolve from broker check</h2>
  <p><b>{target.symbol} {target.action}</b> · intent <code>{target.intentId}</code></p>
  <p class="explain">AgentFox cannot retrieve prior-day broker history. Check the broker order book, activity, or statement for {target.lastAttemptSessionDate ?? 'the prior attempt date'}, then record exactly what it shows.</p>
  <fieldset class:error={!!error && !resolution}>
    <legend>Broker result</legend>
    <label><input type="radio" bind:group={resolution} value="not_filled"/> Not filled — resume daily retries</label>
    <label><input type="radio" bind:group={resolution} value="partial"/> Partially filled</label>
    <label><input type="radio" bind:group={resolution} value="filled"/> Fully filled</label>
  </fieldset>
  {#if resolution === 'partial'}
    <label class="field">Filled quantity <input type="number" min="1" max={target.quantity - 1} step="1" bind:value={quantityText} aria-invalid={!!error && error.startsWith('Enter')}/><small>Whole shares, 1–{target.quantity - 1} of {target.quantity}.</small></label>
  {/if}
  <label class="field">Evidence checked <textarea rows="3" bind:value={note} aria-invalid={!!error && error.startsWith('Describe')} placeholder="For example: broker activity statement for 2026-09-02"></textarea><small>Required. This note becomes part of the audit record.</small></label>
  {#if error}<p class="validation" role="alert">{error}</p>{/if}
  <p class="warning">This records your broker-side finding. Review the intent ID and evidence before confirming.</p>
  <footer><button bind:this={back} on:click={() => finish(null)}>Back — no change</button><button class="confirm" bind:this={confirm} on:click={submit}>Confirm resolution</button></footer>
</dialog>

<style>
  dialog { width:min(560px,calc(100vw - 2rem)); max-height:85dvh; overflow:auto; padding:1rem; border:1px solid var(--border-md); border-radius:8px; background:var(--surface); color:var(--text); }
  dialog::backdrop { background:#0009; } h2 { margin:0 0 .55rem; font-size:1rem; } p { margin:.4rem 0; font-size:.78rem; overflow-wrap:anywhere; } code { color:var(--primary); }
  .explain,.warning,small { color:var(--text-2); line-height:1.45; }.warning { border-left:3px solid var(--warning); padding:.45rem .6rem; }
  fieldset,.field { margin:.75rem 0; padding:.65rem; border:1px solid var(--border); border-radius:6px; display:flex; flex-direction:column; gap:.45rem; font-size:.75rem; }
  fieldset.error { border-color:var(--danger); } legend { color:var(--text-2); } fieldset label { display:flex; align-items:center; gap:.4rem; }
  input[type="number"],textarea { box-sizing:border-box; width:100%; padding:.45rem; border:1px solid var(--border-md); border-radius:4px; background:var(--surface-2); color:var(--text); font:inherit; }
  [aria-invalid="true"] { border-color:var(--danger)!important; }.validation { color:var(--danger); }
  footer { display:flex; justify-content:flex-end; flex-wrap:wrap; gap:.6rem; margin-top:1rem; } button { padding:.5rem .75rem; border:1px solid var(--border-md); border-radius:4px; background:var(--surface-2); color:var(--text); cursor:pointer; } button.confirm { border-color:var(--primary); }
  button:focus-visible,input:focus-visible,textarea:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
</style>
