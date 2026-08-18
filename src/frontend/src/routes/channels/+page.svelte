<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import {
    Radio, RefreshCw, Pencil, Check, X, AlertTriangle,
    MessageSquare, Send, Hash, Phone, Mail,
    Smartphone, Wifi, Rss, Globe, Bot, Users
  } from 'lucide-svelte';
  import {
    api,
    type ChannelInfo,
    type ChannelsStatus,
    type ChannelOutboxEntry
  } from '$lib/api';

  let status: ChannelsStatus | null = null;
  let loading = true;
  let error = '';
  let intervalId: ReturnType<typeof setInterval>;

  // ── Subscription editing ─────────────────────────────────────────────────
  let editingId: string | null = null;
  let draft = '';
  let saving = false;
  let saveError = '';
  /** Set when a save applied live but could not be written to appsettings.json. */
  let saveWarning = '';

  const CATCH_ALL = '>';

  function beginEdit(ch: ChannelInfo) {
    editingId = ch.id;
    draft = ch.receivesAll ? CATCH_ALL : (ch.subscriptions ?? []).join(', ');
    saveError = '';
    saveWarning = '';
  }

  function cancelEdit() {
    editingId = null;
    draft = '';
    saveError = '';
  }

  /** Toggles one filter in the draft, so the topic list doubles as a picker. */
  function toggleFilter(filter: string) {
    const parts = draft.split(',').map((p) => p.trim()).filter(Boolean);
    const at = parts.indexOf(filter);
    if (at >= 0) parts.splice(at, 1);
    else parts.push(filter);
    draft = parts.join(', ');
  }

  function draftHas(filter: string) {
    return draft.split(',').map((p) => p.trim()).includes(filter);
  }

  async function save(ch: ChannelInfo) {
    saving = true;
    saveError = '';
    saveWarning = '';
    try {
      const result = await api.setChannelSubscriptions(ch.id, draft.trim());

      // The change is already live on the server; a failed write only means it reverts on
      // restart. Saying so is the point — silently succeeding would leave the operator
      // believing a setting is permanent when it is not.
      if (!result.persisted)
        saveWarning =
          `Applied to the running channel, but not saved: ${result.persistError ?? 'unknown error'}. ` +
          'It will revert when the agent restarts.';

      editingId = null;
      await load();
    } catch (e: unknown) {
      saveError = e instanceof Error ? e.message : String(e);
    } finally {
      saving = false;
    }
  }

  function onKeydown(event: KeyboardEvent, ch: ChannelInfo) {
    if (event.key === 'Enter') { event.preventDefault(); save(ch); }
    else if (event.key === 'Escape') { event.preventDefault(); cancelEdit(); }
  }

  // ── Test-channel outbox ──────────────────────────────────────────────────
  // Only 'dummy' channels record what they were asked to deliver. Reading it back is how you
  // confirm a subscription filter actually matched, rather than inferring it from silence.
  let outboxId: string | null = null;
  let outbox: ChannelOutboxEntry[] = [];
  let outboxError = '';

  async function toggleOutbox(ch: ChannelInfo) {
    if (outboxId === ch.id) { outboxId = null; outbox = []; return; }
    outboxId = ch.id;
    await loadOutbox(ch.id);
  }

  async function loadOutbox(id: string) {
    outboxError = '';
    try { outbox = (await api.channelMessages(id)).messages; }
    catch (e: unknown) { outboxError = e instanceof Error ? e.message : String(e); }
  }

  async function clearOutbox(id: string) {
    try {
      await api.clearChannelMessages(id);
      outbox = [];
      await load();
    } catch (e: unknown) {
      outboxError = e instanceof Error ? e.message : String(e);
    }
  }

  function shortTime(iso: string) {
    const d = new Date(iso);
    return isNaN(d.getTime()) ? iso : d.toLocaleTimeString();
  }

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
    // The 5s poll would overwrite a half-typed filter with the server's copy, so an open editor
    // pauses it. Saving reloads explicitly.
    if (editingId !== null) return;

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

  {#if saveWarning}
    <div class="warn-banner">
      <AlertTriangle size={14} />
      <span>{saveWarning}</span>
      <button class="icon-btn" title="Dismiss" on:click={() => (saveWarning = '')}>
        <X size={13} />
      </button>
    </div>
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
              <div class="detail-row detail-row-wrap">
                <span class="detail-label">Receives</span>
                {#if editingId === ch.id}
                  <span class="edit-spacer"></span>
                {:else}
                  <span class="filters">
                    {#if ch.receivesAll}
                      <code class="filter filter-all" title="Catch-all — every topic">everything</code>
                    {:else}
                      {#each ch.subscriptions ?? [] as filter}
                        <code class="filter">{filter}</code>
                      {/each}
                    {/if}
                    <button
                      class="icon-btn"
                      title="Edit subscriptions"
                      aria-label="Edit subscriptions for {ch.name}"
                      on:click={() => beginEdit(ch)}
                    >
                      <Pencil size={12} />
                    </button>
                  </span>
                {/if}
              </div>

              <!-- Inline subscription editor -->
              {#if editingId === ch.id}
                <div class="editor">
                  <div class="editor-row">
                    <!-- svelte-ignore a11y-autofocus -->
                    <input
                      class="editor-input mono"
                      bind:value={draft}
                      placeholder={CATCH_ALL}
                      autofocus
                      disabled={saving}
                      aria-label="Topic filters, comma separated"
                      on:keydown={(e) => onKeydown(e, ch)}
                    />
                    <button class="icon-btn icon-btn-ok" title="Save (Enter)" disabled={saving} on:click={() => save(ch)}>
                      <Check size={14} />
                    </button>
                    <button class="icon-btn" title="Cancel (Esc)" disabled={saving} on:click={cancelEdit}>
                      <X size={14} />
                    </button>
                  </div>

                  <p class="editor-hint">
                    Comma-separated. <code>*</code> = one segment, <code>&gt;</code> = one or more
                    trailing segments (last only). Empty or <code>&gt;</code> receives everything.
                  </p>

                  {#if (status?.topics ?? []).length > 0}
                    <div class="picker">
                      <button
                        class="pick"
                        class:pick-on={draftHas(CATCH_ALL)}
                        on:click={() => toggleFilter(CATCH_ALL)}
                      >everything</button>
                      {#each status?.topics ?? [] as topic (topic.name)}
                        <button
                          class="pick"
                          class:pick-on={draftHas(topic.name)}
                          title={topic.description}
                          on:click={() => toggleFilter(topic.name)}
                        >{topic.name}</button>
                      {/each}
                    </div>
                  {/if}

                  {#if saveError}
                    <p class="editor-error">{saveError}</p>
                  {/if}
                </div>
              {/if}

              <!-- Test-channel outbox -->
              {#if ch.recordsMessages}
                <div class="outbox">
                  <div class="outbox-head">
                    <button class="link-btn" on:click={() => toggleOutbox(ch)}>
                      {outboxId === ch.id ? 'Hide' : 'Show'} received ({ch.receivedCount})
                    </button>
                    {#if outboxId === ch.id}
                      <button class="link-btn" on:click={() => loadOutbox(ch.id)}>Refresh</button>
                      <button class="link-btn" on:click={() => clearOutbox(ch.id)}>Clear</button>
                    {/if}
                  </div>

                  {#if outboxId === ch.id}
                    {#if outboxError}
                      <p class="editor-error">{outboxError}</p>
                    {:else if outbox.length === 0}
                      <p class="editor-hint">
                        Nothing recorded. If you expected something, the filters above did not match
                        the topic it was published on.
                      </p>
                    {:else}
                      <ul class="msg-list">
                        {#each outbox as msg (msg.sequence)}
                          <li class="msg">
                            <span class="msg-time mono">{shortTime(msg.at)}</span>
                            <span class="msg-body">
                              {msg.content}
                              {#if msg.actions.length > 0}
                                <span class="msg-actions">[{msg.actions.join('] [')}]</span>
                              {/if}
                            </span>
                          </li>
                        {/each}
                      </ul>
                    {/if}
                  {/if}
                </div>
              {/if}
            </div>
          </div>
        </div>
      {/each}
    </div>
  {/if}

  <!-- Published topics -->
  {#if status?.ready && (status.topics ?? []).length > 0}
    <div class="topics card">
      <div class="topics-header">
        <Radio size={15} />
        <h2>Published topics</h2>
      </div>
      <p class="topics-intro">
        Subjects the agent and its plugins publish on. A channel's <code>Subscribe</code> filters are
        matched against these — <code>*</code> matches exactly one segment,
        <code>&gt;</code> matches one or more trailing segments and must come last. So
        <code>trading.*</code> matches <code>trading.order</code> but not
        <code>trading.order.accepted</code>, while <code>trading.&gt;</code> matches both.
        A filter matching no topic is silent, not an error.
      </p>
      <ul class="topic-list">
        {#each status.topics as topic (topic.name)}
          <li class="topic">
            <code class="topic-name">{topic.name}</code>
            <span class="topic-desc">{topic.description}</span>
            {#if topic.mandatory}
              <span class="badge badge-amber" title="Delivered to every channel if nothing subscribes to it">
                always delivered
              </span>
            {/if}
          </li>
        {/each}
      </ul>
    </div>
  {/if}

  <!-- Config hint -->
  <div class="hint card">
    <p>
      Channels are configured in <code>appsettings.json</code> → <code>Channels[]</code>.
      Set <code>"Enabled": false</code> on any entry to disable it without removing it.
      Add <code>"Name"</code> to pin a stable id (required if you run two channels of the same type),
      and <code>"Subscribe": "trading.&gt;, hitl.&gt;"</code> to narrow what it receives — omit it and
      the channel receives everything. Changes take effect on next restart.
    </p>
  </div>
</div>

<style>
  .page { width: 100%; min-width: 0; padding: 1.5rem; display: flex; flex-direction: column; gap: 1.25rem; }

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
  .badge-amber  { background: rgba(251,191,36,0.12); color: #fbbf24; }

  /* Subscriptions */
  .detail-row-wrap { align-items: flex-start; }
  .filters      { display: flex; flex-wrap: wrap; align-items: center; gap: 0.25rem; justify-content: flex-end; }
  .filter       { font-family: var(--font-mono, monospace); font-size: 0.6875rem; background: var(--surface-2); color: var(--text-2); padding: 0.1em 0.4em; border-radius: 4px; white-space: nowrap; }
  .filter-all   { color: var(--text-3); font-style: italic; }
  .edit-spacer  { flex: 1; }

  .icon-btn     { display: inline-flex; align-items: center; justify-content: center; background: transparent; border: 1px solid transparent; color: var(--text-3); border-radius: 4px; padding: 0.15rem; cursor: pointer; line-height: 0; }
  .icon-btn:hover:not(:disabled) { color: var(--text); background: var(--surface-2); border-color: var(--border); }
  .icon-btn:disabled { opacity: 0.5; cursor: default; }
  .icon-btn-ok:hover:not(:disabled) { color: #34d399; }

  /* Inline editor */
  .editor       { display: flex; flex-direction: column; gap: 0.5rem; padding-top: 0.5rem; border-top: 1px solid var(--border); }
  .editor-row   { display: flex; align-items: center; gap: 0.375rem; }
  .editor-input { flex: 1; min-width: 0; background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--radius-sm, 6px); color: var(--text); padding: 0.35rem 0.5rem; font-size: 0.75rem; }
  .editor-input:focus { outline: none; border-color: var(--primary); }
  .editor-hint  { margin: 0; font-size: 0.6875rem; color: var(--text-3); line-height: 1.5; }
  .editor-hint code { font-family: var(--font-mono, monospace); background: var(--surface-2); padding: 0 0.25em; border-radius: 3px; color: var(--accent); }
  .editor-error { margin: 0; font-size: 0.75rem; color: #f87171; line-height: 1.5; }

  .picker       { display: flex; flex-wrap: wrap; gap: 0.25rem; }
  .pick         { font-family: var(--font-mono, monospace); font-size: 0.6875rem; background: var(--surface-2); border: 1px solid var(--border); color: var(--text-3); padding: 0.1em 0.4em; border-radius: 4px; cursor: pointer; }
  .pick:hover   { color: var(--text); }
  .pick-on      { background: rgba(52,211,153,0.12); border-color: rgba(52,211,153,0.35); color: #34d399; }

  /* Test-channel outbox */
  .outbox       { display: flex; flex-direction: column; gap: 0.375rem; padding-top: 0.5rem; border-top: 1px solid var(--border); }
  .outbox-head  { display: flex; gap: 0.75rem; }
  .link-btn     { background: none; border: none; padding: 0; color: var(--text-3); font-size: 0.75rem; cursor: pointer; text-decoration: underline; text-underline-offset: 2px; }
  .link-btn:hover { color: var(--text); }
  .msg-list     { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.25rem; max-height: 180px; overflow-y: auto; }
  .msg          { display: flex; gap: 0.5rem; font-size: 0.75rem; line-height: 1.5; }
  .msg-time     { color: var(--text-3); flex-shrink: 0; font-size: 0.6875rem; }
  .msg-body     { color: var(--text-2); word-break: break-word; }
  .msg-actions  { color: var(--text-3); }

  .warn-banner  { display: flex; align-items: center; gap: 0.5rem; background: rgba(251,191,36,0.1); border: 1px solid rgba(251,191,36,0.3); color: #fbbf24; padding: 0.625rem 0.875rem; border-radius: var(--radius-sm); font-size: 0.8125rem; }
  .warn-banner span { flex: 1; }

  /* Topics */
  .topics        { padding: 1rem 1.125rem; display: flex; flex-direction: column; gap: 0.625rem; }
  .topics-header { display: flex; align-items: center; gap: 0.5rem; }
  .topics-header h2 { font-size: 0.9375rem; font-weight: 700; color: var(--text); margin: 0; }
  .topics-header :global(svg) { color: var(--primary); }
  .topics-intro  { margin: 0; font-size: 0.8125rem; color: var(--text-3); line-height: 1.6; }
  .topics code   { font-family: var(--font-mono, monospace); background: var(--surface-2); padding: 0.1em 0.35em; border-radius: 4px; color: var(--accent); }
  .topic-list    { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 0.375rem; }
  .topic         { display: flex; align-items: center; flex-wrap: wrap; gap: 0.5rem; font-size: 0.8125rem; }
  .topic-name    { flex-shrink: 0; }
  .topic-desc    { color: var(--text-3); }

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
