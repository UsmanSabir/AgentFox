<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { skills } from '$lib/stores';
  import { Puzzle, RefreshCw, Search, Wrench } from 'lucide-svelte';

  let loading = true;
  let error: string | null = null;
  let search = '';

  async function load() {
    loading = true;
    error = null;
    try {
      skills.set(await api.skills());
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(load);

  $: skillList = $skills;
  $: filtered = skillList.filter(s =>
    !search ||
    s.name.toLowerCase().includes(search.toLowerCase()) ||
    s.description.toLowerCase().includes(search.toLowerCase())
  );

  $: composioCount = filtered.filter(s => s.skillType === 'composio').length;
  $: localCount    = filtered.filter(s => s.skillType === 'local').length;
</script>

<div class="page-wrap fade-in">
  <div class="page-header-row">
    <div>
      <h1 class="page-title">Skills</h1>
      <p class="page-sub">
        Composio integrations and local skills available to the agent
        {#if !loading}<span class="count-pill">{filtered.length} skills</span>{/if}
      </p>
    </div>
    <button class="btn btn-ghost" on:click={load} disabled={loading}>
      <RefreshCw size={14} />
      Refresh
    </button>
  </div>

  {#if !loading && skillList.length > 0}
    <!-- Stats row -->
    <div class="stats-row">
      <div class="mini-stat">
        <span class="mini-val">{skillList.length}</span>
        <span class="mini-lbl">Total</span>
      </div>
      <div class="mini-stat">
        <span class="mini-val">{composioCount}</span>
        <span class="mini-lbl">Composio</span>
      </div>
      <div class="mini-stat">
        <span class="mini-val">{localCount}</span>
        <span class="mini-lbl">Local</span>
      </div>
      <div class="mini-stat">
        <span class="mini-val">{skillList.reduce((a, s) => a + (s.toolCount || 0), 0)}</span>
        <span class="mini-lbl">Tools</span>
      </div>
    </div>
  {/if}

  <!-- Search -->
  <div class="search-wrap" style="margin-bottom: 1.25rem">
    <Search size={14} class="search-icon" />
    <input
      class="input"
      placeholder="Search skills…"
      bind:value={search}
      style="padding-left: 2rem"
    />
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {:else if loading}
    <div class="skills-grid">
      {#each Array(6) as _}
        <div class="skeleton-card"></div>
      {/each}
    </div>
  {:else if filtered.length === 0}
    <div class="empty-state">
      <Puzzle size={40} />
      <h3>{search ? 'No results' : 'No skills loaded'}</h3>
      <p>{search ? 'Try a different search' : 'Add a COMPOSIO_API_KEY to load 100+ external skills'}</p>
    </div>
  {:else}
    <div class="skills-grid">
      {#each filtered as skill (skill.name)}
        <div class="skill-card card card-hover fade-in">
          <div class="skill-header">
            <div class="skill-icon" class:composio={skill.skillType === 'composio'}>
              <Puzzle size={16} />
            </div>
            <div class="skill-meta">
              <span class="skill-name">{skill.name}</span>
              <span
                class="skill-type-badge"
                class:composio-badge={skill.skillType === 'composio'}
              >{skill.skillType}</span>
            </div>
          </div>
          <p class="skill-desc">{skill.description || 'No description available.'}</p>
          {#if skill.toolCount > 0}
            <div class="skill-tools">
              <Wrench size={11} />
              <span>{skill.toolCount} {skill.toolCount === 1 ? 'tool' : 'tools'}</span>
            </div>
          {/if}
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

  .count-pill {
    display: inline-block;
    background: var(--surface-2);
    color: var(--text-3);
    font-size: 0.6875rem;
    padding: 0.1em 0.4em;
    border-radius: 4px;
    margin-left: 0.375rem;
  }

  .stats-row {
    display: flex;
    gap: 1rem;
    margin-bottom: 1.25rem;
  }

  .mini-stat {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    padding: 0.625rem 1rem;
    display: flex;
    flex-direction: column;
    align-items: center;
    min-width: 72px;
  }

  .mini-val {
    font-size: 1.25rem;
    font-weight: 700;
    color: var(--text);
    line-height: 1;
  }

  .mini-lbl {
    font-size: 0.6875rem;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-top: 2px;
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

  .skills-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
    gap: 0.75rem;
  }

  .skill-card {
    display: flex;
    flex-direction: column;
    gap: 0.625rem;
  }

  .skill-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
  }

  .skill-icon {
    width: 34px;
    height: 34px;
    border-radius: 8px;
    background: var(--surface-2);
    color: var(--text-2);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
    transition: background 0.15s;
  }
  .skill-icon.composio {
    background: rgba(129,140,248,0.12);
    color: var(--primary);
  }

  .skill-meta {
    flex: 1;
    min-width: 0;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
  }

  .skill-name {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .skill-type-badge {
    font-size: 0.625rem;
    padding: 0.1em 0.4em;
    border-radius: 4px;
    background: var(--surface-3);
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.04em;
    flex-shrink: 0;
  }
  .skill-type-badge.composio-badge {
    background: var(--primary-dim);
    color: var(--primary);
  }

  .skill-desc {
    font-size: 0.8125rem;
    color: var(--text-2);
    line-height: 1.5;
    margin: 0;
    flex: 1;
    display: -webkit-box;
    -webkit-line-clamp: 3;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .skill-tools {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    color: var(--text-3);
    font-size: 0.75rem;
  }

  .skeleton-card {
    height: 120px;
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
