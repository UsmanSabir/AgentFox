<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { Database, RefreshCw, Search, Filter, Brain, Power } from 'lucide-svelte';
  import type { MemoryEntry, MemorySettings, SpecialistMemoryMode } from '$lib/api';

  let entries: MemoryEntry[] = [];
  let filtered: MemoryEntry[] = [];
  let loading = true;
  let error: string | null = null;
  let search = '';
  let typeFilter = 'all';
  let settings: MemorySettings | null = null;
  let savingGlobal = false;
  let savingAgent: string | null = null;

  const types = ['all', 'Fact', 'UserPreference', 'Observation', 'Conversation', 'ToolExecution', 'SubAgentResult'];

  async function load() {
    loading = true;
    error = null;
    try {
      const [memoryEntries, memorySettings] = await Promise.all([
        api.memory(),
        api.memorySettings()
      ]);
      entries = memoryEntries;
      settings = memorySettings;
      applyFilter();
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  async function toggleGlobalMemory() {
    if (!settings || savingGlobal) return;
    savingGlobal = true;
    try {
      const result = await api.setGlobalMemory(!settings.globalEnabled);
      settings = { ...settings, globalEnabled: result.globalEnabled };
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      savingGlobal = false;
    }
  }

  async function changeAgentMemory(agentId: string, event: Event) {
    if (!settings) return;
    const mode = (event.currentTarget as HTMLSelectElement).value as SpecialistMemoryMode;
    savingAgent = agentId;
    try {
      const result = await api.setSpecialistMemory(agentId, mode);
      settings = {
        ...settings,
        agents: settings.agents.map(agent =>
          agent.id === agentId ? { ...agent, mode: result.mode } : agent)
      };
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      savingAgent = null;
    }
  }

  function applyFilter() {
    filtered = entries.filter(e => {
      const matchType = typeFilter === 'all' || e.type === typeFilter;
      const matchSearch = !search || e.content.toLowerCase().includes(search.toLowerCase());
      return matchType && matchSearch;
    });
  }

  $: { search; typeFilter; applyFilter(); }

  onMount(load);

  function timeAgo(ts: string) {
    const diff = Date.now() - new Date(ts).getTime();
    const m = Math.floor(diff / 60000);
    if (m < 1)  return 'just now';
    if (m < 60) return `${m}m ago`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ago`;
    return `${Math.floor(h / 24)}d ago`;
  }

  const typeColors: Record<string, { bg: string; color: string }> = {
    Fact:           { bg: 'rgba(129,140,248,0.12)', color: 'var(--primary)' },
    UserPreference: { bg: 'rgba(167,139,250,0.12)', color: 'var(--accent)' },
    Observation:    { bg: 'rgba(96,165,250,0.12)',  color: 'var(--info)' },
    Conversation:   { bg: 'var(--surface-3)',        color: 'var(--text-2)' },
    ToolExecution:  { bg: 'rgba(251,191,36,0.12)',   color: 'var(--warning)' },
    SubAgentResult: { bg: 'rgba(52,211,153,0.12)',   color: 'var(--success)' },
  };

  function tc(type: string) {
    return typeColors[type] ?? { bg: 'var(--surface-2)', color: 'var(--text-3)' };
  }

  function importanceBar(v: number) {
    const pct = Math.round(v * 100);
    const color = v > 0.7 ? 'var(--success)' : v > 0.4 ? 'var(--warning)' : 'var(--text-3)';
    return { pct, color };
  }
</script>

<div class="page-wrap fade-in">
  <div class="page-header-row">
    <div>
      <h1 class="page-title">Memory</h1>
      <p class="page-sub">
        Long-term and short-term memories stored by the agent
        {#if !loading}<span class="count-badge">{filtered.length} / {entries.length}</span>{/if}
      </p>
    </div>
    <button class="btn btn-ghost" on:click={load} disabled={loading}>
      <RefreshCw size={14} />
      Refresh
    </button>
  </div>

  {#if settings}
    <section class="memory-controls">
      <div class="memory-master">
        <div class="control-icon" class:enabled={settings.globalEnabled}>
          <Power size={16} />
        </div>
        <div class="control-copy">
          <strong>Agent memory</strong>
          <span>Global privacy switch for recall and memory tools across every session and specialist.</span>
        </div>
        <button
          class="toggle-switch"
          class:on={settings.globalEnabled}
          on:click={toggleGlobalMemory}
          disabled={savingGlobal}
          role="switch"
          aria-checked={settings.globalEnabled}
          aria-label="Toggle agent memory globally"
        ><span></span></button>
      </div>

      {#if settings.agents.length > 0}
        <div class="specialist-controls" class:disabled={!settings.globalEnabled}>
          <div class="specialist-heading">
            <Brain size={14} />
            <div>
              <strong>Specialist memory</strong>
              <span>Shared uses AgentFox memory; isolated stores private specialist memories.</span>
            </div>
          </div>
          {#each settings.agents as agent (agent.id)}
            <label class="specialist-row">
              <span>{agent.name}</span>
              <select
                value={agent.mode}
                on:change={(event) => changeAgentMemory(agent.id, event)}
                disabled={!settings.globalEnabled || savingAgent === agent.id}
              >
                <option value="Shared">Shared</option>
                <option value="Isolated">Isolated</option>
                <option value="Disabled">Disabled</option>
              </select>
            </label>
          {/each}
        </div>
      {/if}
    </section>
  {/if}

  <!-- Filters -->
  <div class="filters">
    <div class="search-wrap">
      <Search size={14} class="search-icon" />
      <input
        class="input search-input"
        placeholder="Search memory…"
        bind:value={search}
        style="padding-left: 2rem"
      />
    </div>
    <div class="type-filters">
      <Filter size={13} style="color: var(--text-3); flex-shrink:0" />
      {#each types as t}
        <button
          class="type-chip"
          class:active={typeFilter === t}
          on:click={() => typeFilter = t}
        >{t === 'all' ? 'All' : t}</button>
      {/each}
    </div>
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {:else if loading}
    <div class="entry-list">
      {#each [1,2,3,4,5] as _}
        <div class="skeleton-entry"></div>
      {/each}
    </div>
  {:else if filtered.length === 0}
    <div class="empty-state">
      <Database size={40} />
      <h3>{search || typeFilter !== 'all' ? 'No results' : 'No memories yet'}</h3>
      <p>{search || typeFilter !== 'all' ? 'Try adjusting your filters' : 'The agent will store facts and context here'}</p>
    </div>
  {:else}
    <div class="entry-list">
      {#each filtered as entry (entry.id)}
        <div class="entry-card fade-in">
          <div class="entry-top">
            <span
              class="type-badge"
              style="background:{tc(entry.type).bg}; color:{tc(entry.type).color}"
            >{entry.type}</span>
            <span class="entry-time">{timeAgo(entry.timestamp)}</span>
            <div class="importance-wrap" title="Importance: {(entry.importance*100).toFixed(0)}%">
              <div class="importance-track">
                <div
                  class="importance-fill"
                  style="width:{importanceBar(entry.importance).pct}%; background:{importanceBar(entry.importance).color}"
                ></div>
              </div>
              <span class="importance-val">{importanceBar(entry.importance).pct}%</span>
            </div>
          </div>
          <p class="entry-content">{entry.content}</p>
          <div class="entry-id">{entry.id}</div>
        </div>
      {/each}
    </div>
  {/if}
</div>

<style>
  .memory-controls {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(260px, 0.8fr);
    gap: 0.75rem;
    margin-bottom: 1rem;
  }
  .memory-master, .specialist-controls {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.9rem 1rem;
  }
  .memory-master { display: flex; align-items: center; gap: 0.75rem; }
  .control-icon {
    width: 32px; height: 32px; border-radius: 8px;
    display: grid; place-items: center;
    background: var(--surface-3); color: var(--text-3);
  }
  .control-icon.enabled { background: rgba(52,211,153,0.12); color: var(--success); }
  .control-copy { min-width: 0; display: flex; flex-direction: column; gap: 0.18rem; }
  .control-copy strong, .specialist-heading strong { color: var(--text); font-size: 0.8rem; }
  .control-copy span, .specialist-heading span { color: var(--text-3); font-size: 0.68rem; line-height: 1.35; }
  .toggle-switch {
    margin-left: auto; width: 38px; height: 22px; padding: 2px;
    border: 0; border-radius: 99px; background: var(--surface-3); cursor: pointer;
    transition: background 0.15s;
  }
  .toggle-switch span {
    display: block; width: 18px; height: 18px; border-radius: 50%;
    background: var(--text-3); transition: transform 0.15s, background 0.15s;
  }
  .toggle-switch.on { background: rgba(52,211,153,0.3); }
  .toggle-switch.on span { transform: translateX(16px); background: var(--success); }
  .toggle-switch:disabled { opacity: 0.55; cursor: wait; }
  .specialist-controls { display: flex; flex-direction: column; gap: 0.65rem; }
  .specialist-controls.disabled { opacity: 0.55; }
  .specialist-heading { display: flex; align-items: flex-start; gap: 0.5rem; }
  .specialist-heading > div { display: flex; flex-direction: column; gap: 0.15rem; }
  .specialist-row { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; font-size: 0.75rem; color: var(--text-2); }
  .specialist-row select {
    background: var(--surface-2); color: var(--text); border: 1px solid var(--border-md);
    border-radius: var(--radius-sm); padding: 0.3rem 1.6rem 0.3rem 0.45rem; font-size: 0.72rem;
  }

  .page-header-row {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    margin-bottom: 1.25rem;
    gap: 1rem;
  }

  .count-badge {
    display: inline-block;
    font-size: 0.6875rem;
    background: var(--surface-2);
    color: var(--text-3);
    padding: 0.1em 0.4em;
    border-radius: 4px;
    margin-left: 0.375rem;
  }

  .filters {
    display: flex;
    flex-direction: column;
    gap: 0.625rem;
    margin-bottom: 1.25rem;
  }

  .search-wrap {
    position: relative;
  }
  .search-wrap :global(.search-icon) {
    position: absolute;
    left: 0.625rem;
    top: 50%;
    transform: translateY(-50%);
    color: var(--text-3);
    pointer-events: none;
  }

  .type-filters {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    flex-wrap: wrap;
  }

  .type-chip {
    padding: 0.2rem 0.625rem;
    border-radius: 99px;
    font-size: 0.6875rem;
    background: var(--surface-2);
    border: 1px solid var(--border);
    color: var(--text-2);
    cursor: pointer;
    transition: all 0.15s;
    white-space: nowrap;
  }
  .type-chip:hover  { border-color: var(--border-md); color: var(--text); }
  .type-chip.active { background: var(--primary-dim); border-color: rgba(129,140,248,0.3); color: var(--primary); }

  .error-banner {
    background: rgba(248,113,113,0.1);
    border: 1px solid rgba(248,113,113,0.25);
    border-radius: var(--radius);
    padding: 0.75rem 1rem;
    color: var(--danger);
    font-size: 0.8125rem;
    margin-bottom: 1rem;
  }

  .entry-list {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .entry-card {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.875rem 1rem;
    transition: border-color 0.15s;
  }
  .entry-card:hover { border-color: var(--border-md); }

  .entry-top {
    display: flex;
    align-items: center;
    gap: 0.625rem;
    margin-bottom: 0.5rem;
  }

  .type-badge {
    padding: 0.125rem 0.5rem;
    border-radius: 99px;
    font-size: 0.6875rem;
    font-weight: 500;
  }

  .entry-time {
    font-size: 0.6875rem;
    color: var(--text-3);
    margin-left: auto;
  }

  .importance-wrap {
    display: flex;
    align-items: center;
    gap: 0.375rem;
  }

  .importance-track {
    width: 48px;
    height: 4px;
    background: var(--surface-3);
    border-radius: 2px;
    overflow: hidden;
  }

  .importance-fill {
    height: 100%;
    border-radius: 2px;
    transition: width 0.3s;
  }

  .importance-val {
    font-size: 0.6875rem;
    color: var(--text-3);
    width: 28px;
    text-align: right;
  }

  .entry-content {
    font-size: 0.875rem;
    color: var(--text);
    line-height: 1.55;
    margin: 0 0 0.375rem;
    white-space: pre-wrap;
    word-break: break-word;
  }

  .entry-id {
    font-size: 0.625rem;
    color: var(--text-3);
    font-family: monospace;
  }

  .skeleton-entry {
    height: 80px;
    border-radius: var(--radius);
    background: linear-gradient(90deg, var(--surface) 25%, var(--surface-2) 50%, var(--surface) 75%);
    background-size: 200% 100%;
    animation: shimmer 1.5s infinite;
  }

  @keyframes shimmer {
    0%   { background-position: 200% 0; }
    100% { background-position: -200% 0; }
  }

  @media (max-width: 760px) {
    .memory-controls { grid-template-columns: 1fr; }
  }
</style>
