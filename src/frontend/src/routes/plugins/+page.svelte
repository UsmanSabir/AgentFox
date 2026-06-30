<script lang="ts">
  import { onMount } from 'svelte';
  import { api, type PluginSessionSummary, type PluginSessionStats, type PluginConfigResponse } from '$lib/api';
  import { RefreshCw, Activity, Settings, Clock, CheckCircle, AlertCircle, Zap } from 'lucide-svelte';

  let loading = true;
  let error: string | null = null;
  let activeTab: 'sessions' | 'config' = 'sessions';

  let sessions: PluginSessionSummary[] = [];
  let stats: Record<string, PluginSessionStats> = {};
  let configs: PluginConfigResponse[] = [];

  let selectedSession: PluginSessionSummary | null = null;
  let sessionDetail: any = null;
  let loadingDetail = false;

  let editingConfig: PluginConfigResponse | null = null;
  let configJson = '';

  async function loadSessions() {
    try {
      sessions = await api.pluginSessions.listAll();
      for (const session of sessions) {
        const key = session.pluginName;
        if (!stats[key]) {
          stats[key] = await api.pluginSessions.getStats(session.pluginName);
        }
      }
    } catch (e) {
      error = `Failed to load sessions: ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  async function loadConfigs() {
    try {
      configs = await api.pluginConfig.listAll();
    } catch (e) {
      error = `Failed to load configs: ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  async function load() {
    loading = true;
    error = null;
    try {
      await Promise.all([loadSessions(), loadConfigs()]);
    } finally {
      loading = false;
    }
  }

  async function loadSessionDetail(session: PluginSessionSummary) {
    loadingDetail = true;
    try {
      sessionDetail = await api.pluginSessions.getDetail(session.pluginName, session.sessionId);
      selectedSession = session;
    } catch (e) {
      error = `Failed to load session detail: ${e instanceof Error ? e.message : String(e)}`;
    } finally {
      loadingDetail = false;
    }
  }

  function editConfig(config: PluginConfigResponse) {
    editingConfig = config;
    configJson = JSON.stringify(config.config, null, 2);
  }

  async function saveConfig() {
    if (!editingConfig) return;
    try {
      const parsed = JSON.parse(configJson);
      await api.pluginConfig.update(editingConfig.pluginName, {
        config: parsed,
        merge: true
      });
      error = null;
      editingConfig = null;
      await loadConfigs();
    } catch (e) {
      error = `Failed to save config: ${e instanceof Error ? e.message : String(e)}`;
    }
  }

  function formatDate(date: string) {
    return new Date(date).toLocaleString();
  }

  function formatMs(ms: number) {
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  }

  onMount(load);

  $: groupedSessions = sessions.reduce((acc, session) => {
    if (!acc[session.pluginName]) acc[session.pluginName] = [];
    acc[session.pluginName].push(session);
    return acc;
  }, {} as Record<string, PluginSessionSummary[]>);
</script>

<div class="page-wrap fade-in">
  <div class="page-header-row">
    <div>
      <h1 class="page-title">Plugins</h1>
      <p class="page-sub">
        Monitor plugin execution, audit tool invocations, and manage configurations
      </p>
    </div>
    <button class="btn btn-ghost" on:click={load} disabled={loading}>
      <RefreshCw size={14} />
      Refresh
    </button>
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {/if}

  <!-- Tab Navigation -->
  <div class="tab-bar">
    <button
      class="tab"
      class:active={activeTab === 'sessions'}
      on:click={() => activeTab = 'sessions'}
    >
      <Activity size={14} />
      Sessions & Audit
    </button>
    <button
      class="tab"
      class:active={activeTab === 'config'}
      on:click={() => activeTab = 'config'}
    >
      <Settings size={14} />
      Configuration
    </button>
  </div>

  <!-- Sessions Tab -->
  {#if activeTab === 'sessions'}
    {#if loading}
      <div class="loading-state">
        <div class="spinner"></div>
        Loading sessions...
      </div>
    {:else if Object.keys(groupedSessions).length === 0}
      <div class="empty-state">
        <Activity size={40} />
        <h3>No Active Sessions</h3>
        <p>Plugin sessions will appear here as tools are invoked</p>
      </div>
    {:else}
      <div class="sessions-container">
        {#each Object.entries(groupedSessions) as [pluginName, pluginSessions]}
          <div class="plugin-group">
            <div class="plugin-header">
              <div>
                <div class="plugin-name">{pluginName}</div>
                {#if stats[pluginName]}
                  {@const s = stats[pluginName]}
                  <div class="plugin-stats">
                    <span class="stat-item">
                      <Zap size={12} />
                      {s.totalToolInvocations} invocations
                    </span>
                    <span class="stat-item success">
                      <CheckCircle size={12} />
                      {s.successfulInvocations} succeeded
                    </span>
                    <span class="stat-item {s.failedInvocations > 0 ? 'danger' : ''}">
                      <AlertCircle size={12} />
                      {s.failedInvocations} failed
                    </span>
                    <span class="stat-item">
                      {(s.successRate * 100).toFixed(1)}% success rate
                    </span>
                  </div>
                {/if}
              </div>
            </div>

            <div class="sessions-list">
              {#each pluginSessions as session (session.sessionId)}
                <button
                  class="session-card"
                  class:active={selectedSession?.sessionId === session.sessionId && selectedSession?.pluginName === session.pluginName}
                  on:click={() => loadSessionDetail(session)}
                  disabled={loadingDetail}
                >
                  <div class="session-info">
                    <div class="session-id">{session.sessionId}</div>
                    <div class="session-meta">
                      <span class="meta-item">
                        <Clock size={12} />
                        {formatDate(session.createdAt)}
                      </span>
                      <span class="meta-item">
                        {session.toolCount} tools
                      </span>
                    </div>
                  </div>
                  <div class="session-progress">
                    <div class="progress-bar" style="--success: {session.successfulToolCount}; --total: {session.toolCount}">
                      <div class="progress-fill" style="width: {session.toolCount > 0 ? (session.successfulToolCount / session.toolCount * 100) : 0}%"></div>
                    </div>
                    <span class="progress-text">{session.successfulToolCount}/{session.toolCount}</span>
                  </div>
                </button>
              {/each}
            </div>
          </div>
        {/each}
      </div>

      <!-- Session Detail -->
      {#if sessionDetail && selectedSession}
        <div class="detail-panel fade-in">
          <div class="detail-header">
            <div>
              <div class="detail-title">{selectedSession.pluginName} → {selectedSession.sessionId}</div>
              <div class="detail-subtitle">Execution Audit Trail</div>
            </div>
            <button class="btn btn-ghost" on:click={() => selectedSession = null}>Close</button>
          </div>

          <div class="executions-table">
            <div class="table-header">
              <div class="col-tool">Tool</div>
              <div class="col-time">Started</div>
              <div class="col-duration">Duration</div>
              <div class="col-status">Status</div>
            </div>

            {#each sessionDetail.executions as exec (exec.executionId)}
              <div class="table-row">
                <div class="col-tool">
                  <span class="tool-name">{exec.toolName}</span>
                </div>
                <div class="col-time">
                  <span class="time">{new Date(exec.startedAt).toLocaleTimeString()}</span>
                </div>
                <div class="col-duration">
                  {formatMs(exec.executionTimeMs)}
                </div>
                <div class="col-status">
                  <span class="status" class:completed={exec.status === 'Completed'} class:failed={exec.status === 'Failed'}>
                    {exec.status}
                  </span>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}
    {/if}
  {/if}

  <!-- Config Tab -->
  {#if activeTab === 'config'}
    {#if loading}
      <div class="loading-state">
        <div class="spinner"></div>
        Loading configurations...
      </div>
    {:else if configs.length === 0}
      <div class="empty-state">
        <Settings size={40} />
        <h3>No Configurations</h3>
        <p>Plugin configurations will appear here once set</p>
      </div>
    {:else if editingConfig}
      <div class="config-editor fade-in">
        <div class="editor-header">
          <div>
            <div class="editor-title">{editingConfig.pluginName}</div>
            <div class="editor-subtitle">Last updated: {formatDate(editingConfig.lastUpdatedAt)}</div>
          </div>
          <div class="editor-actions">
            <button class="btn btn-ghost" on:click={() => editingConfig = null}>Cancel</button>
            <button class="btn btn-primary" on:click={saveConfig}>Save</button>
          </div>
        </div>

        <textarea
          class="config-textarea"
          bind:value={configJson}
          placeholder='{"key": "value"}'
        ></textarea>
      </div>
    {:else}
      <div class="configs-list">
        {#each configs as config (config.pluginName)}
          <div class="config-card card card-hover">
            <div class="config-header">
              <div class="config-name">{config.pluginName}</div>
              {#if config.isDefault}
                <span class="badge-default">Default</span>
              {/if}
            </div>
            <div class="config-body">
              <pre class="config-json">{JSON.stringify(config.config, null, 2)}</pre>
            </div>
            <div class="config-footer">
              <span class="config-date">Updated: {formatDate(config.lastUpdatedAt)}</span>
              <button class="btn btn-sm" on:click={() => editConfig(config)}>Edit</button>
            </div>
          </div>
        {/each}
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

  .error-banner {
    background: rgba(248, 113, 113, 0.1);
    border: 1px solid rgba(248, 113, 113, 0.25);
    border-radius: var(--radius);
    padding: 0.75rem 1rem;
    color: var(--danger);
    font-size: 0.8125rem;
    margin-bottom: 1rem;
  }

  /* Tab Navigation */
  .tab-bar {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 1.5rem;
    border-bottom: 1px solid var(--border);
  }

  .tab {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    background: none;
    border: none;
    color: var(--text-2);
    padding: 0.75rem 1rem;
    cursor: pointer;
    font-size: 0.875rem;
    font-weight: 500;
    border-bottom: 2px solid transparent;
    transition: all 0.15s;
    margin-bottom: -1px;
  }

  .tab:hover {
    color: var(--text);
  }

  .tab.active {
    color: var(--primary);
    border-bottom-color: var(--primary);
  }

  /* Sessions */
  .sessions-container {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
  }

  .plugin-group {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    overflow: hidden;
  }

  .plugin-header {
    background: var(--surface-2);
    padding: 1rem;
    border-bottom: 1px solid var(--border);
  }

  .plugin-name {
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--text);
    margin-bottom: 0.5rem;
  }

  .plugin-stats {
    display: flex;
    gap: 1rem;
    flex-wrap: wrap;
  }

  .stat-item {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    font-size: 0.75rem;
    color: var(--text-2);
  }

  .stat-item.success {
    color: #10b981;
  }

  .stat-item.danger {
    color: #ef4444;
  }

  .sessions-list {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    padding: 0.5rem;
  }

  .session-card {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    background: none;
    border: 1px solid transparent;
    border-radius: 6px;
    padding: 0.75rem 1rem;
    cursor: pointer;
    transition: all 0.15s;
    font-size: 0.875rem;
  }

  .session-card:hover {
    background: var(--surface-2);
    border-color: var(--border-md);
  }

  .session-card.active {
    background: var(--primary-dim);
    border-color: var(--primary);
  }

  .session-info {
    flex: 1;
    min-width: 0;
  }

  .session-id {
    font-weight: 600;
    color: var(--text);
    margin-bottom: 0.25rem;
  }

  .session-meta {
    display: flex;
    gap: 0.75rem;
    font-size: 0.75rem;
    color: var(--text-3);
  }

  .meta-item {
    display: flex;
    align-items: center;
    gap: 0.25rem;
  }

  .session-progress {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    min-width: 120px;
  }

  .progress-bar {
    flex: 1;
    height: 4px;
    background: var(--surface-2);
    border-radius: 2px;
    overflow: hidden;
  }

  .progress-fill {
    height: 100%;
    background: linear-gradient(90deg, #10b981, #34d399);
    transition: width 0.15s;
  }

  .progress-text {
    font-size: 0.75rem;
    color: var(--text-2);
    min-width: 40px;
    text-align: right;
  }

  /* Detail Panel */
  .detail-panel {
    background: var(--surface);
    border: 1px solid var(--border-md);
    border-radius: var(--radius);
    padding: 1.25rem;
    margin-top: 1rem;
  }

  .detail-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    margin-bottom: 1rem;
  }

  .detail-title {
    font-size: 1rem;
    font-weight: 600;
    color: var(--text);
  }

  .detail-subtitle {
    font-size: 0.75rem;
    color: var(--text-3);
    margin-top: 0.25rem;
  }

  /* Executions Table */
  .executions-table {
    background: var(--surface-2);
    border: 1px solid var(--border);
    border-radius: 6px;
    overflow: hidden;
    font-size: 0.875rem;
  }

  .table-header {
    display: grid;
    grid-template-columns: 1.5fr 1fr 1fr 1fr;
    gap: 1rem;
    background: var(--surface);
    border-bottom: 1px solid var(--border);
    padding: 0.75rem 1rem;
    font-weight: 600;
    color: var(--text-2);
  }

  .table-row {
    display: grid;
    grid-template-columns: 1.5fr 1fr 1fr 1fr;
    gap: 1rem;
    padding: 0.75rem 1rem;
    border-bottom: 1px solid var(--border);
    align-items: center;
    color: var(--text);
  }

  .table-row:last-child {
    border-bottom: none;
  }

  .col-tool { overflow: hidden; }
  .tool-name { word-break: break-all; }
  .col-time { text-align: left; }
  .time { color: var(--text-2); font-family: 'Courier New', monospace; font-size: 0.75rem; }
  .col-duration { text-align: center; }
  .col-status { text-align: right; }

  .status {
    display: inline-block;
    font-size: 0.75rem;
    padding: 0.25rem 0.5rem;
    border-radius: 4px;
    background: rgba(249, 115, 22, 0.1);
    color: var(--warning);
  }

  .status.completed {
    background: rgba(16, 185, 129, 0.1);
    color: #10b981;
  }

  .status.failed {
    background: rgba(239, 68, 68, 0.1);
    color: var(--danger);
  }

  /* Config Styles */
  .configs-list {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(350px, 1fr));
    gap: 1rem;
  }

  .config-card {
    display: flex;
    flex-direction: column;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--radius);
    padding: 1rem;
  }

  .config-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 0.75rem;
    gap: 0.5rem;
  }

  .config-name {
    font-weight: 600;
    color: var(--text);
    font-size: 0.9375rem;
  }

  .badge-default {
    background: var(--primary-dim);
    color: var(--primary);
    font-size: 0.625rem;
    padding: 0.125rem 0.375rem;
    border-radius: 3px;
    font-weight: 600;
  }

  .config-body {
    flex: 1;
    margin-bottom: 0.75rem;
    overflow: hidden;
  }

  .config-json {
    background: var(--surface-2);
    border: 1px solid var(--border);
    border-radius: 4px;
    padding: 0.75rem;
    font-size: 0.7rem;
    font-family: 'Courier New', monospace;
    color: var(--text-2);
    max-height: 200px;
    overflow-y: auto;
    margin: 0;
    white-space: pre-wrap;
    word-break: break-word;
  }

  .config-footer {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding-top: 0.75rem;
    border-top: 1px solid var(--border);
  }

  .config-date {
    font-size: 0.75rem;
    color: var(--text-3);
  }

  /* Config Editor */
  .config-editor {
    background: var(--surface);
    border: 1px solid var(--border-md);
    border-radius: var(--radius);
    padding: 1.25rem;
  }

  .editor-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    margin-bottom: 1rem;
  }

  .editor-title {
    font-size: 1rem;
    font-weight: 600;
    color: var(--text);
  }

  .editor-subtitle {
    font-size: 0.75rem;
    color: var(--text-3);
    margin-top: 0.25rem;
  }

  .editor-actions {
    display: flex;
    gap: 0.5rem;
  }

  .config-textarea {
    width: 100%;
    height: 300px;
    background: var(--surface-2);
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 0.75rem;
    font-family: 'Courier New', monospace;
    font-size: 0.75rem;
    color: var(--text);
    resize: vertical;
  }

  .config-textarea:focus {
    outline: none;
    border-color: var(--primary);
    box-shadow: 0 0 0 2px var(--primary-dim);
  }

  /* Loading State */
  .loading-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 1rem;
    padding: 3rem 1rem;
    color: var(--text-2);
  }

  .spinner {
    width: 24px;
    height: 24px;
    border: 2px solid var(--border-md);
    border-top-color: var(--primary);
    border-radius: 50%;
    animation: spin 0.6s linear infinite;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  /* Empty State */
  .empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 0.75rem;
    padding: 3rem 1rem;
    color: var(--text-3);
  }

  .empty-state h3 {
    color: var(--text-2);
    margin: 0;
  }

  .empty-state p {
    margin: 0;
    font-size: 0.875rem;
  }
</style>
