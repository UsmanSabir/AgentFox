<script lang="ts">
  import { onMount, onDestroy, tick } from 'svelte';
  import {
    Radio,
    RefreshCw,
    Plus,
    Power,
    Trash2,
    Pencil,
    AlertTriangle,
    Send,
    Hash,
    Phone,
    Bot,
    Users,
    MessageSquare,
  } from 'lucide-svelte';
  import ChannelHelp from '#lib/components/ChannelHelp.svelte';
  import {
    api,
    type ChannelInfo,
    type ChannelsStatus,
    type ChannelProviderInfo,
    type ChannelManagementResult,
    type ChannelOutboxEntry,
  } from '#lib/api.ts';

  let status: ChannelsStatus | null = $state<ChannelsStatus | null>(null);
  let providers: ChannelProviderInfo[] = $state([]);
  let providersLoaded = $state(false),
    loading = $state(true),
    busy = $state(false),
    adding = $state(false),
    disposed = false;
  let error = $state(''),
    loadError = $state(''),
    note = $state(''),
    warning = $state(''),
    channelType = $state(''),
    name = $state(''),
    subscribe = $state('>'),
    draft = $state('');
  let config: Record<string, string> = $state({});
  let nameInput: HTMLInputElement = $state()!, addButton: HTMLButtonElement = $state()!, topicInput: HTMLInputElement = $state()!;
  let editingId: string | null = $state<string | null>(null);
  let removing: ChannelInfo | null = $state<ChannelInfo | null>(null);
  let outboxId: string | null = $state<string | null>(null);
  let outbox: ChannelOutboxEntry[] = $state([]);
  let readVersion = 0;
  let readController: AbortController | null = null;
  const requests = new Set<AbortController>();
  let interval: ReturnType<typeof setInterval>;
  let draftExpiry: ReturnType<typeof setTimeout>;
  let provider = $derived(providers.find((p) => p.type === channelType));
  let enabledCount = $derived(status?.channels.filter((c) => c.enabled).length ?? 0);

  async function request<T>(
    action: (signal: AbortSignal) => Promise<T>,
    timeout = 65000,
  ): Promise<T> {
    const controller = new AbortController();
    requests.add(controller);
    const timer = setTimeout(() => controller.abort(), timeout);
    try {
      return await action(controller.signal);
    } finally {
      clearTimeout(timer);
      requests.delete(controller);
    }
  }
  function stopRead() {
    readVersion++;
    readController?.abort();
    readController = null;
  }
  function errorText(e: unknown) {
    return e instanceof Error ? e.message : String(e);
  }
  async function load(force = false) {
    if (disposed || (!force && (busy || adding || editingId !== null || removing !== null))) return;
    stopRead();
    const version = readVersion;
    const controller = new AbortController();
    readController = controller;
    const timer = setTimeout(() => controller.abort(), 15000);
    try {
      const [next, available] = await Promise.all([
        api.channels(controller.signal),
        providersLoaded ? Promise.resolve(null) : api.channelProviders(controller.signal),
      ]);
      if (disposed || version !== readVersion) return;
      status = next;
      if (available) {
        providers = available.providers;
        providersLoaded = true;
      }
      loadError = '';
    } catch (e) {
      if (!disposed && version === readVersion)
        loadError = controller.signal.aborted
          ? 'Channel status took too long to load. Retry Refresh.'
          : errorText(e);
    } finally {
      clearTimeout(timer);
      if (!disposed && version === readVersion) {
        loading = false;
        readController = null;
      }
    }
  }
  async function run(action: (signal: AbortSignal) => Promise<ChannelManagementResult>) {
    if (busy) return false;
    stopRead();
    busy = true;
    error = '';
    note = '';
    warning = '';
    let failure = '';
    try {
      const result = await request(action);
      if (disposed) return false;
      note = result.message ?? 'Channel updated.';
      warning = result.warning ?? '';
      if (!result.persisted)
        warning = `Applied now, but not saved. It will revert on restart. ${result.persistError ?? ''}`;
      return true;
    } catch (e) {
      failure =
        e instanceof Error && e.name === 'AbortError'
          ? 'The request timed out. Refresh channel status before retrying.'
          : errorText(e);
      return false;
    } finally {
      if (!disposed) {
        busy = false;
        await load(true);
        if (failure) error = failure;
      }
    }
  }
  function clearAdd() {
    clearTimeout(draftExpiry);
    adding = false;
    name = '';
    config = {};
    subscribe = '>';
    channelType = '';
  }
  function expireDraft() {
    clearTimeout(draftExpiry);
    draftExpiry = setTimeout(() => {
      clearAdd();
      editingId = null;
      draft = '';
      note = 'Unsaved channel form expired.';
    }, 600000);
  }
  async function openAdd() {
    stopRead();
    editingId = null;
    removing = null;
    outboxId = null;
    outbox = [];
    adding = true;
    channelType = providers[0]?.type ?? '';
    error = '';
    note = '';
    config = {};
    expireDraft();
    await tick();
    nameInput?.focus();
  }
  async function cancelAdd() {
    clearAdd();
    await tick();
    addButton?.focus();
  }
  async function add() {
    const payload = {
      type: channelType,
      name: name.trim(),
      config: { ...config },
      subscribe: subscribe.trim(),
    };
    // Credentials exist only in the mounted form and one request. No browser storage.
    const result = run((signal) => api.addChannel(payload, signal));
    config = {};
    if (await result) {
      clearAdd();
      await tick();
      addButton?.focus();
    }
    for (const key of Object.keys(payload.config)) delete payload.config[key];
  }
  async function beginEdit(ch: ChannelInfo) {
    stopRead();
    clearAdd();
    removing = null;
    editingId = ch.id;
    draft = ch.receivesAll ? '>' : ch.subscriptions.join(', ');
    error = '';
    note = '';
    expireDraft();
    await tick();
    topicInput?.focus();
  }
  async function saveTopics(ch: ChannelInfo) {
    if (await run((signal) => api.setChannelSubscriptions(ch.id, draft.trim(), signal))) {
      editingId = null;
      draft = '';
    }
  }
  function pick(filter: string) {
    if (filter === '>') {
      draft = '>';
      return;
    }
    const parts = draft
      .split(',')
      .map((p) => p.trim())
      .filter((p) => p && p !== '>');
    draft = (parts.includes(filter) ? parts.filter((p) => p !== filter) : [...parts, filter]).join(
      ', ',
    );
  }
  async function remove(ch: ChannelInfo) {
    if (await run((signal) => api.removeChannel(ch.id, signal))) {
      removing = null;
      outboxId = null;
      outbox = [];
    }
  }
  async function toggleOutbox(ch: ChannelInfo) {
    if (outboxId === ch.id) {
      outboxId = null;
      outbox = [];
      return;
    }
    outboxId = ch.id;
    outbox = [];
    await loadOutbox(ch);
  }
  async function loadOutbox(ch: ChannelInfo) {
    try {
      const response = await request((signal) => api.channelMessages(ch.id, signal), 15000);
      if (!disposed && outboxId === ch.id) outbox = response.messages;
    } catch (e) {
      if (!disposed) error = errorText(e);
    }
  }
  async function clearOutbox(ch: ChannelInfo) {
    try {
      await request((signal) => api.clearChannelMessages(ch.id, signal), 15000);
      if (!disposed && outboxId === ch.id) outbox = [];
    } catch (e) {
      if (!disposed) error = errorText(e);
    }
  }
  const icons: Record<string, typeof Radio> = {
    telegram: Send,
    slack: Hash,
    discord: MessageSquare,
    whatsapp: Phone,
    'stockji-whatsapp': Phone,
    'whatsapp-bridge': Phone,
    teams: Users,
  };
  const filterHelp =
    'Comma-separated filters. > receives everything. * matches one segment. > matches trailing segments and must come last. Empty receives everything. Approval requests fall back to connected active channels when no filter matches.';
  const namePattern = '[a-zA-Z0-9][a-zA-Z0-9._\\-]{0,63}';
  onMount(() => {
    load();
    interval = setInterval(() => load(), 5000);
  });
  onDestroy(() => {
    disposed = true;
    stopRead();
    clearInterval(interval);
    requests.forEach((c) => c.abort());
    requests.clear();
    clearAdd();
    draft = '';
    outbox = [];
    status = null;
  });
</script>

<svelte:head><title>Channels — AgentFox</title></svelte:head>
<div class="page" aria-busy={busy}>
  <header class="page-header">
    <div class="page-title">
      <Radio size={20} />
      <h1>Channels</h1>
      <ChannelHelp
        label="channels"
        text="Activate to receive selected topics and accept channel messages. Deactivate keeps credentials and topics. Remove deletes the saved configuration. These controls do not change trading mode."
      />
    </div>
    <div class="actions">
      <button
        class="btn btn-ghost"
        onclick={() => load()}
        disabled={busy || adding || editingId !== null || removing !== null}
        ><RefreshCw size={14} />Refresh</button
      >
      <button
        class="btn btn-primary"
        bind:this={addButton}
        onclick={openAdd}
        disabled={busy || adding || !providersLoaded || !status?.ready}
        ><Plus size={15} />Add channel</button
      >
    </div>
  </header>
  {#if error}<p class="error-banner" role="alert">{error}</p>{/if}
  {#if loadError}<p class="error-banner" role="alert">{loadError}</p>{/if}
  {#if warning}<p class="warn-banner" role="status"><AlertTriangle size={16} />{warning}</p>{/if}
  {#if note}<p class="result" role="status">{note}</p>{/if}
  {#if adding}
    <form class="card add-form" onsubmit={(event) => { event.preventDefault(); void add(); }}>
      <h2>Add channel</h2>
      <fieldset disabled={busy}>
        <div class="form-grid">
          <label
            >Channel type<select bind:value={channelType} onchange={() => (config = {})}
              >{#each providers as p}<option value={p.type}>{p.name}</option>{/each}</select
            ></label
          >
          <label
            >Name<input
              bind:this={nameInput}
              bind:value={name}
              required
              maxlength="64"
              pattern={namePattern}
              autocomplete="off"
              placeholder="my-alerts"
            /></label
          >
        </div>
        {#key channelType}<div class="form-grid provider-fields">
            {#each provider?.fields ?? [] as field}<label
                ><span class="field-label"
                  >{field.key}{field.required ? ' *' : ''}<ChannelHelp
                    label={field.key}
                    text={field.description}
                  /></span
                >
                <input
                  type={field.secret ? 'password' : 'text'}
                  aria-label={field.key}
                  bind:value={config[field.key]}
                  required={field.required}
                  maxlength="8192"
                  autocomplete="off"
                  spellcheck="false"
                  autocapitalize="none"
                /></label
              >{/each}
          </div>{/key}
        {#if channelType === 'stockji-whatsapp'}<p class="delivery-note">
            Uses this agent's linked Stockji WhatsApp number. The phone link is checked when
            sending.
          </p>{/if}
        <label
          ><span class="field-label"
            >Topics <ChannelHelp label="topic filters" text={filterHelp} /></span
          ><input
            aria-label="Topics"
            bind:value={subscribe}
            maxlength="4096"
            autocomplete="off"
          /></label
        >
        <div class="actions form-actions">
          <button class="btn btn-primary" type="submit"
            >{busy ? 'Adding…' : 'Add & activate'}</button
          ><button class="btn btn-ghost" type="button" onclick={cancelAdd}>Cancel</button>
        </div>
      </fieldset>
    </form>
  {/if}
  {#if loading}<p class="empty">Loading channels…</p>{:else if !status?.ready}<p class="empty">
      Channel manager is not ready yet.
    </p>{:else}
    <p class="summary">
      {status.total} configured · {enabledCount} active · {status.total - enabledCount} disabled
    </p>
    {#if !status.channels.length}<div class="card empty">
        <Radio size={28} />
        <p>No channels configured.</p>
      </div>{/if}
    <div class="grid">
      {#each status.channels as ch (ch.id)}
        {@const SvelteComponent = icons[ch.type] ?? Bot}
        <article class="card channel-card" aria-label={ch.id}>
          <div class="card-header">
            <div class="identity">
              <span class="type-icon"
                ><SvelteComponent size={19} /></span
              >
              <div>
                <h2>{ch.name}</h2>
                <p class="channel-id">{ch.id}</p>
              </div>
            </div>
            <span
              class="status"
              class:connected={ch.enabled && ch.isConnected}
              class:disconnected={ch.enabled && !ch.isConnected}
              >{ch.enabled
                ? ch.isConnected
                  ? ch.deliveryStatus
                    ? 'Active'
                    : 'Connected'
                  : 'Disconnected'
                : 'Disabled'}</span
            >
          </div>
          {#if !ch.providerAvailable}<p class="delivery-note">
              Channel provider is unavailable.
            </p>{/if}
          {#if ch.enabled && ch.deliveryStatus}<p class="delivery-note">{ch.deliveryStatus}</p>{/if}
          <div class="topics">
            <span>Receives</span>{#if ch.receivesAll}<code>Everything</code
              >{:else}{#each ch.subscriptions as topic}<code>{topic}</code>{/each}{/if}
          </div>
          {#if editingId === ch.id}<form
              class="topic-editor"
              onsubmit={(event) => { event.preventDefault(); void saveTopics(ch); }}
            >
              <label
                ><span class="field-label"
                  >Topics <ChannelHelp label="topic filters" text={filterHelp} /></span
                ><input
                  aria-label="Topics"
                  bind:this={topicInput}
                  bind:value={draft}
                  disabled={busy}
                  maxlength="4096"
                  autocomplete="off"
                /></label
              >
              <div class="picker">
                <button class="pick" type="button" disabled={busy} onclick={() => pick('>')}
                  >Everything</button
                >{#each status.topics ?? [] as topic}<button
                    class="pick"
                    type="button"
                    disabled={busy}
                    aria-pressed={draft
                      .split(',')
                      .map((p) => p.trim())
                      .includes(topic.name)}
                    onclick={() => pick(topic.name)}>{topic.name}</button
                  >{/each}
              </div>
              <div class="actions">
                <button class="btn btn-primary" disabled={busy} type="submit">Save topics</button
                ><button
                  class="btn btn-ghost"
                  disabled={busy}
                  type="button"
                  onclick={() => {
                    editingId = null;
                    draft = '';
                  }}>Cancel</button
                >
              </div>
            </form>{/if}
          {#if removing?.id === ch.id}<div
              class="remove-confirm"
              role="group"
              aria-label={`Confirm removal of ${ch.id}`}
            >
              <p>Remove <strong>{ch.id}</strong> and delete its saved configuration?</p>
              <div class="actions">
                <button class="btn btn-danger" disabled={busy} onclick={() => remove(ch)}
                  >Remove channel</button
                ><button class="btn btn-ghost" disabled={busy} onclick={() => (removing = null)}
                  >Cancel</button
                >
              </div>
            </div>
          {:else}<div class="actions card-actions">
              <button
                class="btn btn-ghost"
                disabled={busy || (!ch.providerAvailable && !ch.enabled)}
                aria-label={`${ch.enabled && ch.isConnected ? 'Deactivate' : 'Activate'} ${ch.id}`}
                onclick={() =>
                  run((signal) =>
                    api.setChannelEnabled(ch.id, !(ch.enabled && ch.isConnected), signal),
                  )}
                ><Power size={14} />{ch.enabled && ch.isConnected
                  ? 'Deactivate'
                  : 'Activate'}</button
              >
              {#if ch.enabled && !ch.isConnected}<button
                  class="btn btn-ghost"
                  disabled={busy}
                  onclick={() => run((signal) => api.setChannelEnabled(ch.id, false, signal))}
                  >Deactivate</button
                >{/if}
              <button class="btn btn-ghost" disabled={busy} onclick={() => beginEdit(ch)}
                ><Pencil size={13} />Edit topics</button
              >
              <button
                class="btn btn-danger remove"
                disabled={busy}
                aria-label={`Remove ${ch.id}`}
                onclick={() => {
                  stopRead();
                  clearAdd();
                  editingId = null;
                  removing = ch;
                }}><Trash2 size={14} />Remove</button
              >
            </div>{/if}
          {#if ch.recordsMessages}<div class="outbox">
              <div class="actions">
                <button class="btn btn-ghost" disabled={busy} onclick={() => toggleOutbox(ch)}
                  >{outboxId === ch.id ? 'Hide' : 'Show'} received ({ch.receivedCount})</button
                >{#if outboxId === ch.id}<button
                    class="btn btn-ghost"
                    disabled={busy}
                    onclick={() => loadOutbox(ch)}>Refresh</button
                  ><button class="btn btn-ghost" disabled={busy} onclick={() => clearOutbox(ch)}
                    >Clear</button
                  >{/if}
              </div>
              {#if outboxId === ch.id}<ul>
                  {#each outbox as msg (msg.sequence)}<li>
                      <time>{new Date(msg.at).toLocaleTimeString()}</time>
                      {msg.content}{#if msg.actions.length}
                        [{msg.actions.join('] [')}]{/if}
                    </li>{:else}<li>Nothing recorded.</li>{/each}
                </ul>{/if}
            </div>{/if}
        </article>
      {/each}
    </div>
  {/if}
</div>

<style>
  .page {
    width: 100%;
    min-width: 0;
    padding: 1.5rem;
    display: flex;
    flex-direction: column;
    gap: 1.25rem;
  }
  .page-header,
  .page-title,
  .actions,
  .card-header,
  .identity,
  .field-label {
    display: flex;
    align-items: center;
    gap: 0.625rem;
  }
  .page-header,
  .card-header {
    justify-content: space-between;
    flex-wrap: wrap;
  }
  .page-title h1 {
    font-size: 1.25rem;
    margin: 0;
    color: var(--text);
  }
  .page-title :global(svg) {
    color: var(--primary);
  }
  .actions {
    flex-wrap: wrap;
    gap: 0.5rem;
  }
  h2 {
    margin: 0;
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--text);
  }
  p {
    margin: 0;
  }
  .summary {
    color: var(--text-2);
    font-size: 0.8125rem;
  }
  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(min(100%, 350px), 1fr));
    gap: 1rem;
    align-items: start;
  }
  .channel-card {
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  .identity,
  .identity > div {
    min-width: 0;
  }
  .channel-id {
    color: var(--text-2);
    font-size: 0.75rem;
    overflow-wrap: anywhere;
  }
  .type-icon {
    display: flex;
    padding: 10px;
    color: var(--primary);
    background: var(--primary-dim);
    border-radius: 9px;
  }
  .status {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 0.75rem;
    color: var(--text-2);
  }
  .status::before {
    content: '';
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: currentColor;
  }
  .status.connected,
  .result {
    color: var(--success);
  }
  .status.disconnected {
    color: var(--warning);
  }
  .topics {
    display: flex;
    gap: 0.375rem;
    align-items: center;
    flex-wrap: wrap;
    color: var(--text-2);
    font-size: 0.8125rem;
  }
  .topics code {
    padding: 2px 7px;
    background: var(--surface-2);
    color: var(--text);
    border-radius: 4px;
    font-size: 0.75rem;
    overflow-wrap: anywhere;
  }
  .remove {
    margin-inline-start: auto;
  }
  .empty {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 1rem;
    color: var(--text-2);
    padding: 2.5rem 1rem;
  }
  .add-form {
    max-width: 720px;
    display: flex;
    flex-direction: column;
    gap: 1rem;
  }
  fieldset {
    border: 0;
    padding: 0;
    margin: 0;
    min-width: 0;
  }
  .form-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 1rem;
  }
  .provider-fields {
    margin: 1rem 0;
  }
  label {
    display: flex;
    flex-direction: column;
    gap: 0.375rem;
    font-size: 0.8125rem;
    color: var(--text-2);
  }
  .field-label {
    justify-content: space-between;
  }
  input,
  select {
    width: 100%;
    min-width: 0;
    font: inherit;
    color: var(--text);
    background: var(--surface-2);
    border: 1px solid var(--border-md);
    border-radius: 6px;
    padding: 10px 12px;
  }
  .form-actions {
    margin-top: 1rem;
  }
  .topic-editor,
  .remove-confirm,
  .outbox {
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    padding-top: 1rem;
    border-top: 1px solid var(--border);
  }
  .remove-confirm {
    font-size: 0.8125rem;
    overflow-wrap: anywhere;
  }
  .picker {
    display: flex;
    gap: 0.375rem;
    flex-wrap: wrap;
  }
  .pick {
    font-family: monospace;
    font-size: 0.75rem;
    color: var(--text-2);
    border: 1px solid var(--border-md);
    padding: 4px 7px;
    border-radius: 4px;
    background: var(--surface-2);
    cursor: pointer;
    overflow-wrap: anywhere;
  }
  .pick[aria-pressed='true'] {
    color: var(--primary);
    border-color: var(--primary);
  }
  .delivery-note {
    font-size: 0.8125rem;
    color: var(--text-2);
    margin-bottom: 0.25rem;
  }
  .error-banner,
  .warn-banner {
    padding: 0.75rem 1rem;
    border-radius: 6px;
    background: var(--surface-2);
    font-size: 0.8125rem;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    overflow-wrap: anywhere;
  }
  .error-banner {
    color: var(--danger);
    border: 1px solid var(--danger);
  }
  .warn-banner {
    color: var(--warning);
    border: 1px solid var(--warning);
  }
  .result {
    font-size: 0.8125rem;
  }
  .outbox ul {
    margin: 0;
    padding-inline-start: 1rem;
    font-size: 0.8125rem;
    overflow-wrap: anywhere;
  }
  .outbox time {
    color: var(--text-2);
    font-size: 0.75rem;
  }
  button:disabled {
    opacity: 0.55;
    cursor: default;
  }
  :is(button, input, select):focus-visible {
    outline: 2px solid var(--primary);
    outline-offset: 3px;
  }
  @media (max-width: 600px) {
    .page {
      padding: 1rem;
    }
    .form-grid {
      grid-template-columns: 1fr;
    }
    .remove {
      margin-inline-start: 0;
    }
  }
  @media (pointer: coarse) {
    button,
    input,
    select {
      min-height: 44px;
    }
    input,
    select {
      font-size: 16px;
    }
  }
</style>
