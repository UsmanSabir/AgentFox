<script lang="ts">
  import { createEventDispatcher, onMount } from 'svelte';
  import {
    trading, TRIGGER_KINDS, ALERT_KINDS, PERCENT_PRESETS,
    isPercentTrigger, percentTriggerLevel,
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

  // ── Percent trigger ("fire if it drops 3%") ──────────────────────────────
  // The move, and the price it is measured from. `referencePrice` is pre-filled by the caller with
  // whatever price it was showing, so the level quoted below is the level actually armed.
  export let triggerPercent: number | null = null;
  export let referencePrice: number | null = null;
  /**
   * Off by default, so the plain reading of "if it drops 3%" — 3% from today's price — is what an
   * unattended default does. Callers that mean a trailing stop say so, and the checkbox explains the
   * difference rather than assuming which one was wanted.
   */
  export let trailing = false;
  /** Live price, for the already-passed warning only. Never submitted. */
  export let currentPrice: number | null = null;
  /**
   * Limit for both sides by default. A pre-filled STOPLOSS made the type depend on where the dialog
   * was opened from, so the same click produced a different order kind on a chart level than on an
   * alert — and the stop variant needs a second price that is easy to leave at its guessed default.
   * Limit is the type that carries exactly what the user typed.
   */
  export let orderType = 'LIMIT';
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
  // Exported so a caller with a stop already in hand — the chart's plan gives an entry AND the level
  // that invalidates it — can arm both in one click instead of leaving the user to retype a number
  // that was already on screen. Left at these defaults, the dialog behaves exactly as before.
  export let attachStop = false;
  export let stopTrigger: number | null = null;
  export let stopLimit: number | null = null;
  export let stopRecurring = true;

  onMount(() => dialogElement.focus());

  function closeOnBackdrop(event: MouseEvent) {
    if (event.target === event.currentTarget) dispatch('close');
  }

  function closeOnEscape(event: KeyboardEvent) {
    if (event.key === 'Escape') dispatch('close');
  }

  $: isEvent = triggerKind === 'Event';
  $: isPercent = isPercentTrigger(triggerKind);
  $: isStop = orderType === 'STOPLOSS';
  $: fallsToTrigger = triggerKind === 'PriceBelow' || triggerKind === 'PercentDrop';

  // Picking the percent kind without a size of move would leave the level blank; 3% is a starting
  // point to change, not a recommendation.
  $: if (isPercent && triggerPercent == null) triggerPercent = 3;

  /**
   * The level the order fires at, whichever way it was specified. Everything downstream — the
   * sentence, the estimate, the stop defaults, the already-passed check — reads this rather than
   * branching on the trigger kind again.
   */
  $: level = isPercent
    ? percentTriggerLevel(triggerKind, referencePrice, triggerPercent)
    : triggerPrice;

  /**
   * The trigger has already been passed, so this would fire on the next monitoring pass rather than
   * wait for anything. Worth saying out loud: it is occasionally what someone wants ("get me out, it
   * is already falling") and much more often a number typed on the wrong side of the price.
   */
  $: alreadyPassed = !isEvent && level != null && currentPrice != null && currentPrice > 0
    && (fallsToTrigger ? currentPrice <= level : currentPrice >= level);

  // A stop's limit defaults just past its trigger, mirroring the broker-side default: a stop limit set
  // exactly AT the trigger often misses the move that triggered it.
  $: if (isStop && price != null && limitPrice == null) {
    limitPrice = Number((action === 'SELL' ? price * 0.99 : price * 1.01).toFixed(2));
  }

  // A percent trigger has no level to type, so a LIMIT would otherwise go in with no price at all.
  // Defaulted to the level, which is where the order was meant to sit. It does NOT follow a trailing
  // reference afterwards — see the note the template shows when both are on.
  $: if (isPercent && !isEvent && orderType !== 'MARKET' && price == null && level != null) {
    price = level;
  }

  $: estimatedValue = (quantity ?? 0) * (price ?? level ?? 0);

  // A stop only makes sense on a BUY — it protects the position this entry creates.
  $: canAttachStop = action === 'BUY';
  $: if (!canAttachStop) attachStop = false;

  $: entryPrice = price ?? level ?? null;

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

  const money = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 2 });

  /**
   * The whole order as one sentence, rebuilt as the form is edited.
   *
   * This is the part that makes the dialog usable without knowing the vocabulary. Every field above
   * is a number in a box; this says what those numbers will actually do, in the order it happens —
   * and it is the thing to read back before committing size to a level.
   */
  $: summary = (() => {
    if (!quantity || quantity <= 0) return null;
    const fill = orderType === 'MARKET'
      ? 'at the best price available'
      : price != null ? `at ${money(price)}` : 'with no price set';
    const verb = action === 'SELL' ? 'Sells' : 'Buys';

    if (isEvent) {
      return triggerAlertKind
        ? `${verb} ${quantity} ${symbol} ${fill} when ${symbol} raises `
          + `"${triggerAlertKind.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()}".`
        : null;
    }

    if (level == null) return null;
    const move = fallsToTrigger ? 'falls' : 'rises';

    if (!isPercent) return `${verb} ${quantity} ${symbol} ${fill} if it ${move} to ${money(level)}.`;

    const from = referencePrice != null ? money(referencePrice) : '—';
    return `${verb} ${quantity} ${symbol} ${fill} if it ${move} ${triggerPercent}% `
         + `from ${from} — that is ${money(level)}.`;
  })();

  async function submit() {
    if (busy) return;
    error = null;

    if (!quantity || quantity <= 0) { error = 'Quantity must be a positive number.'; return; }
    if (isEvent && !triggerAlertKind) { error = 'Choose the event to trigger on.'; return; }
    if (isPercent) {
      if (!(triggerPercent && triggerPercent > 0 && triggerPercent <= 50)) {
        error = 'Enter a move between 0 and 50%.'; return;
      }
      if (!(referencePrice && referencePrice > 0)) {
        error = 'Enter the price to measure the move from.'; return;
      }
    } else if (!isEvent && !(triggerPrice && triggerPrice > 0)) {
      error = 'Enter a trigger price.'; return;
    }
    if (stopError) { error = stopError; return; }

    const request: ArmOrderRequest = {
      symbol, action, quantity, triggerKind,
      triggerPrice: isEvent || isPercent ? null : triggerPrice,
      triggerAlertKind: isEvent ? triggerAlertKind : null,
      // Sent rather than left to the server to capture, so the level armed is the one quoted above.
      triggerPercent: isPercent ? triggerPercent : null,
      referencePrice: isPercent ? referencePrice : null,
      trailing: isPercent && trailing,
      orderType,
      price: orderType === 'MARKET' ? null : price ?? level,
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
      <!-- WHEN it fires, on its own, above the order itself. The trigger is the decision; the order
           is the consequence. Reading them the other way round is what made the percent triggers
           look like an extra field on a form rather than a different question. -->
      <div class="trigger-block">
        <label class="full">
          <span>Fire this order when…</span>
          <select bind:value={triggerKind}>
            {#each TRIGGER_KINDS as kind}
              <option value={kind}>
                {kind === 'PercentDrop' ? 'the price drops by a % — from here, or from its peak'
                 : kind === 'PercentRise' ? 'the price rises by a %'
                 : kind === 'PriceBelow' ? 'the price falls to an exact level'
                 : kind === 'PriceAbove' ? 'the price rises to an exact level'
                 : 'something happens on the chart'}
              </option>
            {/each}
          </select>
        </label>

        {#if isPercent}
          <div class="percent-row">
            <div class="presets">
              {#each PERCENT_PRESETS as preset}
                <button
                  type="button"
                  class="chip-btn"
                  class:on={triggerPercent === preset}
                  on:click={() => triggerPercent = preset}
                >{fallsToTrigger ? '−' : '+'}{preset}%</button>
              {/each}
            </div>
            <label class="tight">
              <span>or</span>
              <input type="number" step="0.1" min="0.1" max="50" bind:value={triggerPercent} />
            </label>
            <label class="tight">
              <span>measured from</span>
              <input type="number" step="0.01" bind:value={referencePrice} />
            </label>
          </div>

          <label class="check">
            <input type="checkbox" bind:checked={trailing} />
            <span>
              Follow the price {fallsToTrigger ? 'up' : 'down'} as it moves
              <em>({fallsToTrigger ? 'a trailing stop' : 'chases a falling market'})</em>
            </span>
          </label>
          <p class="hint-note">
            {#if trailing}
              The {fallsToTrigger ? 'higher' : 'lower'} {symbol} goes, the {fallsToTrigger ? 'higher' : 'lower'}
              this trigger goes with it — it never moves back, so the {triggerPercent}% is always
              measured from the {fallsToTrigger ? 'best' : 'lowest'} price seen since you armed it.
            {:else}
              The {triggerPercent}% is measured from {referencePrice ?? '—'} and stays there, whatever
              {symbol} does afterwards.
            {/if}
          </p>
        {:else if isEvent}
          <label class="full">
            <span>Event</span>
            <select bind:value={triggerAlertKind}>
              <option value={null}>— choose —</option>
              {#each ALERT_KINDS as kind}
                <option value={kind}>{kind.replace(/([a-z])([A-Z])/g, '$1 $2')}</option>
              {/each}
            </select>
          </label>
        {:else}
          <label class="full">
            <span>Trigger price</span>
            <input type="number" step="0.01" bind:value={triggerPrice} />
          </label>
        {/if}

        {#if level != null}
          <p class="level-line">
            Fires at <b>{money(level)}</b>
            {#if currentPrice != null && currentPrice > 0}
              · {symbol} is {money(currentPrice)} now
            {/if}
          </p>
        {/if}

        {#if alreadyPassed}
          <p class="error">
            <AlertTriangle size={12} />
            {symbol} is already {fallsToTrigger ? 'at or below' : 'at or above'} {money(level!)}, so
            this would fire on the next check rather than wait for a move. Arm it only if that is
            what you mean.
          </p>
        {/if}
      </div>

      <div class="grid">
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
            <option value="MARKET">Market — take the best price available</option>
            <option value="LIMIT">Limit — no worse than a price I set</option>
            <option value="STOPLOSS">Stop Loss — broker trigger + limit</option>
          </select>
        </label>

        {#if orderType !== 'MARKET'}
          <label>
            <span>{isStop ? 'Stop trigger' : 'Limit price'}</span>
            <input type="number" step="0.01" bind:value={price} />
          </label>
        {/if}

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

      {#if isPercent && trailing && orderType !== 'MARKET'}
        <!-- Said plainly because it is the one place the two features do not compose: the trigger
             trails, the price on the order does not. Left unsaid, a trail that ran up 20% would fire
             a limit priced for where the stock was when it was armed, and simply not fill. -->
        <p class="hint-note warn">
          <AlertTriangle size={12} />
          Your {orderType === 'LIMIT' ? 'limit' : 'stop'} price stays at {price ?? '—'} even as the
          trigger trails upward, so a large move could leave it too far away to fill. Market is the
          safer pairing with a trailing trigger.
        </p>
      {/if}

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

      {#if summary}
        <p class="summary">{summary}</p>
      {/if}

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

  /* The trigger gets its own framed block: it is a different question from the order, and the
     percent controls need room to read as one row rather than as three unrelated fields. */
  .trigger-block {
    border: 1px solid var(--border-md); border-left: 3px solid var(--primary);
    border-radius: var(--radius-sm); padding: .65rem .7rem;
    display: flex; flex-direction: column; gap: .55rem;
  }
  .percent-row { display: flex; gap: .6rem; align-items: flex-end; flex-wrap: wrap; }
  .presets { display: flex; gap: .3rem; }
  .chip-btn {
    background: var(--surface-2); border: 1px solid var(--border-md); border-radius: var(--radius-sm);
    color: var(--text-2); font: inherit; font-size: .78rem; font-variant-numeric: tabular-nums;
    padding: .4rem .55rem; cursor: pointer; white-space: nowrap;
  }
  .chip-btn:hover { color: var(--text); border-color: var(--primary); }
  .chip-btn.on { background: var(--primary); border-color: var(--primary); color: #0c0d10; font-weight: 600; }
  .tight { flex: 0 1 8rem; min-width: 6rem; }

  .level-line { margin: 0; color: var(--text-2); font-size: .78rem; }
  .level-line b { color: var(--text); font-family: ui-monospace, monospace; font-size: .86rem; }

  .hint-note { margin: 0; color: var(--text-3); font-size: .7rem; line-height: 1.55; }
  .hint-note.warn { color: var(--warning); display: flex; gap: .35rem; align-items: flex-start; }
  .check em { color: var(--text-3); font-style: normal; font-size: .72rem; }

  /* The whole order in one sentence, immediately above the button that commits it. */
  .summary {
    margin: 0; background: var(--surface-2); border-radius: var(--radius-sm);
    padding: .55rem .65rem; color: var(--text); font-size: .8rem; line-height: 1.55;
  }

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
  .error { margin: 0; color: var(--danger); font-size: .75rem; line-height: 1.5;
           display: flex; gap: .35rem; align-items: flex-start; }

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
