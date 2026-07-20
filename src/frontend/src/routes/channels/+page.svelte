<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import {
    Radio, RefreshCw,
    MessageSquare, Send, Hash, Phone, Mail,
    Smartphone, Wifi, Rss, Globe, Bot, Users
  } from 'lucide-svelte';
  import { api, type ChannelInfo, type ChannelsStatus } from '$lib/api';

  let status: ChannelsStatus | null = null;
  let loading = true;
  let error = '';
  let intervalId: ReturnType<typeof setInterval>;

  // Icon map keyed by stable backend channel type id.
  const typeIcons: Record<string, typeof Radio> = {
    discord:    MessageSquare,
    telegram:   Send,
    slack:      Hash,
    whatsapp:   Phone,
    teams:      Users,
    email:      Mail,
    sms:        Smartphone,
    websocket:  Wifi,
    rss:        Rss,
    webhook:    Globe,
  };

  const typeColors: Record<string, string> = {
    discord:   '#5865f2',
    telegram:  '#26a5e4',
    slack:     '#e01e5a',
    whatsapp:  '#25d366',
    teams:     '#6264a7',
    email:     '#ea4335',
    sms:       '#fb923c',
    websocket: '#0ea5e9',
    rss:       '#f97316',
    webhook:   '#8b5cf6',
  };

  function iconFor(type: string) {
    return typeIcons[type] ?? Bot;
  }

  function colorFor(type: string) {
    return typeColors[type] ?? 'var(--primary)';
  }

  function labelFor(type: string) {
    if (type === 'whatsapp') return 'WhatsApp';
    if (type === 'websocket') return 'WebSocket';
    return type.charAt(0).toUpperCase() + type.slice(1);
  }

  async function load() {
    error = '';
    try { status = await api.channels(); }
    catch (e: unknown) { error = e instanceof Error ? e.message : String(e); }
    finally { loading = false; }
  }

  onMount(() => {
    load();
    intervalId = setInterval(load, 5000); // auto-refresh every 5s
  });

  onDestroy(() => clearInterval(intervalId));
</script>

<svelte:head><title>Channels — AgentFox</title></svelte:head>

<div class="page">
  <!-- Header -->
  <div class="page-header">
    <div class="page-title">
      <Radio size={20} />
      <h1>Channels</h1>
    </div>
    <div class="page-actions">
      <button class="btn" on:click={load}>
        <RefreshCw size={14} />
        Refresh
      </button>
    </div>
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {/if}

  <!-- Summary pills -->
  {#if status && status.ready}
    <div class="summary-row">
      <div class="pill">
        <span class="pill-value">{status.total}</span>
        <span class="pill-label">Configured</span>
      </div>
      <div class="pill pill-green">
        <span class="pill-value">{status.connected}</span>
        <span class="pill-label">Connected</span>
      </div>
      {#if status.total - status.connected > 0}
        <div class="pill pill-red">
          <span class="pill-value">{status.total - status.connected}</span>
          <span class="pill-label">Offline</span>
        </div>
      {/if}
    </div>
  {/if}

  <!-- Channel cards -->
  {#if loading}
    <div class="loading">Loading channels…</div>
  {:else if !status?.ready}
    <div class="empty">
      <Radio size={36} class="empty-icon" />
      <p>Channel manager not ready yet.</p>
      <p class="empty-sub">The agent is still initializing — try again in a moment.</p>
    </div>
  {:else if status.channels.length === 0}
    <div class="empty">
      <Radio size={36} class="empty-icon" />
      <p>No channels configured.</p>
      <p class="empty-sub">
        Add channels in <code>appsettings.json</code> under the <code>Channels</code> array.<br />
        Supported via providers. Built-in providers: telegram, discord, slack, whatsapp, teams.
      </p>
    </div>
  {:else}
    <div class="grid">
      {#each status.channels as ch (ch.id)}
        <div class="channel-card card" class:offline={!ch.isConnected}>
          <!-- Left accent bar -->
          <div class="accent-bar" style="background: {colorFor(ch.type)}"></div>

          <div class="card-body">
            <!-- Icon + name row -->
            <div class="card-header">
              <div class="type-icon" style="background: {colorFor(ch.type)}22; color: {colorFor(ch.type)}">
                <svelte:component this={iconFor(ch.type)} size={18} />
              </div>
              <div class="card-titles">
                <div class="ch-name">{ch.name}</div>
                <div class="ch-type">{labelFor(ch.type)}</div>
              </div>
              <span
                class="status-dot"
                class:dot-green={ch.isConnected}
                class:dot-red={!ch.isConnected}
                title={ch.status}
              ></span>
            </div>

            <!-- Details -->
            <div class="card-details">
              <div class="detail-row">
                <span class="detail-label">Channel ID</span>
                <span class="detail-value mono">{ch.id || '—'}</span>
              </div>
              <div class="detail-row">
                <span class="detail-label">Status</span>
                <span class="badge" class:badge-green={ch.isConnected} class:badge-red={!ch.isConnected}>
                  {ch.status}
                </span>
              </div>
            </div>
          </div>
        </div>
      {/each}
    </div>
  {/if}

  <!-- Config hint -->
  <div class="hint card">
    <p>
      Channels are configured in <code>appsettings.json</code> → <code>Channels[]</code>.
      Set <code>"Enabled": false</code> on any entry to disable it without removing it.
      Changes take effect on next restart.
    </p>
  </div>
</div>

<style>
  .page { padding: 1.5rem; max-width: 1100px; margin: 0 auto; display: flex; flex-direction: column; gap: 1.25rem; }

  .page-header  { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 0.75rem; }
  .page-title   { display: flex; align-items: center; gap: 0.5rem; }
  .page-title h1 { font-size: 1.25rem; font-weight: 700; color: var(--text); margin: 0; }
  .page-title :global(svg) { color: var(--primary); }
  .page-actions { display: flex; gap: 0.5rem; }

  .error-banner { background: rgba(239,68,68,0.12); border: 1px solid rgba(239,68,68,0.3); color: #fca5a5; padding: 0.625rem 0.875rem; border-radius: var(--radius-sm); font-size: 0.8125rem; }

  /* Summary */
  .summary-row { display: flex; gap: 0.75rem; flex-wrap: wrap; }
  .pill { display: flex; flex-direction: column; align-items: center; background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 0.625rem 1.25rem; min-width: 80px; }
  .pill-green { border-color: rgba(52,211,153,0.3); background: rgba(52,211,153,0.06); }
  .pill-red   { border-color: rgba(239,68,68,0.3);  background: rgba(239,68,68,0.06); }
  .pill-value { font-size: 1.375rem; font-weight: 700; color: var(--text); line-height: 1.2; }
  .pill-green .pill-value { color: #34d399; }
  .pill-red   .pill-value { color: #f87171; }
  .pill-label { font-size: 0.6875rem; color: var(--text-3); text-transform: uppercase; letter-spacing: 0.05em; font-weight: 600; margin-top: 2px; }

  /* Grid */
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 0.875rem; }

  .channel-card { display: flex; gap: 0; overflow: hidden; padding: 0; }
  .channel-card.offline { opacity: 0.65; }

  .accent-bar { width: 4px; flex-shrink: 0; }

  .card-body  { flex: 1; padding: 1rem 1.125rem; display: flex; flex-direction: column; gap: 0.75rem; }

  .card-header { display: flex; align-items: center; gap: 0.75rem; }
  .type-icon   { width: 36px; height: 36px; border-radius: 10px; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
  .card-titles { flex: 1; min-width: 0; }
  .ch-name     { font-size: 0.9375rem; font-weight: 700; color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .ch-type     { font-size: 0.75rem; color: var(--text-3); margin-top: 1px; }

  .status-dot  { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
  .dot-green   { background: #34d399; box-shadow: 0 0 6px #34d39966; }
  .dot-red     { background: #f87171; }

  .card-details { display: flex; flex-direction: column; gap: 0.375rem; }
  .detail-row   { display: flex; align-items: center; justify-content: space-between; gap: 0.5rem; font-size: 0.8125rem; }
  .detail-label { color: var(--text-3); flex-shrink: 0; }
  .detail-value { color: var(--text-2); text-align: right; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 160px; }
  .mono         { font-family: var(--font-mono, monospace); font-size: 0.75rem; }

  .badge        { display: inline-flex; padding: 0.15rem 0.5rem; border-radius: 99px; font-size: 0.6875rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; }
  .badge-green  { background: rgba(52,211,153,0.12); color: #34d399; }
  .badge-red    { background: rgba(239,68,68,0.12);  color: #f87171; }

  /* Loading / empty */
  .loading { text-align: center; color: var(--text-3); padding: 3rem; font-size: 0.875rem; }
  .empty   { text-align: center; padding: 3.5rem 1rem; color: var(--text-3); }
  .empty p { margin: 0.25rem 0; line-height: 1.6; }
  .empty-sub { font-size: 0.8125rem; }
  .empty :global(.empty-icon) { opacity: 0.3; margin-bottom: 0.75rem; }
  .empty code { font-family: var(--font-mono, monospace); background: var(--surface-2); padding: 0.1em 0.35em; border-radius: 4px; font-size: 0.8125rem; color: var(--accent); }

  /* Hint */
  .hint { padding: 0.75rem 1rem; }
  .hint p { margin: 0; font-size: 0.8125rem; color: var(--text-3); line-height: 1.6; }
  .hint code { font-family: var(--font-mono, monospace); background: var(--surface-2); padding: 0.1em 0.35em; border-radius: 4px; color: var(--accent); }
</style>
