<script lang="ts">
  import { onMount, onDestroy, createEventDispatcher } from 'svelte';
  import {
    trading,
    type ArmOrderDialogContext, type TradingAlert, type MonitorStatus, type StockAssessment
  } from './api';
  import {
    Bell, Check, X, Activity, RefreshCw, Radio, AlertTriangle, Brain, Crosshair,
    CheckCheck, Trash2
  } from 'lucide-svelte';
  import AssessmentCard from './AssessmentCard.svelte';

  /** Verdicts fetched on demand, by alert id. Never fetched automatically — see api.assess. */
  let assessments: Record<string, StockAssessment> = {};
  let assessing: string | null = null;

  /**
   * Selecting an alert drives the chart pane to that symbol. `alertsChanged` fires whenever the open
   * count could have moved (ack/dismiss, a manual pass, or a new alert over the live stream) — the
   * watchlist's per-symbol open-alert badge is fetched separately and has no other way to learn this.
   */
  const dispatch = createEventDispatcher<{
    select: string;
    arm: ArmOrderDialogContext;
    alertsChanged: void;
  }>();

  let alerts: TradingAlert[] = [];
  let status: MonitorStatus | null = null;
  let loading = true;
  let busy = false;
  /** Alert ids with an ack/dismiss in flight — scoped per row so one slow request doesn't grey out the rest of the list. */
  let busyAlertIds = new Set<string>();
  let error: string | null = null;
  let showDismissed = false;
  let selectedAlertIds = new Set<string>();
  let live = false;
  let stopStream: (() => void) | null = null;

  /**
   * Reconcile a REST snapshot with alerts that may have arrived over SSE while the request was in
   * flight. The snapshot wins for matching ids because it carries the latest ack/dismiss state;
   * stream-only ids are retained so the initial load can never erase a just-arrived alert.
   */
  function mergeAlerts(incoming: TradingAlert[]) {
    const byId = new Map(alerts.map(alert => [alert.alertId, alert]));
    for (const alert of incoming) byId.set(alert.alertId, alert);
    alerts = [...byId.values()].sort(
      (a, b) => new Date(b.raisedUtc).getTime() - new Date(a.raisedUtc).getTime()
    );
  }

  async function refreshAlerts() {
    const incoming = await trading.alerts.list({ limit: 100 });
    mergeAlerts(incoming);
  }

  async function load() {
    loading = true;
    error = null;
    const [alertsResult, statusResult] = await Promise.allSettled([
      trading.alerts.list({ limit: 100 }),
      trading.monitor.status()
    ]);

    const failures: string[] = [];
    if (alertsResult.status === 'fulfilled') mergeAlerts(alertsResult.value);
    else failures.push(`Alerts: ${alertsResult.reason instanceof Error ? alertsResult.reason.message : String(alertsResult.reason)}`);

    if (statusResult.status === 'fulfilled') status = statusResult.value;
    else failures.push(`Monitor status: ${statusResult.reason instanceof Error ? statusResult.reason.message : String(statusResult.reason)}`);

    error = failures.length ? failures.join(' · ') : null;
    loading = false;
  }

  async function runNow() {
    if (busy) return;
    busy = true;
    error = null;
    try {
      status = await trading.monitor.run();
      await refreshAlerts();
      dispatch('alertsChanged');
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  async function setState(alert: TradingAlert, action: 'ack' | 'dismiss') {
    if (busyAlertIds.has(alert.alertId)) return;
    busyAlertIds = new Set(busyAlertIds).add(alert.alertId);
    try {
      const result = action === 'ack'
        ? await trading.alerts.ack(alert.alertId)
        : await trading.alerts.dismiss(alert.alertId);
      // Patch in place rather than refetching: the list can be long and the change is one field.
      alerts = alerts.map(a => a.alertId === alert.alertId ? { ...a, state: result.state } : a);
      dispatch('alertsChanged');
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      const next = new Set(busyAlertIds);
      next.delete(alert.alertId);
      busyAlertIds = next;
    }
  }

  function toggleSelected(alertId: string) {
    const next = new Set(selectedAlertIds);
    if (next.has(alertId)) next.delete(alertId); else next.add(alertId);
    selectedAlertIds = next;
  }

  function toggleSelectAll() {
    selectedAlertIds = visible.every(alert => selectedAlertIds.has(alert.alertId))
      ? new Set()
      : new Set(visible.map(alert => alert.alertId));
  }

  async function bulkAction(action: 'acknowledge' | 'dismiss', all = false) {
    if (busy) return;
    const ids = all ? undefined : [...selectedAlertIds];
    if (!all && !ids?.length) return;
    const label = action === 'acknowledge' ? 'mark read' : 'delete from the active view';
    if (action === 'dismiss' && !confirm(
      `${all ? 'Clear all alerts' : `Delete ${ids!.length} selected alert(s)`}?\n\n` +
      `This will ${label}. The audit record is retained and can be viewed with “show dismissed”.`
    )) return;

    busy = true;
    error = null;
    try {
      const result = await trading.alerts.bulk(action, ids, all);
      const affected = all ? alerts.map(alert => alert.alertId) : ids!;
      const state = result.state;
      alerts = alerts.map(alert => affected.includes(alert.alertId) ? { ...alert, state } : alert);
      selectedAlertIds = new Set();
      dispatch('alertsChanged');
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  /**
   * Arms an order on the KIND of event this alert is — not on this instance, which has already
   * happened. Side is inferred from the event's direction (a bounce or breakout is a buy case, a
   * rejection or breakdown a sell case) and stays editable.
   */
  function armFromAlert(alert: TradingAlert) {
    const bullish = ['SupportBounce', 'ResistanceBreakout'].includes(alert.kind);
    dispatch('arm', {
      symbol: alert.symbol,
      triggerKind: 'Event',
      triggerAlertKind: alert.kind,
      action: bullish ? 'BUY' : 'SELL',
      orderType: 'LIMIT',
      price: alert.levelPrice ?? alert.price,
      sourceAlertId: alert.alertId,
      context:
        `Fires the NEXT time ${label(alert.kind)} is raised for ${alert.symbol}. ` +
        `This alert is the example, not the trigger — it has already happened.`
    });
  }

  async function assess(alert: TradingAlert) {
    if (assessing) return;
    assessing = alert.alertId;
    error = null;
    try {
      const result = await trading.assess.alert(alert.alertId);
      assessments = { ...assessments, [alert.alertId]: result.assessment };
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      assessing = null;
    }
  }

  onMount(() => {
    load();
    // Live push, so a level break reaches an open page in seconds rather than at the next poll.
    // Prepending only if unseen keeps this safe against a duplicate from a concurrent reload.
    stopStream = trading.alerts.stream(
      alert => {
        if (!alerts.some(a => a.alertId === alert.alertId)) {
          alerts = [alert, ...alerts];
          dispatch('alertsChanged');
        }
      },
      connected => {
        const reconnected = connected && !live;
        live = connected;
        // Re-read SQLite whenever a stream (re)connects. Alerts raised during a network gap are
        // durable even though their one-time push event has already passed.
        if (reconnected && !loading) refreshAlerts().catch(() => {});
      }
    );
  });

  onDestroy(() => stopStream?.());

  $: visible = showDismissed ? alerts : alerts.filter(a => a.state !== 'dismissed');
  $: openCount = alerts.filter(a => a.state === 'new').length;
  $: liveBarCount = visible.filter(a => a.fromLiveBar).length;
  $: selectedVisibleCount = visible.filter(alert => selectedAlertIds.has(alert.alertId)).length;
  $: allVisibleSelected = visible.length > 0 && selectedVisibleCount === visible.length;

  const when = (iso: string) => new Date(iso).toLocaleString();
  const label = (kind: string) => kind.replace(/([a-z])([A-Z])/g, '$1 $2');
</script>

<section class="alerts">
  <header>
    <div class="head-copy">
      <Bell size={15} />
      <div>
        <b>Alerts {#if openCount}<span class="count">{openCount}</span>{/if}</b>
        <span>
          {#if status}
            {status.enabled
              ? `Monitoring every ${status.intervalSeconds}s · ${status.confirmPasses} confirming passes`
              : 'Monitoring is disabled'}
            {#if status.lastPassUtc} · last pass {when(status.lastPassUtc)}{/if}
          {:else}Trend, support, and resistance transitions{/if}
        </span>
      </div>
    </div>
    <div class="head-actions">
      {#if live}<span class="chip live" title="Connected to the live alert stream"><Radio size={11} /> live</span>{/if}
      <label class="toggle">
        <input type="checkbox" bind:checked={showDismissed} /> show dismissed
      </label>
      <button class="btn btn-ghost" on:click={runNow} disabled={busy || loading} title="Run a monitoring pass now">
        <RefreshCw size={13} /> Check now
      </button>
    </div>
  </header>

  {#if status?.message}
    <p class="status-line" class:warn={status.alertsSuppressed > 0}>{status.message}</p>
  {/if}
  {#if error}<p class="status-line danger">{error}</p>{/if}

  {#if liveBarCount > 0}
    <p class="live-bar-note">
      <AlertTriangle size={13} />
      {#if status?.marketOpen}
        <span><b>Today’s daily candle is still open.</b> During market hours, daily alerts use the live
        candle so they arrive promptly; the signal can change or disappear by the closing bell.</span>
      {:else}
        <span><b>Some alerts were raised from a forming candle.</b> The market is now closed; these
        records keep their original context and the chart is showing settled prices.</span>
      {/if}
    </p>
  {/if}

  {#if !loading && visible.length}
    <div class="bulk-actions" aria-label="Bulk alert actions">
      <label class="select-all">
        <input type="checkbox" checked={allVisibleSelected} on:change={toggleSelectAll} />
        {allVisibleSelected ? 'Clear selection' : 'Select all'}
      </label>
      <span>{selectedVisibleCount} selected</span>
      <button class="btn btn-ghost" on:click={() => bulkAction('acknowledge')} disabled={!selectedVisibleCount || busy}>
        <CheckCheck size={13} /> Mark read
      </button>
      <button class="btn btn-ghost danger-action" on:click={() => bulkAction('dismiss')} disabled={!selectedVisibleCount || busy}>
        <Trash2 size={13} /> Delete
      </button>
      <button class="btn btn-ghost danger-action push" on:click={() => bulkAction('dismiss', true)} disabled={busy}>
        <Trash2 size={13} /> Clear all
      </button>
    </div>
  {/if}

  {#if loading}
    <p class="status-line">Loading alerts…</p>
  {:else if !visible.length}
    <p class="empty">
      <Activity size={22} />
      No alerts. The monitor reports transitions — a bounce off support, a level breaking, a trend
      flip — not standing conditions, so a quiet list is the normal state.
    </p>
  {:else}
    <ul class="list">
      {#each visible as alert (alert.alertId)}
        <li class="alert {alert.severity.toLowerCase()}" class:resolved={alert.state !== 'new'}>
          <label class="row-select" title="Select alert">
            <input
              type="checkbox"
              checked={selectedAlertIds.has(alert.alertId)}
              on:change={() => toggleSelected(alert.alertId)}
              aria-label="Select {alert.symbol} alert"
            />
          </label>
          <button class="body" on:click={() => dispatch('select', alert.symbol)}>
            <div class="row-1">
              <span class="symbol">{alert.symbol}</span>
              <span class="kind">{label(alert.kind)}</span>
              {#if alert.weeklyConfirmed}<span class="chip ok">weekly ✓</span>{/if}
              {#if alert.fromLiveBar}<span class="chip warn" title="Uses today’s still-open candle; the signal can change before market close">open candle</span>{/if}
              {#if alert.state !== 'new'}<span class="chip">{alert.state}</span>{/if}
            </div>
            <p class="summary">{alert.summary}</p>
            <div class="meta">{alert.interval} · {when(alert.raisedUtc)}</div>
            {#if assessments[alert.alertId]}
              <AssessmentCard assessment={assessments[alert.alertId]} compact />
            {/if}
          </button>
          <div class="actions">
            <button
              class="icon"
              title="Ask the model how much confidence the evidence supports (one model call)"
              on:click={() => assess(alert)}
              disabled={!!assessing}
            >
              <Brain size={13} />
            </button>
            <button
              class="icon"
              title="Arm an order that fires the next time this event happens on {alert.symbol}"
              on:click={() => armFromAlert(alert)}
            >
              <Crosshair size={13} />
            </button>
            {#if alert.state === 'new'}
              <button class="icon" title="Acknowledge" on:click={() => setState(alert, 'ack')} disabled={busyAlertIds.has(alert.alertId)}>
                <Check size={13} />
              </button>
            {/if}
            {#if alert.state !== 'dismissed'}
              <button class="icon" title="Dismiss" on:click={() => setState(alert, 'dismiss')} disabled={busyAlertIds.has(alert.alertId)}>
                <X size={13} />
              </button>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  {/if}

  {#if status?.warnings?.length}
    <div class="warnings">
      {#each status.warnings as warning}<p><AlertTriangle size={12} /> {warning}</p>{/each}
    </div>
  {/if}
</section>

<style>
  .alerts {
    background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius);
    padding: 1rem; display: flex; flex-direction: column; gap: .7rem; margin-bottom: 1.25rem;
  }
  header { display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; flex-wrap:wrap; }
  .head-copy { display:flex; gap:.55rem; align-items:flex-start; color:var(--primary); }
  .head-copy div { display:flex; flex-direction:column; gap:.2rem; }
  .head-copy b { color:var(--text); font-size:.9rem; display:flex; align-items:center; gap:.4rem; }
  .head-copy span { color:var(--text-3); font-size:.72rem; }
  .count {
    background:var(--danger); color:#0c0d10; border-radius:999px;
    font-size:.65rem; padding:.05rem .35rem; font-weight:700;
  }
  .head-actions { display:flex; align-items:center; gap:.6rem; flex-wrap:wrap; }
  .head-actions .btn { display:flex; align-items:center; gap:.35rem; }
  .toggle { display:flex; align-items:center; gap:.3rem; color:var(--text-3); font-size:.7rem; cursor:pointer; }

  .status-line { margin:0; color:var(--text-3); font-size:.72rem; }
  .status-line.warn { color:var(--warning); }
  .status-line.danger { color:var(--danger); }
  .live-bar-note { margin:0; padding:.55rem .65rem; border:1px solid color-mix(in srgb,var(--warning) 32%,var(--border)); border-radius:var(--radius-sm); background:color-mix(in srgb,var(--warning) 7%,transparent); color:var(--text-2); font-size:.7rem; line-height:1.4; display:flex; align-items:flex-start; gap:.45rem; }
  .live-bar-note :global(svg) { color:var(--warning); flex:0 0 auto; margin-top:.1rem; }
  .live-bar-note b { color:var(--warning); }

  .bulk-actions {
    display:flex; align-items:center; gap:.45rem; flex-wrap:wrap; padding:.4rem .5rem;
    background:var(--surface-2); border:1px solid var(--border); border-radius:var(--radius-sm);
    color:var(--text-3); font-size:.68rem;
  }
  .bulk-actions .btn { display:flex; align-items:center; gap:.3rem; padding:.32rem .5rem; }
  .bulk-actions .push { margin-left:auto; }
  .select-all { display:flex; align-items:center; gap:.3rem; cursor:pointer; color:var(--text-2); }
  .danger-action:hover:not(:disabled) { color:var(--danger); border-color:color-mix(in srgb, var(--danger) 40%, transparent); }

  .empty {
    color:var(--text-3); font-size:.75rem; margin:0; padding:1.25rem 0; text-align:center;
    display:flex; flex-direction:column; align-items:center; gap:.5rem; line-height:1.6;
  }

  .list { list-style:none; margin:0; padding:0; display:flex; flex-direction:column; gap:.35rem; max-height:340px; overflow-y:auto; }
  .alert {
    display:flex; gap:.4rem; align-items:flex-start;
    background:var(--surface-2); border-radius:var(--radius-sm);
    border-left:3px solid var(--text-3);
  }
  /* Severity is the only colour signal in the list, so it has to be immediately legible. */
  .alert.critical { border-left-color:var(--danger); }
  .alert.high     { border-left-color:var(--warning); }
  .alert.medium   { border-left-color:var(--info); }
  .alert.low      { border-left-color:var(--text-3); }
  .alert.resolved { opacity:.55; }
  .row-select { padding:.65rem 0 0 .5rem; display:flex; cursor:pointer; }

  .body { flex:1; background:none; border:0; text-align:left; cursor:pointer; padding:.5rem .6rem; font:inherit; color:var(--text); display:flex; flex-direction:column; gap:.25rem; min-width:0; }
  .row-1 { display:flex; align-items:center; gap:.4rem; flex-wrap:wrap; }
  .symbol { font-family:ui-monospace, monospace; font-weight:600; font-size:.78rem; }
  .kind { color:var(--text-2); font-size:.7rem; text-transform:lowercase; }
  .summary { margin:0; color:var(--text-2); font-size:.73rem; line-height:1.5; }
  .meta { color:var(--text-3); font-size:.65rem; }

  .chip { font-size:.6rem; padding:.05rem .35rem; border-radius:999px; border:1px solid var(--border-md); color:var(--text-3); }
  .chip.ok { color:var(--success); border-color:color-mix(in srgb, var(--success) 35%, transparent); }
  .chip.warn { color:var(--warning); border-color:color-mix(in srgb, var(--warning) 35%, transparent); }
  .chip.live { color:var(--success); display:inline-flex; align-items:center; gap:.2rem; }

  .actions { display:flex; gap:.15rem; padding:.4rem .3rem; }
  .icon { background:none; border:0; cursor:pointer; color:var(--text-3); padding:.25rem; border-radius:var(--radius-sm); display:flex; }
  .icon:hover { background:var(--surface-3); color:var(--text); }
  .icon:disabled { opacity:.5; cursor:wait; }

  .warnings p { margin:0; color:var(--warning); font-size:.68rem; display:flex; gap:.3rem; align-items:flex-start; }
</style>
