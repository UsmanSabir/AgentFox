<script lang="ts">
  import { onMount } from 'svelte';
  import { Heart, Plus, Trash2, Pause, Play, RefreshCw, Pencil, X, Check } from 'lucide-svelte';
  import { api, type HeartbeatInfo, type HeartbeatRequest } from '$lib/api';

  let beats: HeartbeatInfo[] = [];
  let loading = true;
  let error = '';

  // Add form
  let showAddForm = false;
  let addName = '';
  let addTask = '';
  let addInterval = 60;
  let addMaxMissed = 3;
  let addError = '';
  let adding = false;

  // Edit modal
  let editTarget: HeartbeatInfo | null = null;
  let editTask = '';
  let editInterval = 60;
  let editMaxMissed = 3;
  let saving = false;

  async function load() {
    loading = true; error = '';
    try { beats = await api.heartbeats.list(); }
    catch (e: unknown) { error = e instanceof Error ? e.message : String(e); }
    finally { loading = false; }
  }

  onMount(load);

  async function addHeartbeat() {
    if (!addName.trim() || !addTask.trim()) { addError = 'Name and task are required.'; return; }
    adding = true; addError = '';
    try {
      await api.heartbeats.add({ name: addName.trim(), task: addTask.trim(), intervalSeconds: addInterval, maxMissed: addMaxMissed });
      addName = ''; addTask = ''; addInterval = 60; addMaxMissed = 3;
      showAddForm = false;
      await load();
    } catch (e: unknown) {
      addError = e instanceof Error ? e.message : String(e);
    } finally { adding = false; }
  }

  async function remove(name: string) {
    if (!confirm(`Remove heartbeat "${name}"?`)) return;
    try { await api.heartbeats.remove(name); await load(); }
    catch (e: unknown) { error = e instanceof Error ? e.message : String(e); }
  }

  async function toggle(beat: HeartbeatInfo) {
    try {
      if (beat.isPaused) await api.heartbeats.resume(beat.name);
      else               await api.heartbeats.pause(beat.name);
      await load();
    } catch (e: unknown) { error = e instanceof Error ? e.message : String(e); }
  }

  function openEdit(beat: HeartbeatInfo) {
    editTarget   = beat;
    editTask     = beat.task;
    editInterval = beat.intervalSeconds;
    editMaxMissed = beat.maxMissed;
  }

  async function saveEdit() {
    if (!editTarget) return;
    saving = true;
    try {
      await api.heartbeats.update(editTarget.name, { task: editTask, intervalSeconds: editInterval, maxMissed: editMaxMissed });
      editTarget = null;
      await load();
    } catch (e: unknown) { error = e instanceof Error ? e.message : String(e); }
    finally { saving = false; }
  }

  function fmt(iso: string) {
    try { return new Date(iso).toLocaleString(); } catch { return iso; }
  }
</script>

<svelte:head><title>Heartbeats — AgentFox</title></svelte:head>

<div class="page">
  <div class="page-header">
    <div class="page-title">
      <Heart size={20} />
      <h1>Heartbeats</h1>
    </div>
    <div class="page-actions">
      <button class="btn" on:click={load} title="Refresh">
        <RefreshCw size={14} />
        Refresh
      </button>
      <button class="btn btn-primary" on:click={() => { showAddForm = !showAddForm; addError = ''; }}>
        <Plus size={14} />
        Add Heartbeat
      </button>
    </div>
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {/if}

  <!-- Add form -->
  {#if showAddForm}
    <div class="card add-form">
      <h3 class="form-title">New Heartbeat</h3>
      <div class="form-grid">
        <label>
          Name
          <input class="input" bind:value={addName} placeholder="health-check" />
        </label>
        <label>
          Interval (seconds)
          <input class="input" type="number" min="10" bind:value={addInterval} />
        </label>
        <label class="full">
          Task (prompt sent to agent)
          <textarea class="input textarea" rows="2" bind:value={addTask} placeholder="Check system health and report status"></textarea>
        </label>
        <label>
          Max missed before alert
          <input class="input" type="number" min="1" bind:value={addMaxMissed} />
        </label>
      </div>
      {#if addError}<p class="field-error">{addError}</p>{/if}
      <div class="form-actions">
        <button class="btn" on:click={() => { showAddForm = false; addError = ''; }}>Cancel</button>
        <button class="btn btn-primary" on:click={addHeartbeat} disabled={adding}>
          {adding ? 'Adding…' : 'Add Heartbeat'}
        </button>
      </div>
    </div>
  {/if}

  <!-- Table -->
  {#if loading}
    <div class="loading">Loading heartbeats…</div>
  {:else if beats.length === 0}
    <div class="empty">
      <Heart size={36} class="empty-icon" />
      <p>No heartbeats configured.</p>
      <p class="empty-sub">Add a heartbeat to have the agent run periodic health checks.</p>
    </div>
  {:else}
    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Task</th>
            <th>Interval</th>
            <th>Missed</th>
            <th>Last triggered</th>
            <th>Status</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {#each beats as beat (beat.name)}
            <tr>
              <td class="name-cell">{beat.name}</td>
              <td class="task-cell"><span class="task-text">{beat.task}</span></td>
              <td>{beat.intervalSeconds}s</td>
              <td>
                <span class="missed" class:missed-warn={beat.missedCount > 0}>
                  {beat.missedCount}/{beat.maxMissed}
                </span>
              </td>
              <td class="ts-cell">{fmt(beat.lastTriggered)}</td>
              <td>
                <span class="badge" class:badge-green={!beat.isPaused} class:badge-yellow={beat.isPaused}>
                  {beat.status}
                </span>
              </td>
              <td class="actions-cell">
                <button class="icon-btn" title={beat.isPaused ? 'Resume' : 'Pause'} on:click={() => toggle(beat)}>
                  {#if beat.isPaused}<Play size={14} />{:else}<Pause size={14} />{/if}
                </button>
                <button class="icon-btn" title="Edit" on:click={() => openEdit(beat)}>
                  <Pencil size={14} />
                </button>
                <button class="icon-btn danger" title="Remove" on:click={() => remove(beat.name)}>
                  <Trash2 size={14} />
                </button>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  {/if}
</div>

<!-- Edit modal -->
{#if editTarget}
  <div class="modal-backdrop" on:click|self={() => (editTarget = null)}>
    <div class="modal card">
      <div class="modal-header">
        <h3>Edit — {editTarget.name}</h3>
        <button class="icon-btn" on:click={() => (editTarget = null)}><X size={16} /></button>
      </div>
      <div class="form-grid">
        <label class="full">
          Task
          <textarea class="input textarea" rows="2" bind:value={editTask}></textarea>
        </label>
        <label>
          Interval (seconds)
          <input class="input" type="number" min="10" bind:value={editInterval} />
        </label>
        <label>
          Max missed
          <input class="input" type="number" min="1" bind:value={editMaxMissed} />
        </label>
      </div>
      <div class="form-actions">
        <button class="btn" on:click={() => (editTarget = null)}>Cancel</button>
        <button class="btn btn-primary" on:click={saveEdit} disabled={saving}>
          {#if saving}<RefreshCw size={13} class="spin" />{:else}<Check size={13} />{/if}
          {saving ? 'Saving…' : 'Save'}
        </button>
      </div>
    </div>
  </div>
{/if}

<style>
  .page { padding: 1.5rem; max-width: 1100px; margin: 0 auto; display: flex; flex-direction: column; gap: 1.25rem; }

  .page-header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 0.75rem; }
  .page-title  { display: flex; align-items: center; gap: 0.5rem; }
  .page-title h1 { font-size: 1.25rem; font-weight: 700; color: var(--text); margin: 0; }
  .page-title :global(svg) { color: var(--primary); }
  .page-actions { display: flex; gap: 0.5rem; }

  .error-banner { background: rgba(239,68,68,0.12); border: 1px solid rgba(239,68,68,0.3); color: #fca5a5; padding: 0.625rem 0.875rem; border-radius: var(--radius-sm); font-size: 0.8125rem; }

  .add-form { padding: 1.125rem; }
  .form-title { font-size: 0.9375rem; font-weight: 600; color: var(--text); margin: 0 0 0.875rem; }
  .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem 1rem; }
  .form-grid label { display: flex; flex-direction: column; gap: 0.375rem; font-size: 0.8125rem; color: var(--text-2); }
  .form-grid .full { grid-column: 1 / -1; }
  .textarea { resize: vertical; min-height: 56px; }
  .field-error { color: #fca5a5; font-size: 0.75rem; margin: 0.25rem 0 0; }
  .form-actions { display: flex; justify-content: flex-end; gap: 0.5rem; margin-top: 0.875rem; }

  .loading { text-align: center; color: var(--text-3); padding: 3rem; font-size: 0.875rem; }
  .empty { text-align: center; padding: 3.5rem 1rem; color: var(--text-3); }
  .empty p { margin: 0.25rem 0; }
  .empty-sub { font-size: 0.8125rem; }
  .empty :global(.empty-icon) { opacity: 0.3; margin-bottom: 0.75rem; }

  .table-card { padding: 0; overflow: hidden; }
  .table { width: 100%; border-collapse: collapse; font-size: 0.8125rem; }
  .table th { padding: 0.625rem 0.875rem; background: var(--surface-2); color: var(--text-3); font-weight: 600; text-align: left; font-size: 0.75rem; text-transform: uppercase; letter-spacing: 0.04em; white-space: nowrap; }
  .table td { padding: 0.625rem 0.875rem; border-top: 1px solid var(--border); color: var(--text-2); vertical-align: middle; }
  .table tbody tr:hover td { background: var(--surface-2); }

  .name-cell { font-weight: 600; color: var(--text); }
  .task-cell { max-width: 260px; }
  .task-text { display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; line-height: 1.4; }
  .ts-cell { white-space: nowrap; font-size: 0.75rem; color: var(--text-3); }
  .missed { font-variant-numeric: tabular-nums; }
  .missed-warn { color: #fb923c; font-weight: 600; }

  .actions-cell { white-space: nowrap; }
  .icon-btn { background: none; border: none; color: var(--text-3); cursor: pointer; padding: 0.25rem; border-radius: 4px; display: inline-flex; align-items: center; transition: background 0.12s, color 0.12s; }
  .icon-btn:hover { background: var(--surface-2); color: var(--text); }
  .icon-btn.danger:hover { background: rgba(239,68,68,0.12); color: #fca5a5; }

  .badge { display: inline-flex; padding: 0.15rem 0.5rem; border-radius: 99px; font-size: 0.6875rem; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; }
  .badge-green  { background: rgba(52,211,153,0.12); color: #34d399; }
  .badge-yellow { background: rgba(251,191,36,0.12); color: #fbbf24; }

  /* Modal */
  .modal-backdrop { position: fixed; inset: 0; background: rgba(0,0,0,0.55); z-index: 100; display: flex; align-items: center; justify-content: center; padding: 1rem; }
  .modal { width: 100%; max-width: 480px; padding: 1.25rem; }
  .modal-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 1rem; }
  .modal-header h3 { margin: 0; font-size: 1rem; font-weight: 600; color: var(--text); }

  :global(.spin) { animation: spin 0.8s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
</style>
