<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { tools } from '$lib/stores';
  import { Wrench, RefreshCw, Search, Terminal, Globe, FolderOpen, Brain, Calculator, Clock, Hash, Info } from 'lucide-svelte';

  let loading = true;
  let error: string | null = null;
  let search = '';

  const categoryMap: Record<string, { icon: typeof Wrench; color: string; bg: string }> = {
    shell:       { icon: Terminal,   color: 'var(--warning)',  bg: 'rgba(251,191,36,0.12)'  },
    web:         { icon: Globe,      color: 'var(--info)',     bg: 'rgba(96,165,250,0.12)'  },
    file:        { icon: FolderOpen, color: 'var(--accent)',   bg: 'rgba(167,139,250,0.12)' },
    memory:      { icon: Brain,      color: 'var(--primary)',  bg: 'var(--primary-dim)'     },
    calculator:  { icon: Calculator, color: 'var(--success)',  bg: 'rgba(52,211,153,0.12)'  },
    timestamp:   { icon: Clock,      color: 'var(--text-2)',   bg: 'var(--surface-2)'       },
    uuid:        { icon: Hash,       color: 'var(--text-2)',   bg: 'var(--surface-2)'       },
    default:     { icon: Wrench,     color: 'var(--text-2)',   bg: 'var(--surface-2)'       },
  };

  function getCategory(name: string) {
    const n = name.toLowerCase();
    if (n.includes('shell') || n.includes('command')) return 'shell';
    if (n.includes('web') || n.includes('fetch') || n.includes('search')) return 'web';
    if (n.includes('file') || n.includes('read') || n.includes('write') || n.includes('list') || n.includes('directory') || n.includes('delete')) return 'file';
    if (n.includes('memory')) return 'memory';
    if (n.includes('calc')) return 'calculator';
    if (n.includes('time') || n.includes('stamp')) return 'timestamp';
    if (n.includes('uuid') || n.includes('guid')) return 'uuid';
    return 'default';
  }

  async function load() {
    loading = true;
    error = null;
    try {
      tools.set(await api.tools());
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(load);

  $: toolList = $tools;
  $: filtered = toolList.filter(t =>
    !search ||
    t.name.toLowerCase().includes(search.toLowerCase()) ||
    t.description.toLowerCase().includes(search.toLowerCase())
  );

  let selected: typeof toolList[0] | null = null;
</script>

<div class="page-wrap fade-in">
  <div class="page-header-row">
    <div>
      <h1 class="page-title">Tools</h1>
      <p class="page-sub">
        Registered tool registry — capabilities the agent can call
        {#if !loading}<span class="count-pill">{toolList.length} tools</span>{/if}
      </p>
    </div>
    <button class="btn btn-ghost" on:click={load} disabled={loading}>
      <RefreshCw size={14} />
      Refresh
    </button>
  </div>

  <!-- Search -->
  <div class="search-wrap" style="margin-bottom: 1.25rem">
    <Search size={14} class="search-icon" />
    <input
      class="input"
      placeholder="Search tools…"
      bind:value={search}
      style="padding-left: 2rem"
    />
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {:else if loading}
    <div class="tools-grid">
      {#each Array(8) as _}
        <div class="skeleton-card"></div>
      {/each}
    </div>
  {:else if filtered.length === 0}
    <div class="empty-state">
      <Wrench size={40} />
      <h3>{search ? 'No results' : 'No tools registered'}</h3>
      <p>{search ? 'Try a different search' : 'Tools are auto-registered from ToolsConfig'}</p>
    </div>
  {:else}
    <div class="tools-grid">
      {#each filtered as tool (tool.name)}
        {@const cat = categoryMap[getCategory(tool.name)]}
        <button
          class="tool-card card card-hover"
          class:active={selected?.name === tool.name}
          on:click={() => selected = selected?.name === tool.name ? null : tool}
        >
          <div class="tool-icon" style="background:{cat.bg}; color:{cat.color}">
            <svelte:component this={cat.icon} size={16} />
          </div>
          <div class="tool-info">
            <span class="tool-name">{tool.name}</span>
            <span class="tool-desc">{tool.description}</span>
          </div>
          <Info size={13} style="color: var(--text-3); flex-shrink:0; opacity:{selected?.name === tool.name ? 1 : 0.4}" />
        </button>
      {/each}
    </div>

    <!-- Detail panel -->
    {#if selected}
      {@const cat = categoryMap[getCategory(selected.name)]}
      <div class="detail-panel fade-in">
        <div class="detail-header">
          <div class="tool-icon" style="background:{cat.bg}; color:{cat.color}">
            <svelte:component this={cat.icon} size={18} />
          </div>
          <div>
            <div class="detail-name">{selected.name}</div>
            <div class="detail-category">{getCategory(selected.name)}</div>
          </div>
          <button class="btn btn-ghost" style="margin-left:auto" on:click={() => selected = null}>Close</button>
        </div>
        <div class="detail-body">
          <p class="detail-desc">{selected.description}</p>
        </div>
      </div>
    {/if}
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

  .count-pill {
    display: inline-block;
    background: var(--surface-2);
    color: var(--text-3);
    font-size: 0.6875rem;
    padding: 0.1em 0.4em;
    border-radius: 4px;
    margin-left: 0.375rem;
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

  .error-banner {
    background: rgba(248,113,113,0.1);
    border: 1px solid rgba(248,113,113,0.25);
    border-radius: var(--radius);
    padding: 0.75rem 1rem;
    color: var(--danger);
    font-size: 0.8125rem;
    margin-bottom: 1rem;
  }

  .tools-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
    gap: 0.5rem;
    margin-bottom: 1rem;
  }

  .tool-card {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    text-align: left;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 0.75rem 0.875rem;
    cursor: pointer;
    transition: all 0.15s;
    width: 100%;
  }
  .tool-card:hover { border-color: var(--border-md); background: var(--surface-2); }
  .tool-card.active { border-color: var(--primary); background: var(--primary-dim); }

  .tool-icon {
    width: 34px;
    height: 34px;
    border-radius: 8px;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .tool-info {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }

  .tool-name {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .tool-desc {
    font-size: 0.75rem;
    color: var(--text-2);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* Detail panel */
  .detail-panel {
    background: var(--surface);
    border: 1px solid var(--border-md);
    border-radius: var(--radius);
    padding: 1.25rem;
    margin-top: 0.5rem;
  }

  .detail-header {
    display: flex;
    align-items: center;
    gap: 0.875rem;
    margin-bottom: 1rem;
  }

  .detail-name {
    font-size: 1rem;
    font-weight: 600;
    color: var(--text);
  }

  .detail-category {
    font-size: 0.6875rem;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .detail-body { display: flex; flex-direction: column; gap: 0.875rem; }

  .detail-desc {
    font-size: 0.875rem;
    color: var(--text-2);
    line-height: 1.6;
    margin: 0;
  }

  .skeleton-card {
    height: 68px;
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
