<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { agents } from '$lib/stores';
  import StatusBadge from '$lib/components/StatusBadge.svelte';
  import { Bot, RefreshCw, Users, Cpu, Activity } from 'lucide-svelte';
  import type { AgentInfo, SpecialistAgentInfo, CommandQueueStatus } from '$lib/api';

  let loading = true;
  let error: string | null = null;
  let specialists: SpecialistAgentInfo[] = [];
  let queues: CommandQueueStatus | null = null;

  async function load() {
    loading = true;
    error = null;
    try {
      const [data, specialistData, queueData] = await Promise.all([
        api.agents(), api.specialistAgents(), api.commandQueues()
      ]);
      agents.set(data);
      specialists = specialistData;
      queues = queueData;
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(load);

  $: agentList = $agents;
  $: main  = agentList.find(a => a.role === 'main');
  $: subs  = agentList.filter(a => a.role === 'sub');
</script>

<div class="page-wrap fade-in">
  <div class="page-header">
    <div>
      <h1 class="page-title">Agents</h1>
      <p class="page-sub">Active agent hierarchy — main agent and spawned sub-agents</p>
    </div>
    <button class="btn btn-ghost" on:click={load} disabled={loading}>
      <RefreshCw size={14} class={loading ? 'spin-icon' : ''} />
      Refresh
    </button>
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {:else if loading}
    <div class="grid-2">
      {#each [1, 2] as _}
        <div class="card skeleton-card"></div>
      {/each}
    </div>
  {:else if agentList.length === 0}
    <div class="empty-state">
      <Bot size={40} />
      <h3>No agents found</h3>
      <p>The main agent initializes when AgentFox starts</p>
    </div>
  {:else}
    <!-- Main agent -->
    {#if main}
      <section class="section">
        <h2 class="section-heading">
          <Cpu size={15} />
          Main Agent
        </h2>
        <div class="agent-card main-agent">
          <div class="agent-left">
            <div class="agent-avatar main">
              <Bot size={22} />
            </div>
            <div>
              <div class="agent-name">{main.name}</div>
              <div class="agent-id">{main.id}</div>
            </div>
          </div>
          <div class="agent-right">
            <div class="agent-stat">
              <span class="agent-stat-val">{main.subAgentCount ?? 0}</span>
              <span class="agent-stat-lbl">sub-agents</span>
            </div>
            <StatusBadge status={main.status} />
          </div>
        </div>
      </section>
    {/if}

    <section class="section">
      <h2 class="section-heading">
        <Bot size={15} /> Specialist Agents
        <span class="badge badge-neutral">{specialists.length}</span>
      </h2>
      {#if specialists.length === 0}
        <div class="empty-state" style="padding: 2rem"><p>No specialist agents registered</p></div>
      {:else}
        <div class="specialist-grid">
          {#each specialists as specialist}
            <div class="card specialist-card">
              <div class="specialist-head">
                <div><div class="agent-name">{specialist.name}</div><div class="agent-id">{specialist.id}</div></div>
                <StatusBadge status={specialist.isActive ? 'active' : 'inactive'} />
              </div>
              <p class="specialist-description">{specialist.description}</p>
              <div class="metrics">
                <span><b>{specialist.activeTurns}</b> active</span>
                <span><b>{specialist.waitingTurns}</b> waiting</span>
                <span><b>{specialist.totalTurns}</b> turns</span>
                <span><b>{specialist.failedTurns}</b> failed</span>
              </div>
              <div class="chips">
                {#each specialist.toolNames as tool}<span class="chip">{tool}</span>{/each}
              </div>
              <div class="specialist-meta">Routes: {specialist.channelTypes.join(', ') || 'main-agent delegation only'} · concurrency {specialist.maxConcurrentTurns}</div>
              {#if specialist.lastError}<div class="specialist-error">{specialist.lastError}</div>{/if}
            </div>
          {/each}
        </div>
      {/if}
    </section>

    {#if queues}
      <section class="section">
        <h2 class="section-heading"><Activity size={15} /> Command Queues</h2>
        <div class="queue-grid">
          {#each queues.lanes as lane}
            <div class="queue-card">
              <div class="queue-name">{lane.lane}</div>
              <div class="queue-counts"><b>{lane.activeCommands}</b> active · <b>{lane.queuedCommands}</b> queued</div>
              <div class="specialist-meta">limit {lane.maxConcurrency} · {lane.handlerRegistered ? 'handler ready' : 'no handler'}</div>
            </div>
          {/each}
        </div>
      </section>
    {/if}

    <!-- Sub-agents -->
    {#if subs.length > 0}
      <section class="section">
        <h2 class="section-heading">
          <Users size={15} />
          Sub-Agents
          <span class="badge badge-neutral">{subs.length}</span>
        </h2>
        <div class="sub-grid">
          {#each subs as sub}
            <div class="sub-card card card-hover">
              <div class="sub-header">
                <div class="agent-avatar sub">
                  <Bot size={15} />
                </div>
                <div class="sub-info">
                  <span class="sub-name">{sub.name}</span>
                  <span class="sub-id">{sub.id}</span>
                </div>
                <StatusBadge status={sub.status} />
              </div>
            </div>
          {/each}
        </div>
      </section>
    {:else if main}
      <section class="section">
        <h2 class="section-heading">
          <Users size={15} />
          Sub-Agents
        </h2>
        <div class="empty-state" style="padding: 2rem">
          <Activity size={28} />
          <h3>No active sub-agents</h3>
          <p>Sub-agents are spawned dynamically during complex tasks</p>
        </div>
      </section>
    {/if}
  {/if}
</div>

<style>
  .page-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    margin-bottom: 1.75rem;
    gap: 1rem;
  }

  .error-banner {
    background: rgba(248,113,113,0.1);
    border: 1px solid rgba(248,113,113,0.25);
    border-radius: var(--radius);
    padding: 0.75rem 1rem;
    color: var(--danger);
    font-size: 0.8125rem;
    margin-bottom: 1rem;
  }

  .section { margin-bottom: 1.75rem; }

  .section-heading {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.8125rem;
    font-weight: 600;
    color: var(--text-2);
    margin: 0 0 0.875rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  /* Main agent card */
  .agent-card {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1.25rem 1.5rem;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
  }

  .main-agent {
    background: linear-gradient(135deg, rgba(129,140,248,0.05), rgba(167,139,250,0.03));
    border-color: rgba(129,140,248,0.2);
  }

  .agent-left { display: flex; align-items: center; gap: 1rem; }
  .agent-right { display: flex; align-items: center; gap: 1.5rem; }

  .agent-avatar {
    width: 48px;
    height: 48px;
    border-radius: 12px;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }
  .agent-avatar.main {
    background: linear-gradient(135deg, var(--primary), var(--accent));
    color: #fff;
    box-shadow: 0 4px 16px rgba(129,140,248,0.3);
  }
  .agent-avatar.sub {
    background: var(--surface-2);
    color: var(--text-2);
    width: 32px;
    height: 32px;
    border-radius: 8px;
  }

  .agent-name {
    font-size: 1rem;
    font-weight: 600;
    color: var(--text);
  }

  .agent-id {
    font-size: 0.6875rem;
    color: var(--text-3);
    font-family: monospace;
    margin-top: 2px;
  }

  .agent-stat {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 1px;
  }
  .agent-stat-val {
    font-size: 1.25rem;
    font-weight: 700;
    color: var(--text);
    line-height: 1;
  }
  .agent-stat-lbl {
    font-size: 0.6875rem;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.05em;
  }

  /* Sub-agents grid */
  .sub-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
    gap: 0.75rem;
  }

  .sub-card { padding: 0.875rem 1rem; }

  .specialist-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 0.75rem; }
  .specialist-card { padding: 1rem; }
  .specialist-head { display: flex; align-items: center; justify-content: space-between; gap: 1rem; }
  .specialist-description { color: var(--text-2); font-size: 0.78rem; line-height: 1.45; }
  .metrics { display: flex; flex-wrap: wrap; gap: 0.75rem; color: var(--text-3); font-size: 0.72rem; margin: 0.75rem 0; }
  .metrics b { color: var(--text); }
  .chips { display: flex; flex-wrap: wrap; gap: 0.35rem; }
  .chip { background: var(--surface-2); border: 1px solid var(--border); padding: 0.2rem 0.4rem; border-radius: 4px; font-size: 0.65rem; color: var(--text-2); font-family: monospace; }
  .specialist-meta { color: var(--text-3); font-size: 0.68rem; margin-top: 0.65rem; }
  .specialist-error { color: var(--danger); font-size: 0.72rem; margin-top: 0.5rem; }
  .queue-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 0.6rem; }
  .queue-card { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 0.75rem; }
  .queue-name { font-weight: 600; color: var(--text); font-size: 0.8rem; }
  .queue-counts { color: var(--text-2); font-size: 0.72rem; margin-top: 0.35rem; }

  .sub-header {
    display: flex;
    align-items: center;
    gap: 0.625rem;
  }

  .sub-info {
    flex: 1;
    min-width: 0;
  }

  .sub-name {
    display: block;
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .sub-id {
    display: block;
    font-size: 0.6875rem;
    color: var(--text-3);
    font-family: monospace;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .skeleton-card {
    height: 100px;
    background: linear-gradient(90deg, var(--surface) 25%, var(--surface-2) 50%, var(--surface) 75%);
    background-size: 200% 100%;
    animation: shimmer 1.5s infinite;
  }

  :global(.spin-icon) {
    animation: spin 0.7s linear infinite;
  }

  @keyframes shimmer {
    0%   { background-position: 200% 0; }
    100% { background-position: -200% 0; }
  }
</style>
