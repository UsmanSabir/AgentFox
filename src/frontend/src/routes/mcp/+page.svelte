<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { McpStatus, McpServerInfo } from '$lib/api';
  import {
    Server, RefreshCw, CheckCircle, XCircle, Wrench,
    ChevronDown, ChevronRight, Plug, AlertTriangle, Info
  } from 'lucide-svelte';

  let data: McpStatus | null = null;
  let loading = true;
  let error: string | null = null;
  let expanded: Set<string> = new Set();

  async function load() {
    loading = true;
    error = null;
    try {
      data = await api.mcp();
    } catch (e: unknown) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      loading = false;
    }
  }

  onMount(load);

  function toggle(name: string) {
    const next = new Set(expanded);
    next.has(name) ? next.delete(name) : next.add(name);
    expanded = next;
  }

  $: servers    = (data?.servers ?? []) as McpServerInfo[];
  $: connected  = servers.filter(s => s.status === 'connected');
  $: failed     = servers.filter(s => s.status === 'failed');
</script>

<div class="page-wrap fade-in">
  <div class="page-header-row">
    <div>
      <h1 class="page-title">MCP Servers</h1>
      <p class="page-sub">
        Model Context Protocol server connections and their exposed tools
      </p>
    </div>
    <button class="btn btn-ghost" on:click={load} disabled={loading}>
      <RefreshCw size={14} />
      Refresh
    </button>
  </div>

  {#if error}
    <div class="error-banner">
      <AlertTriangle size={15} />
      {error}
    </div>
  {:else if loading}
    <div class="stats-row">
      {#each [1,2,3] as _}
        <div class="skeleton-stat"></div>
      {/each}
    </div>
    <div class="server-list">
      {#each [1,2] as _}
        <div class="skeleton-server"></div>
      {/each}
    </div>
  {:else if !data || servers.length === 0}
    <div class="empty-state">
      <Plug size={40} />
      <h3>No MCP servers configured</h3>
      <p>Add servers to the <code>MCP:Servers</code> array in <code>appsettings.json</code></p>
    </div>
  {:else}
    <!-- Stats -->
    <div class="stats-row">
      <div class="mini-stat">
        <span class="mini-val">{data.serverCount}</span>
        <span class="mini-lbl">Connected</span>
      </div>
      <div class="mini-stat">
        <span class="mini-val">{data.totalTools}</span>
        <span class="mini-lbl">Tools</span>
      </div>
      {#if data.failureCount > 0}
        <div class="mini-stat failure-stat">
          <span class="mini-val" style="color:var(--danger)">{data.failureCount}</span>
          <span class="mini-lbl">Failed</span>
        </div>
      {/if}
    </div>

    <!-- Connected servers -->
    {#if connected.length > 0}
      <section class="section">
        <h2 class="section-heading">
          <CheckCircle size={13} style="color:var(--success)" />
          Connected
          <span class="badge badge-success">{connected.length}</span>
        </h2>
        <div class="server-list">
          {#each connected as server (server.name)}
            <div class="server-card fade-in">
              <button
                class="server-header"
                on:click={() => toggle(server.name)}
                aria-expanded={expanded.has(server.name)}
              >
                <div class="server-icon connected">
                  <Server size={16} />
                </div>
                <div class="server-info">
                  <span class="server-name">{server.name}</span>
                  <span class="server-meta">
                    <Wrench size={11} />
                    {server.toolCount} {server.toolCount === 1 ? 'tool' : 'tools'}
                  </span>
                </div>
                <span class="badge badge-success" style="margin-left:auto">connected</span>
                <span class="expand-icon">
                  {#if expanded.has(server.name)}
                    <ChevronDown size={15} />
                  {:else}
                    <ChevronRight size={15} />
                  {/if}
                </span>
              </button>

              {#if expanded.has(server.name) && server.tools.length > 0}
                <div class="tools-panel">
                  <div class="tools-grid">
                    {#each server.tools as tool}
                      <div class="tool-chip">
                        <Wrench size={11} />
                        {tool}
                      </div>
                    {/each}
                  </div>
                </div>
              {:else if expanded.has(server.name)}
                <div class="tools-panel">
                  <p class="no-tools">No tools reported by this server</p>
                </div>
              {/if}
            </div>
          {/each}
        </div>
      </section>
    {/if}

    <!-- Failed servers -->
    {#if failed.length > 0}
      <section class="section">
        <h2 class="section-heading">
          <XCircle size={13} style="color:var(--danger)" />
          Failed to Connect
          <span class="badge badge-danger">{failed.length}</span>
        </h2>
        <div class="server-list">
          {#each failed as server (server.name)}
            <div class="server-card failure-card fade-in">
              <button
                class="server-header"
                on:click={() => toggle(server.name)}
                aria-expanded={expanded.has(server.name)}
              >
                <div class="server-icon failed">
                  <Server size={16} />
                </div>
                <div class="server-info">
                  <span class="server-name">{server.name}</span>
                  <span class="server-meta error-meta">Connection failed</span>
                </div>
                <span class="badge badge-danger" style="margin-left:auto">failed</span>
                <span class="expand-icon">
                  {#if expanded.has(server.name)}
                    <ChevronDown size={15} />
                  {:else}
                    <ChevronRight size={15} />
                  {/if}
                </span>
              </button>

              {#if expanded.has(server.name) && server.error}
                <div class="tools-panel error-panel">
                  <pre class="error-msg">{server.error}</pre>
                </div>
              {/if}
            </div>
          {/each}
        </div>
      </section>
    {/if}

    <!-- Config hint -->
    <div class="hint-box">
      <Info size={13} style="flex-shrink:0" />
      <div>
        Configure MCP servers in <code>appsettings.json</code> under <code>MCP:Servers</code>.
        Each entry needs a <code>Name</code>, transport config (<code>Url</code> for HTTP or <code>Command</code> for stdio),
        and optional <code>Enabled: false</code> to disable without removing.
      </div>
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

  .error-banner {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    background: rgba(248,113,113,0.1);
    border: 1px solid rgba(248,113,113,0.25);
    border-radius: var(--radius);
    padding: 0.75rem 1rem;
    color: var(--danger);
    font-size: 0.8125rem;
    margin-bottom: 1rem;
  }

  .stats-row {
    display: flex;
    gap: 1rem;
    margin-bottom: 1.5rem;
    flex-wrap: wrap;
  }

  .mini-stat {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius-sm);
    padding: 0.625rem 1.25rem;
    display: flex;
    flex-direction: column;
    align-items: center;
    min-width: 80px;
  }

  .failure-stat { border-color: rgba(248,113,113,0.2); }

  .mini-val {
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--text);
    line-height: 1;
  }

  .mini-lbl {
    font-size: 0.6875rem;
    color: var(--text-3);
    text-transform: uppercase;
    letter-spacing: 0.04em;
    margin-top: 3px;
  }

  .section { margin-bottom: 1.5rem; }

  .section-heading {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    font-size: 0.8125rem;
    font-weight: 600;
    color: var(--text-2);
    margin: 0 0 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
  }

  .server-list {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
  }

  .server-card {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
    transition: border-color 0.15s;
  }
  .server-card:hover { border-color: var(--border-md); }

  .failure-card { border-color: rgba(248,113,113,0.15); }
  .failure-card:hover { border-color: rgba(248,113,113,0.3); }

  .server-header {
    display: flex;
    align-items: center;
    gap: 0.875rem;
    padding: 0.875rem 1rem;
    background: transparent;
    border: none;
    cursor: pointer;
    width: 100%;
    text-align: left;
    transition: background 0.1s;
  }
  .server-header:hover { background: var(--surface-2); }

  .server-icon {
    width: 36px;
    height: 36px;
    border-radius: 8px;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }
  .server-icon.connected {
    background: rgba(52,211,153,0.12);
    color: var(--success);
  }
  .server-icon.failed {
    background: rgba(248,113,113,0.12);
    color: var(--danger);
  }

  .server-info {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .server-name {
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .server-meta {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    font-size: 0.75rem;
    color: var(--text-3);
  }

  .error-meta { color: var(--danger); opacity: 0.7; }

  .expand-icon {
    color: var(--text-3);
    flex-shrink: 0;
  }

  /* Tools panel */
  .tools-panel {
    border-top: 1px solid var(--border);
    padding: 0.875rem 1rem;
    background: var(--surface-2);
  }

  .tools-grid {
    display: flex;
    flex-wrap: wrap;
    gap: 0.375rem;
  }

  .tool-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.25rem;
    padding: 0.2rem 0.625rem;
    border-radius: 99px;
    background: var(--surface-3);
    border: 1px solid var(--border);
    font-size: 0.75rem;
    color: var(--text-2);
    font-family: 'JetBrains Mono', 'Fira Code', Consolas, monospace;
  }

  .no-tools {
    font-size: 0.8125rem;
    color: var(--text-3);
    margin: 0;
    font-style: italic;
  }

  .error-panel { background: rgba(248,113,113,0.05); }

  .error-msg {
    font-family: monospace;
    font-size: 0.75rem;
    color: var(--danger);
    margin: 0;
    white-space: pre-wrap;
    word-break: break-word;
  }

  .hint-box {
    display: flex;
    align-items: flex-start;
    gap: 0.625rem;
    margin-top: 1rem;
    padding: 0.875rem 1rem;
    background: var(--primary-dim);
    border-radius: var(--radius);
    font-size: 0.8125rem;
    color: var(--text-2);
    line-height: 1.6;
  }
  .hint-box code {
    background: rgba(129,140,248,0.15);
    padding: 0.05em 0.35em;
    border-radius: 3px;
    color: var(--primary);
    font-size: 0.8em;
  }

  /* Skeletons */
  .skeleton-stat {
    height: 64px;
    width: 100px;
    border-radius: var(--radius-sm);
    background: linear-gradient(90deg, var(--surface) 25%, var(--surface-2) 50%, var(--surface) 75%);
    background-size: 200% 100%;
    animation: shimmer 1.5s infinite;
  }

  .skeleton-server {
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
