<script lang="ts">
  import { onMount, tick } from 'svelte';
  import { Bell, Plus } from 'lucide-svelte';
  import { trading, type PriceAlert, type PriceAlertInput, type PriceAlertObservation, type PriceAlertsPage } from './api';

  let page: PriceAlertsPage | null = null;
  let dialog: HTMLDialogElement;
  let draft: PriceAlertInput = { symbol: '', direction: 'above', target: 0 };
  let current: PriceAlertObservation | null = null;
  let editing: string | null = null;
  let history = false;
  let limit = 25;
  let busy = false;
  let loading = false;
  let loadVersion = 0;
  let error = '';
  let formError = '';
  let notice = '';
  let visible: PriceAlertsPage['rules'] = [];
  $: visible = (page?.rules ?? []).filter(({rule}) => (rule.state !== 'watching') === history);
  $: index = /^KSE[ -]?100$/i.test(draft.symbol.trim());
  $: current = page?.rules.find(row => row.rule.symbol === (index ? 'KSE100' : draft.symbol.trim().toUpperCase()) && row.latest)?.latest ?? null;
  const stamp = (at: string) => new Date(at).toLocaleTimeString('en-GB', { timeZone: 'Asia/Karachi', hour: '2-digit', minute: '2-digit' }) + ' PKT';
  const instrument = (symbol: string) => symbol === 'KSE100' ? 'KSE 100' : symbol;
  const value = (symbol: string, price: number) => {
    const formatted = price.toLocaleString('en-PK', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
    return symbol === 'KSE100' ? formatted + ' points' : 'PKR ' + formatted;
  };

  async function load(afterChange = false) {
    if (loading && !afterChange) return;
    const version = ++loadVersion;
    loading = true;
    try {
      const result = await trading.priceAlerts.list();
      if (version === loadVersion) { page = result; error = ''; }
    } catch (e) {
      if (version === loadVersion) error = e instanceof Error ? e.message : 'Could not load price alerts.';
    } finally {
      if (version === loadVersion) loading = false;
    }
  }
  onMount(() => {
    void load();
    const timer = setInterval(() => { if (!document.hidden) void load(); }, 30_000);
    return () => { clearInterval(timer); loadVersion++; };
  });

  export async function open(symbol = '', target = 0, rule?: PriceAlert, direction: PriceAlertInput['direction'] = 'above') {
    editing = rule?.id ?? null;
    draft = rule ? {symbol: rule.symbol, direction: rule.direction, target: rule.target} : { symbol, direction, target };
    formError = '';
    await tick();
    if (!dialog.open) dialog.showModal();
  }
  async function save() {
    if (busy || !draft.target) return;
    busy = true; formError = '';
    try {
      if (editing) await trading.priceAlerts.edit(editing, draft);
      else await trading.priceAlerts.create(draft);
      dialog.close();
      notice = editing ? 'Price alert updated.' : 'Price alert set.';
      history = false;
      await load(true);
    } catch (e) { formError = e instanceof Error ? e.message : 'Could not save the alert.'; }
    finally { busy = false; }
  }
  async function cancel(rule: PriceAlert) {
    if (busy) return;
    busy = true; error = '';
    try { await trading.priceAlerts.cancel(rule.id); notice = 'Price alert cancelled.'; await load(true); }
    catch (e) { error = e instanceof Error ? e.message : 'Could not cancel the alert.'; }
    finally { busy = false; }
  }
  function status(rule: PriceAlert, hasQuote: boolean) {
    switch (rule.state) {
      case 'watching': return !page?.marketOpen ? 'Market closed' : hasQuote ? 'Watching' : 'Waiting for fresh data';
      case 'pending': return rule.pushQueued ? 'Target reached · App push queued' : 'Target reached · Waiting for delivery';
      case 'delivered': return `Delivered to ${rule.recipientCount} ${rule.recipientCount === 1 ? 'channel' : 'channels'}${rule.pushQueued ? ' · App push queued' : ''}`;
      case 'delivery-failed': return rule.pushQueued ? 'App push queued · Channel delivery failed' : 'Channel delivery failed';
      case 'cancelled': return 'Cancelled';
      case 'expired': return 'Expired';
      default: return 'Unavailable';
    }
  }
</script>

<section aria-label="Price alerts">
  <header>
    <div class="actions" aria-label="Price alert views">
      <button type="button" aria-pressed={!history} on:click={() => { history = false; limit = 25; }}>Active</button>
      <button type="button" aria-pressed={history} on:click={() => { history = true; limit = 25; }}>History</button>
    </div>
    <button type="button" class="primary" on:click={() => open()}><Plus size={15}/> Set alert</button>
  </header>
  <div role="status">{notice}</div>
  {#if error}<p role="alert">{error} <button on:click={() => load()}>Retry</button></p>{/if}
  {#if !page && loading}
    <p>Loading price alerts…</p>
  {:else if page && visible.length === 0}
    <p class="muted">{history ? 'No price alert history yet.' : 'No active price alerts.'}</p>
  {/if}
  <ul>
    {#each visible.slice(0, limit) as {rule, latest} (rule.id)}
      {@const reading = rule.trigger ?? latest}
      <li>
        <div>
          <strong>{instrument(rule.symbol)}</strong>
          <span>{rule.direction === 'above' ? 'at or above' : 'at or below'} {value(rule.symbol, rule.target)}</span>
          <small>{status(rule, !!latest)}</small>

          {#if reading}<small>{value(rule.symbol, reading.value)} · {stamp(reading.observedUtc)} · {reading.source === 'psx' ? 'PSX' : 'Broker feed'}</small>{/if}
        </div>
        {#if rule.state === 'watching'}
          <div class="actions">
            <button disabled={busy} on:click={() => open(rule.symbol, rule.target, rule)}>Edit</button>
            <button disabled={busy} on:click={() => cancel(rule)}>Cancel alert</button>
          </div>
        {/if}
      </li>
    {/each}
  </ul>
  {#if visible.length > limit}<button type="button" on:click={() => limit += 25}>Show more</button>{/if}
</section>

<dialog bind:this={dialog} aria-labelledby="price-alert-title" on:cancel={event => { if (busy) event.preventDefault(); }}>
  <form on:submit|preventDefault={save}>
    <h3 id="price-alert-title"><Bell size={18}/> {editing ? 'Edit price alert' : 'Set price alert'}</h3>
    <label>Instrument <input bind:value={draft.symbol} list="price-alert-instruments" placeholder="Stock symbol or KSE 100" required maxlength="16" autocomplete="off"/></label>
    <datalist id="price-alert-instruments"><option value="KSE 100"></option></datalist>
    {#if current}<p class="muted">Latest {value(index ? 'KSE100' : draft.symbol, current.value)} · {stamp(current.observedUtc)}</p>{/if}
    <label>Notify when
      <select bind:value={draft.direction}><option value="above">At or above</option><option value="below">At or below</option></select>
    </label>
    <label>Target ({index ? 'points' : 'PKR'})
      <input type="number" min="0.0001" max="100000000" step="0.0001" bind:value={draft.target} required/>
    </label>
    {#if current && draft.target > 0 && (draft.direction === 'above' ? current.value >= draft.target : current.value <= draft.target)}
      <p role="status">Target already met. This alert may send immediately.</p>
    {/if}
    <p class="muted">Delivery · Configured channels <a href="/channels" target="_top">Manage channels</a></p>
    <details><summary aria-label="Help about price alerts">?</summary>Alerts fire once on the first fresh reading that meets the target. An already-met target can send immediately. Active alerts expire after 30 days.</details>
    {#if formError}<p role="alert">{formError}</p>{/if}
    <footer><button type="button" disabled={busy} on:click={() => dialog.close()}>Close</button><button class="primary" type="submit" disabled={busy}>{busy ? 'Saving…' : editing ? 'Save changes' : 'Set alert'}</button></footer>
  </form>
</dialog>

<style>
  section { padding: .75rem; color: var(--text); }
  header, footer, li, .actions { display:flex; align-items:center; gap:.5rem; flex-wrap:wrap; }
  header, li { justify-content:space-between; }
  ul { padding:0; margin:.5rem 0 0; list-style:none; }
  li { padding:.6rem 0; border-top:1px solid var(--border, var(--line)); }
  li strong { margin-right:.4rem; }
  small { display:block; margin-top:.2rem; color:var(--text-2, var(--muted)); }
  button { color:inherit; background:var(--surface-2, var(--card)); border:1px solid var(--border, var(--line)); border-radius:.4rem; min-height:2.25rem; padding:.3rem .65rem; cursor:pointer; }
  button[aria-pressed="true"] { border-color:var(--primary, var(--accent)); }
  .primary { display:inline-flex; align-items:center; gap:.35rem; border-color:var(--primary, var(--accent)); font-weight:650; }
  dialog { width:min(28rem, calc(100% - 2rem)); padding:1.1rem; border:1px solid var(--border, var(--line)); border-radius:.65rem; background:var(--surface, var(--card, Canvas)); color:var(--text, CanvasText); }
  dialog::backdrop { background:#0008; }
  form, label { display:grid; gap:.45rem; }
  form { gap:.75rem; }
  h3 { margin:0; display:flex; align-items:center; gap:.4rem; }
  input, select { color:inherit; background:var(--surface-2, Canvas); padding:.45rem; border:1px solid var(--border, var(--line)); border-radius:.3rem; font:inherit; }
  p { margin:.3rem 0; }
  .muted { color:var(--text-2, var(--muted)); }
  footer { justify-content:flex-end; }
  @media (pointer: coarse) { button, input, select { min-height:44px; } }
</style>
