<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { Database, RefreshCw, Search, Filter } from 'lucide-svelte';
  import type { MemoryEntry } from '$lib/api';

  let entries: MemoryEntry[] = [];
  let filtered: MemoryEntry[] = [];
  let loading = true;
  let error: string | null = null;
  let search = '';
  let typeFilter = 'all';

  const types = ['all', 'Fact', 'UserPreference', 'Observation', 'Conversation', 'ToolExecution', 'SubAgentResult'];

  async function load() {
    loading = true;
    error = null;
    try {
      entries = await api.memory();
      applyFilter();
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
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
</style>
