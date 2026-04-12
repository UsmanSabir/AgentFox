<script lang="ts">
  import { api } from '$lib/api';
  import { onMount } from 'svelte';
  import { agentStatus } from '$lib/stores';
  import {
    Settings, Server, Cpu, Key, Globe, Database,
    RefreshCw, CheckCircle, XCircle, Zap
  } from 'lucide-svelte';

  let status: Awaited<ReturnType<typeof api.status>> | null = null;
  let health: { status: string; timestamp: string } | null = null;
  let loading = true;
  let healthLoading = true;

  async function load() {
    loading = true;
    try {
      status = await api.status();
      agentStatus.set(status);
    } catch {
      status = null;
    } finally {
      loading = false;
    }
  }

  async function checkHealth() {
    healthLoading = true;
    try {
      health = await api.health();
    } catch {
      health = null;
    } finally {
      healthLoading = false;
    }
  }

  onMount(() => {
    load();
    checkHealth();
  });
</script>

<div class="page-wrap fade-in">
  <div class="page-header">
    <h1 class="page-title">Settings</h1>
    <p class="page-sub">Runtime configuration, system status, and connection info</p>
  </div>

  <!-- Status cards -->
  <div class="grid-2 mb-6">
    <!-- Health card -->
    <div class="card">
      <div class="card-header">
        <div class="card-icon success-icon">
          <Server size={16} />
        </div>
        <div>
          <h3 class="card-title">API Health</h3>
          <p class="card-sub">Backend connectivity</p>
        </div>
        <button
          class="btn btn-ghost"
          style="margin-left: auto; padding: 0.25rem 0.5rem"
          on:click={checkHealth}
          disabled={healthLoading}
          title="Refresh"
        >
          <RefreshCw size={13} />
        </button>
      </div>
      <div class="health-result">
        {#if healthLoading}
          <div class="spinner"></div>
          <span style="color: var(--text-3); font-size: 0.8125rem">Checking…</span>
        {:else if health}
          <CheckCircle size={18} style="color: var(--success)" />
          <div>
            <div style="font-size: 0.875rem; font-weight: 600; color: var(--success)">{health.status}</div>
            <div style="font-size: 0.6875rem; color: var(--text-3)">{new Date(health.timestamp).toLocaleString()}</div>
          </div>
        {:else}
          <XCircle size={18} style="color: var(--danger)" />
          <div style="font-size: 0.875rem; color: var(--danger)">Unreachable</div>
        {/if}
      </div>
    </div>

    <!-- Agent card -->
    <div class="card">
      <div class="card-header">
        <div class="card-icon agent-icon">
          <Zap size={16} />
        </div>
        <div>
          <h3 class="card-title">Agent Runtime</h3>
          <p class="card-sub">Main agent status</p>
        </div>
        <button
          class="btn btn-ghost"
          style="margin-left: auto; padding: 0.25rem 0.5rem"
          on:click={load}
          disabled={loading}
          title="Refresh"
        >
          <RefreshCw size={13} />
        </button>
      </div>
      <div class="health-result">
        {#if loading}
          <div class="spinner"></div>
          <span style="color: var(--text-3); font-size: 0.8125rem">Loading…</span>
        {:else if status}
          {#if status.ready}
            <CheckCircle size={18} style="color: var(--success)" />
          {:else}
            <div class="spinner"></div>
          {/if}
          <div>
            <div style="font-size: 0.875rem; font-weight: 600; color: var(--text)">{status.name}</div>
            <div style="font-size: 0.6875rem; color: var(--text-3)">{status.status} · {status.id ?? '—'}</div>
          </div>
        {:else}
          <XCircle size={18} style="color: var(--danger)" />
          <div style="font-size: 0.875rem; color: var(--danger)">Not connected</div>
        {/if}
      </div>
    </div>
  </div>

  <!-- Info sections -->
  <div class="sections">
    <section class="info-section">
      <h2 class="section-heading">
        <Globe size={14} />
        API Endpoints
      </h2>
      <div class="endpoint-list">
        {#each [
          { method: 'GET',  path: '/api/health',        desc: 'Health check' },
          { method: 'GET',  path: '/api/status',        desc: 'Agent status' },
          { method: 'POST', path: '/api/chat',           desc: 'Chat (request/response)' },
          { method: 'POST', path: '/api/chat/stream',    desc: 'Chat with SSE streaming' },
          { method: 'GET',  path: '/api/agents',         desc: 'Agent hierarchy' },
          { method: 'GET',  path: '/api/tools',          desc: 'Registered tools' },
          { method: 'GET',  path: '/api/skills',         desc: 'Available skills' },
          { method: 'GET',  path: '/api/memory',         desc: 'Memory entries' },
          { method: 'GET',  path: '/api/sessions',       desc: 'Session history' },
        ] as ep}
          <div class="endpoint-row">
            <span class="method-badge" class:post-badge={ep.method === 'POST'}>{ep.method}</span>
            <code class="endpoint-path">{ep.path}</code>
            <span class="endpoint-desc">{ep.desc}</span>
          </div>
        {/each}
      </div>
    </section>

    <section class="info-section">
      <h2 class="section-heading">
        <Cpu size={14} />
        Stack
      </h2>
      <div class="stack-list">
        {#each [
          { label: 'Backend',   value: '.NET 8 (ASP.NET Core minimal APIs)' },
          { label: 'Frontend',  value: 'SvelteKit 2 + Tailwind v4' },
          { label: 'Streaming', value: 'Server-Sent Events (SSE)' },
          { label: 'Protocol',  value: 'Model Context Protocol (MCP)' },
          { label: 'Memory',    value: 'SQLite (long-term) + in-memory (short-term)' },
          { label: 'Port',      value: '8080 (configurable in appsettings.json)' },
        ] as item}
          <div class="stack-row">
            <span class="stack-label">{item.label}</span>
            <span class="stack-value">{item.value}</span>
          </div>
        {/each}
      </div>
    </section>

    <section class="info-section">
      <h2 class="section-heading">
        <Database size={14} />
        Config Files
      </h2>
      <div class="stack-list">
        {#each [
          { label: 'Main config',  value: 'src/Agent/appsettings.json' },
          { label: 'Dev override', value: 'src/Agent/appsettings.Development.json' },
          { label: 'Frontend',     value: 'src/frontend/vite.config.ts + svelte.config.js' },
          { label: 'Build output', value: 'src/Agent/wwwroot/' },
        ] as item}
          <div class="stack-row">
            <span class="stack-label">{item.label}</span>
            <code class="stack-value mono">{item.value}</code>
          </div>
        {/each}
      </div>
    </section>

    <section class="info-section">
      <h2 class="section-heading">
        <Key size={14} />
        Environment Variables
      </h2>
      <div class="stack-list">
        {#each [
          { key: 'OPENAI_API_KEY',    desc: 'OpenAI / compatible provider' },
          { key: 'ANTHROPIC_API_KEY', desc: 'Claude models' },
          { key: 'COMPOSIO_API_KEY',  desc: 'Composio skills (100+ integrations)' },
          { key: 'DOTNET_ENVIRONMENT',desc: 'Development / Production (default)' },
        ] as ev}
          <div class="stack-row">
            <code class="env-key">{ev.key}</code>
            <span class="stack-value">{ev.desc}</span>
          </div>
        {/each}
      </div>
      <div class="hint-box">
        <Settings size={12} />
        <span>Set environment variables or configure providers in <code>appsettings.json</code> under the <code>LLM</code> section.</span>
      </div>
    </section>
  </div>
</div>

<style>
  .mb-6 { margin-bottom: 1.5rem; }

  .card-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    margin-bottom: 1rem;
  }

  .card-icon {
    width: 36px;
    height: 36px;
    border-radius: 8px;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .success-icon { background: rgba(52,211,153,0.12); color: var(--success); }
  .agent-icon   { background: var(--primary-dim);     color: var(--primary); }

  .card-title {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--text);
    margin: 0;
  }

  .card-sub {
    font-size: 0.75rem;
    color: var(--text-3);
    margin: 0;
  }

  .health-result {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.75rem;
    background: var(--surface-2);
    border-radius: var(--radius-sm);
  }

  .sections {
    display: flex;
    flex-direction: column;
    gap: 1.25rem;
  }

  .info-section {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1.25rem;
  }

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

  .endpoint-list {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .endpoint-row {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 0.375rem 0.5rem;
    border-radius: var(--radius-sm);
    transition: background 0.1s;
    font-size: 0.8125rem;
  }
  .endpoint-row:hover { background: var(--surface-2); }

  .method-badge {
    font-size: 0.625rem;
    font-weight: 700;
    padding: 0.15em 0.4em;
    border-radius: 4px;
    background: rgba(52,211,153,0.12);
    color: var(--success);
    letter-spacing: 0.04em;
    min-width: 34px;
    text-align: center;
    flex-shrink: 0;
  }
  .method-badge.post-badge {
    background: var(--primary-dim);
    color: var(--primary);
  }

  .endpoint-path {
    font-family: monospace;
    font-size: 0.8125rem;
    color: var(--text);
    min-width: 180px;
  }

  .endpoint-desc {
    color: var(--text-3);
    font-size: 0.75rem;
  }

  .stack-list {
    display: flex;
    flex-direction: column;
    gap: 0;
  }

  .stack-row {
    display: flex;
    align-items: baseline;
    gap: 1rem;
    padding: 0.5rem 0.5rem;
    border-radius: var(--radius-sm);
    font-size: 0.8125rem;
    transition: background 0.1s;
  }
  .stack-row:hover { background: var(--surface-2); }

  .stack-label {
    color: var(--text-2);
    min-width: 120px;
    font-weight: 500;
    flex-shrink: 0;
  }

  .stack-value {
    color: var(--text);
    flex: 1;
  }

  .stack-value.mono, code.stack-value {
    font-family: monospace;
    font-size: 0.8125rem;
  }

  .env-key {
    font-family: monospace;
    font-size: 0.8125rem;
    color: var(--accent);
    min-width: 200px;
    flex-shrink: 0;
  }

  .hint-box {
    display: flex;
    align-items: flex-start;
    gap: 0.5rem;
    margin-top: 0.875rem;
    padding: 0.625rem 0.875rem;
    background: var(--primary-dim);
    border-radius: var(--radius-sm);
    font-size: 0.75rem;
    color: var(--text-2);
    line-height: 1.5;
  }
  .hint-box code {
    background: rgba(129,140,248,0.15);
    padding: 0.05em 0.3em;
    border-radius: 3px;
    color: var(--primary);
    font-size: inherit;
  }
</style>
