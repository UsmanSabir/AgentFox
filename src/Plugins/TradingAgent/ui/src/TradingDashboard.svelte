<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import {
    trading,
    type ArmOrderDialogContext, type TradingStatus, type TradeProposal, type TradingExecution,
    type TradingEvent, type ReconciliationRun, type CandleArchiveStatus
  } from './api';
  import { RefreshCw, ShieldAlert, Activity, FileText, ListChecks, Scale, History, Power, Database, Download, Play, XCircle } from 'lucide-svelte';
  import WatchlistPanel from './WatchlistPanel.svelte';
  import ChartPane from './ChartPane.svelte';
  import AlertsPanel from './AlertsPanel.svelte';
  import ArmedOrdersPanel from './ArmedOrdersPanel.svelte';
  import ArmOrderDialog from './ArmOrderDialog.svelte';

  /**
   * Non-null while the arming dialog is open. Both entry points — a chart level and an alert — raise the
   * same event with pre-filled context, so there is one dialog rather than one per origin.
   */
  let armContext: ArmOrderDialogContext | null = null;
  let armedPanel: ArmedOrdersPanel | null = null;
  let watchlistPanel: WatchlistPanel | null = null;

  /** Symbol the watchlist has selected; drives the chart pane. */
  let selectedSymbol: string | null = null;

  /** Full-width chart mode, toggled from the chart's own header. */
  let chartExpanded = false;

  /** Proposal inbox: open-only by default, since a decision queue should read as empty when it is. */
  let showResolvedProposals = false;
  let proposalBusy: string | null = null;

  async function loadProposals() {
    try {
      proposals = await trading.proposals(!showResolvedProposals);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }

  async function executeProposal(item: TradeProposal) {
    if (proposalBusy) return;
    const orders = item.proposal.orders?.length ?? 0;
    if (!confirm(
      `Execute this proposal?\n\n` +
      `${orders} order(s) will be handed to the trading manager. Every safety gate still applies ` +
      `(execution mode, risk limits, market hours, kill switch), so it may still be refused — but if ` +
      `they pass, this places REAL orders.`
    )) return;

    proposalBusy = item.proposalId;
    error = null;
    try {
      const result = await trading.executeProposal(item.proposalId);
      // A refusal is not a failure of the click: it usually means a gate said no (market closed,
      // reconciliation stale), and the proposal stays actionable. Say which happened.
      if (!result.accepted) error = `Not executed: ${result.reason}`;
      await Promise.all([loadProposals(), load()]);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      proposalBusy = null;
    }
  }

  async function rejectProposal(item: TradeProposal) {
    if (proposalBusy) return;
    const reason = prompt('Reject this proposal — why? (recorded on the audit trail)');
    if (reason === null) return;

    proposalBusy = item.proposalId;
    try {
      await trading.rejectProposal(item.proposalId, reason || undefined);
      await Promise.all([loadProposals(), load()]);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      proposalBusy = null;
    }
  }

  let loading = true;
  let killSwitchBusy = false;
  let backfillBusy = false;
  let error: string | null = null;
  let status: TradingStatus | null = null;
  let archive: CandleArchiveStatus | null = null;
  let proposals: TradeProposal[] = [];
  let executions: TradingExecution[] = [];
  let events: TradingEvent[] = [];
  let reconciliation: ReconciliationRun[] = [];
  let tab: 'proposals' | 'executions' | 'reconciliation' | 'events' = 'proposals';
  let archivePoll: ReturnType<typeof setInterval> | null = null;

  async function load() {
    loading = true;
    error = null;
    try {
      [status, archive, proposals, executions, reconciliation, events] = await Promise.all([
        trading.status(), trading.candleArchive(), trading.proposals(!showResolvedProposals),
        trading.executions(), trading.reconciliation(), trading.events()
      ]);
      syncArchivePolling();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  // A backfill runs for many minutes, so the card refreshes itself while one is in flight and stops
  // polling once it finishes — no reason to keep hitting the API for a static archive.
  function syncArchivePolling() {
    const running = archive?.progress.isRunning ?? false;
    if (running && !archivePoll) {
      archivePoll = setInterval(async () => {
        try {
          archive = await trading.candleArchive();
          if (!archive.progress.isRunning) syncArchivePolling();
        } catch { /* transient: the next tick retries */ }
      }, 4000);
    } else if (!running && archivePoll) {
      clearInterval(archivePoll);
      archivePoll = null;
    }
  }

  async function startBackfill() {
    if (backfillBusy || archive?.progress.isRunning) return;
    const days = archive?.missingTradingDays ?? 0;
    const minutes = Math.max(1, Math.round(days * 2.1 / 60));
    if (!confirm(
      `Backfill ${days} missing trading days of daily candles?\n\n` +
      `This runs in the background for roughly ${minutes} minute(s), paced to avoid being ` +
      `throttled by the PSX portal. It is resumable, so you can leave this page.`
    )) return;

    backfillBusy = true;
    try {
      const result = await trading.startBackfill();
      archive = result.status;
      syncArchivePolling();
      if (!result.started) error = 'A backfill pass is already running.';
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      backfillBusy = false;
    }
  }

  /**
   * The page's single clock. One interval here rather than one per component: it refreshes the cheap
   * status (which carries the market-open flag the chart's refresh is gated on) and ticks a counter the
   * chart watches. The status call is what keeps that flag current, so a market that opens while the
   * page is left open is noticed within a minute instead of never.
   */
  const MARKET_TICK_MS = 60_000;
  let marketTick = 0;
  let marketTimer: ReturnType<typeof setInterval> | null = null;

  function startMarketClock() {
    marketTimer ??= setInterval(async () => {
      if (typeof document !== 'undefined' && document.hidden) return; // a hidden tab needs nothing
      try {
        status = await trading.status();
      } catch {
        /* transient — the next tick retries, and the chart simply does not refresh this round */
      }
      marketTick += 1;
    }, MARKET_TICK_MS);
  }

  onDestroy(() => {
    if (archivePoll) clearInterval(archivePoll);
    if (marketTimer) clearInterval(marketTimer);
  });

  const date = (value?: string) => value ? new Date(value).toLocaleString() : '—';
  const json = (value: unknown) => JSON.stringify(value, null, 2);

  async function toggleKillSwitch() {
    if (!status || killSwitchBusy) return;
    const next = !status.killSwitch;
    if (next && !confirm('Activate the kill switch? This blocks ALL trading orders immediately.')) return;
    killSwitchBusy = true;
    try {
      await trading.setKillSwitch(next);
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      killSwitchBusy = false;
    }
  }

  onMount(() => { load(); startMarketClock(); });
</script>

{#if armContext}
  <ArmOrderDialog
    {...armContext}
    on:armed={() => armedPanel?.load()}
    on:close={() => armContext = null}
  />
{/if}

<div class="page-wrap fade-in">
  <div class="page-header-row">
    <div><h1 class="page-title">Trading Manager</h1><p class="page-sub">Read-only operational view of the isolated PSX specialist and deterministic ledger</p></div>
    <div class="header-actions">
      {#if status}
        <button
          class="btn kill-switch-btn"
          class:active={status.killSwitch}
          on:click={toggleKillSwitch}
          disabled={killSwitchBusy}
        ><Power size={14} /> {status.killSwitch ? 'Kill switch: ACTIVE — click to clear' : 'Kill switch: clear — click to stop trading'}</button>
      {/if}
      <button class="btn btn-ghost" on:click={load} disabled={loading}><RefreshCw size={14} /> Refresh</button>
    </div>
  </div>

  {#if error}<div class="error-banner">{error}</div>{/if}
  {#if loading}<div class="loading-state">Loading trading state…</div>
  {:else if status}
    <div class:blocked={!status.liveExecutionReady} class="safety-banner">
      <ShieldAlert size={19} />
      <div><b>{status.liveExecutionReady ? 'Live safety gates ready' : 'Live execution blocked'}</b><span>{status.liveExecutionReady ? 'All reported live prerequisites are healthy.' : status.reconciliation.reason}</span></div>
    </div>

    <div class="status-grid">
      <div class="metric"><span>Mode</span><b>{status.policy.executionMode}</b><small>Auto execute: {status.policy.autoExecute ? 'on' : 'off'} · kill switch: {status.killSwitch ? 'active' : 'clear'}</small></div>
      <div class="metric"><span>Market</span><b class:good={status.market.isOpen}>{status.market.isOpen ? 'Open' : 'Closed'}</b><small>{status.market.reason}</small></div>
      <div class="metric"><span>Reconciliation</span><b class:good={status.reconciliation.healthy && status.reconciliationFresh}>{status.reconciliation.healthy && status.reconciliationFresh ? 'Healthy' : 'Unhealthy/stale'}</b><small>{date(status.reconciliation.checkedUtc)}</small></div>
      <div class="metric"><span>Pending proposals</span><b>{status.ledger.pendingProposals}</b><small>Policy {status.policy.version}</small></div>
      <div class="metric"><span>Accepted executions</span><b>{status.ledger.acceptedExecutions}</b><small>{status.ledger.submittingExecutions} submitting</small></div>
      <div class="metric"><span>Unknown outcomes</span><b class:danger={status.ledger.unknownExecutions > 0}>{status.ledger.unknownExecutions}</b><small>Require manual reconciliation</small></div>
    </div>

    <!-- Watchlist beside the archive: the two are related — the archive is what gives the watched
         symbols their weekly levels — and the panel owns its own loading and refresh. -->
    <div class="watch-row" class:expanded={chartExpanded}>
      <WatchlistPanel bind:this={watchlistPanel} bind:selected={selectedSymbol} />
      <ChartPane
        symbol={selectedSymbol}
        bind:expanded={chartExpanded}
        refreshTick={marketTick}
        marketOpen={status.market.isOpen}
        on:arm={(e) => armContext = e.detail}
      />
    </div>

    <div class="alerts-row">
      <ArmedOrdersPanel bind:this={armedPanel} refreshTick={marketTick} />
      <AlertsPanel
        on:select={(e) => selectedSymbol = e.detail}
        on:arm={(e) => armContext = e.detail}
        on:alertsChanged={() => watchlistPanel?.refresh()}
      />
    </div>

    {#if archive}
      <section class="archive-card">
        <div class="archive-head">
          <div class="archive-title">
            <Database size={16} />
            <div>
              <b>Candle archive</b>
              <span>Daily OHLC history that support/resistance and weekly levels are computed from</span>
            </div>
          </div>
          {#if archive.backfillEnabled}
            <button
              class="btn btn-ghost"
              on:click={startBackfill}
              disabled={backfillBusy || archive.progress.isRunning || archive.missingTradingDays === 0}
            ><Download size={14} />
              {#if archive.progress.isRunning}Backfilling…
              {:else if archive.missingTradingDays === 0}Archive complete
              {:else}Backfill {archive.missingTradingDays} days{/if}
            </button>
          {/if}
        </div>

        <div class="archive-stats">
          <div><span>Stored bars</span><b>{archive.archive.bars.toLocaleString()}</b></div>
          <div><span>Symbols</span><b>{archive.archive.symbols} / {archive.configuredSymbols}</b></div>
          <div><span>Coverage</span><b>{archive.archive.earliestSession ?? '—'} → {archive.archive.latestSession ?? '—'}</b></div>
          <div>
            <span>Missing days</span>
            <b class:danger={archive.missingTradingDays > 0} class:good={archive.missingTradingDays === 0}>
              {archive.missingTradingDays}{#if archive.targetTradingDays > 0} / {archive.targetTradingDays}{/if}
            </b>
          </div>
        </div>

        {#if archive.progress.isRunning}
          <div class="progress-wrap">
            <div class="progress-bar"><div style="width:{archive.progress.percentComplete ?? 0}%"></div></div>
            <small>
              {archive.progress.datesCompleted} / {archive.progress.datesTargeted} days
              ({archive.progress.percentComplete ?? 0}%) ·
              {archive.progress.sessionsStored} stored ·
              {archive.progress.emptyDates} non-trading
              {#if archive.progress.currentDate} · at {archive.progress.currentDate}{/if}
            </small>
          </div>
        {/if}

        {#if archive.progress.message}
          <p class="archive-note" class:warn={archive.progress.abortedForThrottling}>{archive.progress.message}</p>
        {/if}

        {#if archive.symbolsShortOfWeekly.length}
          <p class="archive-note warn">
            {archive.symbolsShortOfWeekly.length} symbol(s) cannot produce weekly levels yet:
            {#each archive.symbolsShortOfWeekly as gap, i}{i > 0 ? ', ' : ''}<b>{gap.symbol}</b>
              ({gap.archivedBars}/{archive.dailyBarsForWeekly} bars{#if gap.missingSessions}, {gap.missingSessions} sessions never requested{/if}){/each}.
            Coverage is tracked per symbol and date, so a symbol added after the deep history was
            archived is missing those dates even when every date is on record. Use the download action
            on its watchlist row to fetch just what it needs.
          </p>
        {/if}

        {#if !archive.backfillEnabled}
          <p class="archive-note">Backfill is disabled (<code>Scan.BackfillYears = 0</code>). Weekly levels
            need roughly two years of daily history; without it the analysis reports unknown alignment.</p>
        {:else if archive.configuredSymbols === 0}
          <p class="archive-note warn">No AllowedSymbols are configured, so there is nothing to archive.
            History is stored for the configured trading universe only.</p>
        {/if}
      </section>
    {/if}

    <div class="tabs">
      <button class:active={tab === 'proposals'} on:click={() => tab = 'proposals'}><FileText size={14}/> Proposals ({proposals.length})</button>
      <button class:active={tab === 'executions'} on:click={() => tab = 'executions'}><ListChecks size={14}/> Executions ({executions.length})</button>
      <button class:active={tab === 'reconciliation'} on:click={() => tab = 'reconciliation'}><Scale size={14}/> Reconciliation ({reconciliation.length})</button>
      <button class:active={tab === 'events'} on:click={() => tab = 'events'}><History size={14}/> Audit ({events.length})</button>
    </div>

    {#if tab === 'proposals'}
      <div class="records">
        <div class="inbox-head">
          <p>
            Proposals the specialist produced from signals that arrived while nobody was watching.
            Executing one hands its orders to the deterministic manager — policy, risk engine, market
            calendar and kill switch all still apply.
          </p>
          <label class="toggle">
            <input type="checkbox" bind:checked={showResolvedProposals} on:change={loadProposals} />
            show resolved
          </label>
        </div>

        {#each proposals as item (item.proposalId)}
          <article class="record" class:resolved={item.status !== 'proposed'}>
            <header>
              <b>{item.proposalId}</b>
              <span class="state">{item.status}</span>
            </header>
            <div class="meta">
              {date(item.createdUtc)} · policy {item.policyVersion}
              {#if item.executionId} · execution {item.executionId}{/if}
            </div>
            {#if item.proposal.rationale}<p>{item.proposal.rationale}</p>{/if}
            {#if item.stateReason}<p class="reason">{item.stateReason}</p>{/if}
            <pre>{json(item.proposal.orders ?? [])}</pre>

            {#if item.status === 'proposed'}
              <div class="record-actions">
                <button class="btn btn-primary" on:click={() => executeProposal(item)} disabled={proposalBusy !== null}>
                  <Play size={13} /> {proposalBusy === item.proposalId ? 'Executing…' : 'Execute'}
                </button>
                <button class="btn btn-danger" on:click={() => rejectProposal(item)} disabled={proposalBusy !== null}>
                  <XCircle size={13} /> Reject
                </button>
              </div>
            {/if}
          </article>
        {:else}
          <div class="empty">
            <Activity size={26}/>
            {showResolvedProposals ? 'No proposals recorded' : 'Inbox empty — nothing waiting on a decision'}
          </div>
        {/each}
      </div>
    {:else if tab === 'executions'}
      <div class="records">
        {#each executions as item}<article class="record"><header><b>{item.executionId}</b><span class="state">{item.state}</span></header><div class="meta">{date(item.createdUtc)} · policy {item.policyVersion}</div><pre>{json(item.result ?? item.request)}</pre></article>{:else}<div class="empty">No executions recorded</div>{/each}
      </div>
    {:else if tab === 'reconciliation'}
      <div class="records">
        {#each reconciliation as item}<article class="record"><header><b>{item.reconciliationId}</b><span class="state">{item.state}</span></header><div class="meta">{date(item.startedUtc)}</div><pre>{json(item.details)}</pre></article>{:else}<div class="empty">No reconciliation runs recorded</div>{/each}
      </div>
    {:else}
      <div class="records">
        {#each events as item}<article class="record"><header><b>{item.eventType}</b><span class="meta">#{item.eventId}</span></header><div class="meta">Execution {item.executionId} · {date(item.createdUtc)}</div><pre>{json(item.payload)}</pre></article>{:else}<div class="empty">No audit events recorded</div>{/each}
      </div>
    {/if}
  {/if}
</div>

<style>
  .page-header-row { display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; margin-bottom:1.25rem; }
  .header-actions { display:flex; gap:.6rem; align-items:center; }
  .kill-switch-btn { display:flex; align-items:center; gap:.4rem; border:1px solid rgba(52,211,153,.35); background:rgba(52,211,153,.08); color:var(--success); border-radius:var(--radius); padding:.55rem .9rem; font-size:.78rem; font-weight:600; cursor:pointer; }
  .kill-switch-btn.active { border-color:rgba(248,113,113,.45); background:rgba(248,113,113,.15); color:var(--danger); }
  .kill-switch-btn:disabled { opacity:.6; cursor:wait; }
  .error-banner,.safety-banner { border:1px solid rgba(52,211,153,.25); background:rgba(52,211,153,.08); padding:.9rem 1rem; border-radius:var(--radius); margin-bottom:1rem; display:flex; gap:.75rem; align-items:center; color:var(--success); }
  .error-banner,.safety-banner.blocked { border-color:rgba(248,113,113,.3); background:rgba(248,113,113,.08); color:var(--danger); }
  .safety-banner div { display:flex; flex-direction:column; gap:.2rem; }.safety-banner span { font-size:.75rem; color:var(--text-2); }
  .loading-state,.empty { color:var(--text-3); padding:2rem; text-align:center; }
  .status-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(180px,1fr)); gap:.75rem; margin-bottom:1.25rem; }
  .metric { background:var(--surface); border:1px solid var(--border); border-radius:var(--radius); padding:1rem; display:flex; flex-direction:column; gap:.35rem; }
  .metric span,.metric small { color:var(--text-3); font-size:.7rem; }.metric b { color:var(--text); font-size:1.05rem; }.metric b.good { color:var(--success); }.metric b.danger { color:var(--danger); }
  /* minmax(0,1fr) not 1fr: the chart column must be allowed to shrink, or the canvas keeps its
     widest measured size and pushes the grid wider on every re-render. */
  .watch-row { display:grid; grid-template-columns:minmax(260px,320px) minmax(0,1fr); gap:.75rem; margin-bottom:1.25rem; align-items:stretch; }
  @media (max-width: 820px) { .watch-row { grid-template-columns:minmax(0,1fr); } }

  /* Expanded: the chart takes the full width and the watchlist STACKS BENEATH it rather than being
     hidden — losing the ability to switch symbols would be a poor trade for the extra width. */
  .watch-row.expanded { grid-template-columns:minmax(0,1fr); }
  .watch-row.expanded :global(> section:first-child) { order:2; height:auto; }
  .watch-row.expanded :global(> section:first-child .rows) { flex:none; max-height:min(52vh,420px); }
  .archive-card { background:var(--surface); border:1px solid var(--border); border-radius:var(--radius); padding:1rem; margin-bottom:1.25rem; display:flex; flex-direction:column; gap:.85rem; }
  .archive-head { display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; flex-wrap:wrap; }
  .archive-title { display:flex; gap:.6rem; align-items:flex-start; color:var(--primary); }
  .archive-title div { display:flex; flex-direction:column; gap:.2rem; }
  .archive-title b { color:var(--text); font-size:.9rem; }
  .archive-title span { color:var(--text-3); font-size:.72rem; }
  .archive-head .btn { display:flex; align-items:center; gap:.4rem; white-space:nowrap; }
  .archive-stats { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:.6rem; }
  .archive-stats div { display:flex; flex-direction:column; gap:.25rem; background:var(--surface-2); border-radius:var(--radius-sm); padding:.6rem .7rem; }
  .archive-stats span { color:var(--text-3); font-size:.68rem; }
  .archive-stats b { color:var(--text); font-size:.82rem; overflow-wrap:anywhere; }
  .archive-stats b.good { color:var(--success); } .archive-stats b.danger { color:var(--warning, #fbbf24); }
  .progress-wrap { display:flex; flex-direction:column; gap:.35rem; }
  .progress-bar { height:6px; background:var(--surface-2); border-radius:999px; overflow:hidden; }
  .progress-bar div { height:100%; background:var(--primary); transition:width .4s ease; }
  .progress-wrap small { color:var(--text-3); font-size:.68rem; }
  .archive-note { color:var(--text-2); font-size:.72rem; margin:0; }
  .archive-note.warn { color:var(--warning, #fbbf24); }
  .archive-note code { background:var(--surface-2); padding:.1rem .3rem; border-radius:3px; font-size:.68rem; }
  .tabs { display:flex; flex-wrap:wrap; gap:.4rem; border-bottom:1px solid var(--border); margin-bottom:1rem; }
  .tabs button { display:flex; gap:.4rem; align-items:center; border:0; border-bottom:2px solid transparent; background:none; color:var(--text-2); padding:.7rem .8rem; cursor:pointer; }.tabs button.active { color:var(--primary); border-bottom-color:var(--primary); }
  .inbox-head { display:flex; justify-content:space-between; align-items:flex-start; gap:1rem; flex-wrap:wrap; }
  .inbox-head p { margin:0; color:var(--text-3); font-size:.72rem; line-height:1.55; max-width:70ch; }
  .inbox-head .toggle { display:flex; align-items:center; gap:.3rem; color:var(--text-3); font-size:.7rem; cursor:pointer; white-space:nowrap; }
  .record.resolved { opacity:.6; }
  .record .reason { color:var(--warning); font-size:.73rem; }
  .record-actions { display:flex; gap:.5rem; margin-top:.6rem; }
  .record-actions .btn { display:flex; align-items:center; gap:.35rem; }
  .records { display:flex; flex-direction:column; gap:.7rem; }.record { background:var(--surface); border:1px solid var(--border); border-radius:var(--radius); padding:1rem; }.record header { display:flex; justify-content:space-between; gap:1rem; color:var(--text); }.record header b { font-family:monospace; font-size:.8rem; overflow-wrap:anywhere; }.state { color:var(--primary); text-transform:uppercase; font-size:.68rem; }.meta { color:var(--text-3); font-size:.68rem; margin-top:.35rem; }.record p { color:var(--text-2); font-size:.8rem; }.record pre { background:var(--surface-2); border-radius:var(--radius-sm); padding:.7rem; max-height:240px; overflow:auto; color:var(--text-2); font-size:.7rem; white-space:pre-wrap; }

  @media (max-width: 640px) {
    .page-header-row { flex-direction:column; align-items:stretch; margin-bottom:1rem; }
    .header-actions { flex-wrap:wrap; }
    .kill-switch-btn { flex:1 1 230px; white-space:normal; justify-content:center; }
    .safety-banner { align-items:flex-start; }
    .status-grid { grid-template-columns:repeat(auto-fit,minmax(145px,1fr)); gap:.5rem; }
    .metric { padding:.75rem; }
    .archive-stats { grid-template-columns:repeat(auto-fit,minmax(125px,1fr)); }
    .tabs { flex-wrap:nowrap; overflow-x:auto; }
    .tabs button { flex:0 0 auto; padding-inline:.6rem; }
  }

  @media (max-width: 420px) {
    .status-grid, .archive-stats { grid-template-columns:minmax(0,1fr); }
  }
</style>
