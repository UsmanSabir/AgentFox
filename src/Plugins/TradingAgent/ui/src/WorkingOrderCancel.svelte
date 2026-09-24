<script lang="ts">
  /**
   * Cancel ONE order from the portfolio's working-orders table.
   *
   * Two confirmations at most, and they ask different questions. The first is this button's own:
   * "this order?". The second only appears when the SERVER says the order is managed — a protective
   * stop or a keep-working order — and names what cancelling it also stops doing. A plain order never
   * sees the second, so it is not a box people learn to click through.
   *
   * The result is reported as what is now true. "No longer resting, may have filled" is its own
   * sentence, because reading it as "cancelled" is how a real position ends up unprotected.
   *
   * Replaceable through `symbolExtensions.orderAction`; the props below are that contract.
   */
  import { createEventDispatcher } from 'svelte';
  import { ApiError, trading, type WorkingOrderOwner } from './api';

  export let symbol: string;
  export let orderNo: string;
  export let side: string | null | undefined = null;
  export let orderType: string | null | undefined = null;
  export let remainingQuantity: number | null | undefined = null;
  export let price: number | null | undefined = null;
  export let showValues = false;
  export let status: string | null | undefined = null;
  export let currency: string | null | undefined = null;

  const dispatch = createEventDispatcher<{ changed: void }>();

  type Phase = 'idle' | 'confirming' | 'busy' | 'managed' | 'done';
  let phase: Phase = 'idle';
  let owners: WorkingOrderOwner[] = [];
  let message = '';
  let tone: 'good' | 'warn' | 'bad' = 'good';

  // Quantity and price obey the panel's privacy toggle, like every other value in that table.
  $: described = [
    side, orderType,
    showValues && remainingQuantity != null ? remainingQuantity : null,
    symbol,
    showValues && price != null ? `@ ${currency ?? 'Rs'} ${price}` : null,
    status ? `(${status})` : null
  ].filter(v => v != null && v !== '').join(' ');

  async function cancel(acknowledgeManaged: boolean) {
    phase = 'busy';
    try {
      const result = await trading.cancelWorkingOrder({ orderNo, symbol, acknowledgeManaged });
      message = result.message;
      tone = result.outcome === 'cancelled' || result.outcome === 'fulfilled' ? 'good'
        : result.gone || result.outcome === 'not_resting' ? 'warn' : 'bad';
      phase = 'done';
      if (result.gone || result.outcome === 'not_resting') dispatch('changed');
    } catch (e) {
      if (e instanceof ApiError && e.code === 'needs_acknowledgement') {
        owners = (e.detail?.owners as WorkingOrderOwner[] | undefined) ?? [];
        message = e.message;
        phase = 'managed';
        return;
      }
      message = e instanceof Error ? e.message : String(e);
      tone = 'bad';
      phase = 'done';
    }
  }

  function reset() {
    phase = 'idle';
    owners = [];
    message = '';
  }
</script>

<div class="order-cancel">
  {#if phase === 'idle'}
    <button class="cancel" on:click={() => phase = 'confirming'} aria-label={`Cancel order ${orderNo} on ${symbol}`}>Cancel</button>
  {:else if phase === 'confirming'}
    <span class="prompt">Cancel {described}?</span>
    <span class="row">
      <button class="cancel strong" on:click={() => cancel(false)}>Yes, cancel</button>
      <button class="keep" on:click={reset}>Keep</button>
    </span>
  {:else if phase === 'busy'}
    <span class="prompt" aria-live="polite">Cancelling at the broker…</span>
  {:else if phase === 'managed'}
    <div class="managed" role="alert">
      {#if owners.length}
        {#each owners as owner}<p>{owner.consequence}</p>{/each}
      {:else}
        <p>{message}</p>
      {/if}
    </div>
    <span class="row">
      <button class="cancel strong" on:click={() => cancel(true)}>Cancel anyway</button>
      <button class="keep" on:click={reset}>Keep</button>
    </span>
  {:else}
    <p class="result {tone}" aria-live="polite">{message}</p>
    <button class="keep" on:click={reset}>OK</button>
  {/if}
</div>

<style>
  .order-cancel { display:flex; flex-direction:column; align-items:flex-start; gap:.3rem; max-width:18rem; }
  .row { display:flex; gap:.35rem; }
  .prompt { color:var(--text); font-size:.68rem; }
  button { min-height:1.8rem; padding:.25rem .55rem; border:1px solid var(--border-md); border-radius:var(--radius-sm); background:var(--surface-2); color:var(--text-2); font:inherit; font-weight:650; cursor:pointer; }
  button:hover { background:var(--surface); border-color:var(--border-hover); }
  button:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }
  .cancel { color:var(--danger); border-color:color-mix(in srgb,var(--danger) 38%,var(--border)); }
  .cancel.strong { background:color-mix(in srgb,var(--danger) 14%,var(--surface-2)); }
  .managed p, .result { margin:0; font-size:.66rem; line-height:1.35; }
  .managed p { color:var(--warning); }
  .result.good { color:var(--success); }
  .result.warn { color:var(--warning); }
  .result.bad { color:var(--danger); }
</style>
