<script lang="ts">
  import { createEventDispatcher, onMount } from 'svelte';
  import {
    trading, TRIGGER_KINDS, ALERT_KINDS,
    type ArmOrderRequest, type TriggerKind
  } from './api';
  import { Crosshair, X, AlertTriangle, Zap } from 'lucide-svelte';

  /**
   * Pre-filled context from wherever the user clicked — a chart level or an alert. Everything stays
   * editable: the point of pre-filling is to save typing, not to hide what is being armed.
   */
  export let symbol: string;
  export let triggerKind: TriggerKind = 'PriceBelow';
  export let triggerPrice: number | null = null;
  export let triggerAlertKind: string | null = null;
  export let action: 'BUY' | 'SELL' = 'SELL';
  export let orderType = 'STOPLOSS';
  export let price: number | null = null;
  export let limitPrice: number | null = null;
  export let sourceAlertId: string | null = null;
  /** Shown so the user can see what the level actually is before committing size to it. */
  export let context: string | null = null;

  const dispatch = createEventDispatcher<{ armed: void; close: void }>();

  let quantity: number | null = null;
  let expiresInDays = 30;
  let note = '';
  let busy = false;
  let error: string | null = null;
  let result: {
    willFireUnattended: boolean;
    note: string;
    attachedStop: { stopTrigger: number; stopLimit: number; recurring: boolean; note: string } | null;
  } | null = null;
  let dialogElement: HTMLDivElement;

  // ── Attached protective stop (BUY only) ──────────────────────────────────
  let attachStop = false;
  let stopTrigger: number | null = null;
  let stopLimit: number | null = null;
  let stopRecurring = true;

  onMount(() => dialogElement.focus());

  function closeOnBackdrop(event: MouseEvent) {
    if (event.target === event.currentTarget) dispatch('close');
  }

  function closeOnEscape(event: KeyboardEvent) {
    if (event.key === 'Escape') dispatch('close');
  }

  $: isEvent = triggerKind === 'Event';
  $: isStop = orderType === 'STOPLOSS';

  // A stop's limit defaults just past its trigger, mirroring the broker-side default: a stop limit set
  // exactly AT the trigger often misses the move that triggered it.
  $: if (isStop && price != null && limitPrice == null) {
    limitPrice = Number((action === 'SELL' ? price * 0.99 : price * 1.01).toFixed(2));
  }

  $: estimatedValue = (quantity ?? 0) * (price ?? triggerPrice ?? 0);

  // A stop only makes sense on a BUY — it protects the position this entry creates.
  $: canAttachStop = action === 'BUY';
  $: if (!canAttachStop) attachStop = false;

  $: entryPrice = price ?? triggerPrice ?? null;

  // Default the stop 2% under the entry, and its limit 1% under the trigger. Both are starting
  // points to edit, not recommendations — the level worth stopping at is a judgement about the
  // chart, which this dialog does not have.
  $: if (attachStop && stopTrigger == null && entryPrice != null) {
    stopTrigger = Number((entryPrice * 0.98).toFixed(2));
  }
  $: if (attachStop && stopTrigger != null && stopLimit == null) {
    stopLimit = Number((stopTrigger * 0.99).toFixed(2));
  }

  $: stopRisk = attachStop && entryPrice != null && stopTrigger != null && quantity
    ? (entryPrice - stopTrigger) * quantity
    : null;

  $: stopError =
    !attachStop ? null
    : stopTrigger == null || stopTrigger <= 0 ? 'Enter a stop trigger price.'
    : entryPrice != null && stopTrigger >= entryPrice
      ? `A stop at ${stopTrigger} sits at or above the entry (${entryPrice}), so it would trigger `
        + 'immediately rather than protect anything.'
    : stopLimit != null && stopLimit > stopTrigger
      ? `The stop limit (${stopLimit}) must be at or below the trigger (${stopTrigger}), or it `
        + 'cannot fill once triggered.'
    : null;

  async function submit() {
    if (busy) return;
    error = null;

    if (!quantity || quantity <= 0) { error = 'Quantity must be a positive number.'; return; }
    if (isEvent && !triggerAlertKind) { error = 'Choose the event to trigger on.'; return; }
    if (!isEvent && !(triggerPrice && triggerPrice > 0)) { error = 'Enter a trigger price.'; return; }
    if (stopError) { error = stopError; return; }

    const request: ArmOrderRequest = {
      symbol, action, quantity, triggerKind,
      triggerPrice: isEvent ? null : triggerPrice,
      triggerAlertKind: isEvent ? triggerAlertKind : null,
      orderType,
      price: price ?? triggerPrice,
      limitPrice: isStop ? limitPrice : null,
      expiresInDays,
      note: note.trim() || undefined,
      sourceAlertId,
      attachStop: attachStop && stopTrigger
        ? { stopTrigger, stopLimit, recurring: stopRecurring }
        : null
    };

    busy = true;
    try {
      const created = await trading.armed.arm(request);
      // Held on screen rather than closed immediately: whether this will fire unattended is the single
      // most important thing to read, and closing the dialog would hide it.
      result = {
        willFireUnattended: created.willFireUnattended,
        note: created.note,
        attachedStop: created.attachedStop
      };
      dispatch('armed');
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }
</script>

<div class="backdrop" on:click={closeOnBackdrop} role="presentation">
  <div
    class="dialog"
    bind:this={dialogElement}
    role="dialog"
    aria-modal="true"
    aria-label="Arm an order"
    tabindex="-1"
    on:keydown={closeOnEscape}
  >
    <header>
      <div class="title"><Crosshair size={15} /> <b>Arm an order</b> <span>{symbol}</span></div>
      <button class="icon" on:click={() => dispatch('close')} aria-label="Close"><X size={14} /></button>
    </header>

    {#if context}<p class="context">{context}</p>{/if}

    {#if result}
      <!-- Outcome view: the fire-unattended answer is the payload, not a footnote. -->
      <div class="outcome" class:live={result.willFireUnattended}>
        {#if result.willFireUnattended}
          <b><Zap size={13} /> Armed — this WILL send without asking</b>
        {:else}
          <b><AlertTriangle size={13} /> Armed — but it will NOT send on its own</b>
        {/if}
        <p>{result.note}</p>
      </div>
      {#if result.attachedStop}
        <div class="outcome">
          <b>Stop attached at {result.attachedStop.stopTrigger} (limit {result.attachedStop.stopLimit})</b>
          <p>{result.attachedStop.note}</p>
        </div>
      {/if}
      <div class="actions">
        <button class="btn btn-primary" on:click={() => dispatch('close')}>Done</button>
      </div>
    {:else}
      <div class="grid">
        <label>
          <span>Trigger</span>
          <select bind:value={triggerKind}>
            {#each TRIGGER_KINDS as kind}
              <option value={kind}>
                {kind === 'PriceBelow' ? 'Price falls to / below'
                 : kind === 'PriceAbove' ? 'Price rises to / above'
                 : 'An event fires'}
              </option>
            {/each}
          </select>
        </label>

        {#if isEvent}
          <label>
            <span>Event</span>
            <select bind:value={triggerAlertKind}>
              <option value={null}>— choose —</option>
              {#each ALERT_KINDS as kind}
                <option value={kind}>{kind.replace(/([a-z])([A-Z])/g, '$1 $2')}</option>
              {/each}
            </select>
          </label>
        {:else}
          <label>
            <span>Trigger price</span>
            <input type="number" step="0.01" bind:value={triggerPrice} />
          </label>
        {/if}

        <label>
          <span>Side</span>
          <select bind:value={action}>
            <option value="SELL">SELL</option>
            <option value="BUY">BUY</option>
          </select>
        </label>

        <label>
          <span>Quantity</span>
          <input type="number" min="1" bind:value={quantity} placeholder="shares" />
        </label>

        <label>
          <span>Order type</span>
          <select bind:value={orderType}>
            <option value="STOPLOSS">Stop Loss (trigger + limit)</option>
            <option value="LIMIT">Limit</option>
            <option value="MARKET">Market</option>
          </select>
        </label>

        <label>
          <span>{isStop ? 'Stop trigger' : 'Order price'}</span>
          <input type="number" step="0.01" bind:value={price} />
        </label>

        {#if isStop}
          <label>
            <span>Stop limit</span>
            <input type="number" step="0.01" bind:value={limitPrice} />
          </label>
        {/if}

        <label>
          <span>Expires in (days)</span>
          <input type="number" min="1" max="365" bind:value={expiresInDays} />
        </label>
      </div>

      {#if canAttachStop}
        <!-- The stop is deliberately part of arming the entry rather than a separate action: the
             moment you decide a size is the moment you know what losing on it costs. -->
        <div class="attach" class:on={attachStop}>
          <label class="check">
            <input type="checkbox" bind:checked={attachStop} />
            <span>Protect this with a stop once it fills</span>
          </label>

          {#if attachStop}
            <div class="grid">
              <label>
                <span>Stop trigger</span>
                <input type="number" step="0.01" bind:value={stopTrigger} />
              </label>
              <label>
                <span>Stop limit</span>
                <input type="number" step="0.01" bind:value={stopLimit} />
              </label>
            </div>

            <label class="check">
              <input type="checkbox" bind:checked={stopRecurring} />
              <span>Re-place it every session</span>
            </label>

            {#if stopRisk != null && stopRisk > 0}
              <p class="estimate">
                Risk if the stop fills <b>{Math.round(stopRisk).toLocaleString()} PKR</b>
              </p>
            {/if}

            <p class="stop-note">
              The stop stays dormant until your holdings actually rise, which is how the fill is
              confirmed — nothing is sold on the assumption that the entry went through.
              {#if stopRecurring}
                It is then re-placed at the broker each session, because outstanding orders are
                cleared at the close.
              {:else}
                <b>It is placed once.</b> Outstanding orders are cleared at the close, so the position
                stops being protected the next day.
              {/if}
            </p>

            {#if stopError}<p class="error">{stopError}</p>{/if}
          {/if}
        </div>
      {/if}

      <label class="full">
        <span>Note (optional)</span>
        <input type="text" bind:value={note} placeholder="why this level" maxlength="120" />
      </label>

      {#if estimatedValue > 0}
        <p class="estimate">Approximate order value <b>{estimatedValue.toLocaleString()} PKR</b></p>
      {/if}

      <p class="caveat">
        <AlertTriangle size={12} />
        An armed order is evaluated by the monitor, so it only fires while AgentFox is running and the
        market is open. A native broker stop has neither limitation.
      </p>

      {#if error}<p class="error">{error}</p>{/if}

      <div class="actions">
        <button class="btn btn-ghost" on:click={() => dispatch('close')} disabled={busy}>Cancel</button>
        <button class="btn btn-primary" on:click={submit} disabled={busy}>
          {busy ? 'Arming…' : 'Arm order'}
        </button>
      </div>
    {/if}
  </div>
</div>

<style>
  .backdrop {
    position: fixed; inset: 0; background: rgba(0,0,0,.55); z-index: 200;
    display: flex; align-items: center; justify-content: center; padding: 1rem;
  }
  .dialog {
    background: var(--surface); border: 1px solid var(--border-md); border-radius: var(--radius);
    padding: 1rem; width: min(560px, 100%); max-height: 90vh; overflow-y: auto;
    display: flex; flex-direction: column; gap: .7rem;
  }
  header { display: flex; justify-content: space-between; align-items: center; gap: 1rem; }
  .title { display: flex; align-items: center; gap: .45rem; color: var(--primary); }
  .title b { color: var(--text); font-size: .9rem; }
  .title span { color: var(--text-2); font-family: ui-monospace, monospace; font-size: .82rem; }
  .icon { background: none; border: 0; color: var(--text-3); cursor: pointer; padding: .25rem; }
  .icon:hover { color: var(--text); }

  .context { margin: 0; color: var(--text-2); font-size: .74rem; background: var(--surface-2);
             padding: .45rem .6rem; border-radius: var(--radius-sm); line-height: 1.5; }

  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: .6rem; }
  label { display: flex; flex-direction: column; gap: .25rem; }
  label.full { width: 100%; }
  label span { color: var(--text-3); font-size: .66rem; text-transform: uppercase; letter-spacing: .04em; }
  input, select {
    background: var(--surface-2); border: 1px solid var(--border-md); border-radius: var(--radius-sm);
    padding: .4rem .5rem; color: var(--text); font: inherit; font-size: .8rem; width: 100%;
  }
  input:focus, select:focus { outline: none; border-color: var(--primary); }

  .estimate { margin: 0; color: var(--text-2); font-size: .74rem; }
  .estimate b { color: var(--text); }

  .attach {
    border: 1px solid var(--border-md); border-radius: var(--radius-sm);
    padding: .6rem .7rem; display: flex; flex-direction: column; gap: .55rem;
  }
  .attach.on { border-left: 3px solid var(--success); }
  .check { flex-direction: row; align-items: center; gap: .45rem; cursor: pointer; }
  .check input { width: auto; }
  .check span { color: var(--text); font-size: .78rem; text-transform: none; letter-spacing: normal; }
  .stop-note { margin: 0; color: var(--text-3); font-size: .7rem; line-height: 1.55; }
  .stop-note b { color: var(--warning); }
  .caveat { margin: 0; color: var(--warning); font-size: .68rem; display: flex; gap: .35rem;
            align-items: flex-start; line-height: 1.5; }
  .error { margin: 0; color: var(--danger); font-size: .75rem; }

  .outcome {
    border: 1px solid var(--border-md); border-left-width: 3px; border-left-color: var(--text-3);
    border-radius: var(--radius-sm); padding: .6rem .7rem; display: flex; flex-direction: column; gap: .3rem;
    background: var(--surface-2);
  }
  .outcome.live { border-left-color: var(--warning); }
  .outcome b { display: flex; align-items: center; gap: .35rem; font-size: .8rem; color: var(--text); }
  .outcome.live b { color: var(--warning); }
  .outcome p { margin: 0; color: var(--text-2); font-size: .74rem; line-height: 1.55; }

  .actions { display: flex; justify-content: flex-end; gap: .5rem; }
</style>
