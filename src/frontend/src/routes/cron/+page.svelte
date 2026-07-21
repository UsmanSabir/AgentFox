<script lang="ts">
  import { onMount } from 'svelte';
  import { CalendarClock, Plus, Trash2, RefreshCw, Pencil } from 'lucide-svelte';
  import { api, type CronJobInfo } from '$lib/api';

  let jobs: CronJobInfo[] = [];
  let loading = true;
  let error = '';

  // Add / edit modal — shared for both create and update.
  let showForm = false;
  let formMode: 'add' | 'edit' = 'add';
  let formName = '';
  let formCron = '';
  let formTask = '';
  let formError = '';
  let saving = false;

  const PRESETS = [
    { label: 'Every minute',   value: '* * * * *' },
    { label: 'Every 5 min',    value: '*/5 * * * *' },
    { label: 'Every hour',     value: '0 * * * *' },
    { label: 'Daily at 9am',   value: '0 9 * * *' },
    { label: 'Daily midnight', value: '0 0 * * *' },
    { label: 'Weekly (Mon)',   value: '0 9 * * 1' },
  ];

  async function load() {
    loading = true; error = '';
    try { jobs = await api.cron.list(); }
    catch (e: unknown) { error = e instanceof Error ? e.message : String(e); }
    finally { loading = false; }
  }

  onMount(load);

  function openAdd() {
    formMode = 'add';
    formName = ''; formCron = ''; formTask = '';
    formError = '';
    showForm = true;
  }

  function openEdit(job: CronJobInfo) {
    formMode = 'edit';
    formName = job.name;
    formCron = job.cronExpression;
    formTask = job.task;
    formError = '';
    showForm = true;
  }

  function closeForm() {
    showForm = false;
    formError = '';
  }

  async function save() {
    if (!formName.trim() || !formCron.trim() || !formTask.trim()) {
      formError = 'Name, cron expression, and task are required.'; return;
    }
    saving = true; formError = '';
    try {
      if (formMode === 'edit') {
        await api.cron.update(formName, {
          cronExpression: formCron.trim(),
          task: formTask.trim(),
        });
      } else {
        await api.cron.add({
          name: formName.trim(),
          cronExpression: formCron.trim(),
          task: formTask.trim(),
        });
      }
      showForm = false;
      await load();
    } catch (e: unknown) {
      formError = e instanceof Error ? e.message : String(e);
    } finally { saving = false; }
  }

  async function remove(name: string) {
    if (!confirm(`Remove cron job "${name}"?`)) return;
    try { await api.cron.remove(name); await load(); }
    catch (e: unknown) { error = e instanceof Error ? e.message : String(e); }
  }

  function fmt(iso: string | null) {
    if (!iso) return '—';
    try { return new Date(iso).toLocaleString(); } catch { return iso; }
  }

  function relativeNext(iso: string) {
    try {
      const diff = new Date(iso).getTime() - Date.now();
      if (diff < 0) return 'overdue';
      const s = Math.floor(diff / 1000);
      if (s < 60) return `${s}s`;
      const m = Math.floor(s / 60);
      if (m < 60) return `${m}m`;
      const h = Math.floor(m / 60);
      return `${h}h ${m % 60}m`;
    } catch { return iso; }
  }
</script>

<svelte:head><title>Cron Jobs — AgentFox</title></svelte:head>

<div class="page">
  <div class="page-header">
    <div class="page-title">
      <CalendarClock size={20} />
      <h1>Cron Jobs</h1>
    </div>
    <div class="page-actions">
      <button class="btn" on:click={load}>
        <RefreshCw size={14} />
        Refresh
      </button>
      <button class="btn btn-primary" on:click={openAdd}>
        <Plus size={14} />
        Add Job
      </button>
    </div>
  </div>

  {#if error}
    <div class="error-banner">{error}</div>
  {/if}

  <!-- Add / edit modal -->
  {#if showForm}
    <div
      class="modal-overlay"
      role="button"
      tabindex="0"
      on:click={closeForm}
      on:keydown={(e) => { if (e.key === 'Escape') closeForm(); }}
    >
      <div class="modal card" role="dialog" aria-modal="true" tabindex="-1" on:click|stopPropagation on:keydown|stopPropagation>
        <h3 class="form-title">{formMode === 'edit' ? `Edit “${formName}”` : 'New Cron Job'}</h3>
        <div class="form-grid">
          <label>
            Name
            <input
              class="input"
              bind:value={formName}
              placeholder="daily-summary"
              disabled={formMode === 'edit'}
            />
            {#if formMode === 'edit'}<span class="field-hint">Name is the job's identity and can't be changed.</span>{/if}
          </label>
          <label>
            Cron expression
            <input class="input mono" bind:value={formCron} placeholder="0 9 * * *" />
          </label>
        </div>

        <div class="presets">
          <span class="presets-label">Presets:</span>
          {#each PRESETS as p}
            <button class="preset-chip" on:click={() => (formCron = p.value)}>{p.label}</button>
          {/each}
        </div>

        <label class="full-label">
          Task (full prompt sent to agent)
          <textarea
            class="input textarea"
            rows="10"
            bind:value={formTask}
            placeholder="Generate and send a daily activity summary"
          ></textarea>
        </label>

        {#if formError}<p class="field-error">{formError}</p>{/if}
        <div class="form-actions">
          <button class="btn" on:click={closeForm}>Cancel</button>
          <button class="btn btn-primary" on:click={save} disabled={saving}>
            {saving ? 'Saving…' : (formMode === 'edit' ? 'Save Changes' : 'Add Job')}
          </button>
        </div>
      </div>
    </div>
  {/if}

  <!-- Cron reference -->
  <details class="reference card">
    <summary class="ref-summary">Cron expression reference</summary>
    <div class="ref-body">
      <div class="ref-format">
        <span class="ref-part">minute</span>
        <span class="ref-part">hour</span>
        <span class="ref-part">day</span>
        <span class="ref-part">month</span>
        <span class="ref-part">weekday</span>
      </div>
      <div class="ref-examples">
        <code>* * * * *</code> <span>Every minute</span>
        <code>0 * * * *</code> <span>Every hour</span>
        <code>0 9 * * *</code> <span>Daily at 9:00</span>
        <code>0 9 * * 1</code> <span>Every Monday at 9:00</span>
        <code>30 8 1 * *</code> <span>1st of each month at 8:30</span>
      </div>
    </div>
  </details>

  <!-- Jobs list -->
  {#if loading}
    <div class="loading">Loading cron jobs…</div>
  {:else if jobs.length === 0}
    <div class="empty">
      <CalendarClock size={36} class="empty-icon" />
      <p>No cron jobs scheduled.</p>
      <p class="empty-sub">Add a job to have the agent run tasks on a schedule.</p>
    </div>
  {:else}
    <div class="jobs-grid">
      {#each jobs as job (job.name)}
        <div class="job-card card">
          <div class="job-header">
            <div class="job-name">{job.name}</div>
            <div class="job-badges">
              <span class="badge badge-blue">{job.cronExpression}</span>
            </div>
            <button class="icon-btn" title="View / edit job" on:click={() => openEdit(job)}>
              <Pencil size={14} />
            </button>
            <button class="icon-btn danger" title="Remove job" on:click={() => remove(job.name)}>
              <Trash2 size={14} />
            </button>
          </div>
          <button class="job-task" title="Click to view / edit full instructions" on:click={() => openEdit(job)}>
            {job.task}
          </button>
          <div class="job-meta">
            <div class="meta-item">
              <span class="meta-label">Last run</span>
              <span class="meta-value">{fmt(job.lastExecuted)}</span>
            </div>
            <div class="meta-item">
              <span class="meta-label">Next run</span>
              <span class="meta-value">
                {fmt(job.nextExecution)}
                <span class="relative">({relativeNext(job.nextExecution)})</span>
              </span>
            </div>
          </div>
        </div>
      {/each}
    </div>
  {/if}
</div>

<style>
  .page { padding: 1.5rem; max-width: 1100px; margin: 0 auto; display: flex; flex-direction: column; gap: 1.25rem; }

  .page-header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 0.75rem; }
  .page-title  { display: flex; align-items: center; gap: 0.5rem; }
  .page-title h1 { font-size: 1.25rem; font-weight: 700; color: var(--text); margin: 0; }
  .page-title :global(svg) { color: var(--primary); }
  .page-actions { display: flex; gap: 0.5rem; }

  .error-banner { background: rgba(239,68,68,0.12); border: 1px solid rgba(239,68,68,0.3); color: #fca5a5; padding: 0.625rem 0.875rem; border-radius: var(--radius-sm); font-size: 0.8125rem; }

  /* Modal */
  .modal-overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.55); display: flex; align-items: center; justify-content: center; padding: 1.5rem; z-index: 50; }
  .modal { padding: 1.25rem 1.375rem; width: 100%; max-width: 640px; max-height: 88vh; overflow-y: auto; }
  .form-title { font-size: 0.9375rem; font-weight: 600; color: var(--text); margin: 0 0 0.875rem; }
  .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0.75rem 1rem; margin-bottom: 0.75rem; }
  .form-grid label, .full-label { display: flex; flex-direction: column; gap: 0.375rem; font-size: 0.8125rem; color: var(--text-2); }
  .full-label { margin-bottom: 0.75rem; }
  .textarea { resize: vertical; min-height: 160px; font-family: var(--font-mono, monospace); line-height: 1.55; }
  .mono { font-family: var(--font-mono, monospace); letter-spacing: 0.03em; }
  .field-error { color: #fca5a5; font-size: 0.75rem; margin: 0.25rem 0 0; }
  .field-hint { font-size: 0.6875rem; color: var(--text-3); }
  .input:disabled { opacity: 0.6; cursor: not-allowed; }
  .form-actions { display: flex; justify-content: flex-end; gap: 0.5rem; margin-top: 0.875rem; }

  .presets { display: flex; flex-wrap: wrap; gap: 0.375rem; align-items: center; margin-bottom: 0.75rem; }
  .presets-label { font-size: 0.75rem; color: var(--text-3); }
  .preset-chip { background: var(--surface-2); border: 1px solid var(--border); color: var(--text-2); border-radius: 99px; font-size: 0.75rem; padding: 0.2rem 0.625rem; cursor: pointer; transition: background 0.12s, color 0.12s; }
  .preset-chip:hover { background: var(--primary-dim); color: var(--primary); border-color: var(--primary); }

  /* Reference */
  .reference { padding: 0.75rem 1rem; }
  .ref-summary { cursor: pointer; font-size: 0.8125rem; color: var(--text-2); user-select: none; font-weight: 500; }
  .ref-summary:hover { color: var(--text); }
  .ref-body { margin-top: 0.75rem; }
  .ref-format { display: flex; gap: 0.5rem; margin-bottom: 0.5rem; }
  .ref-part { background: var(--primary-dim); color: var(--primary); border-radius: 4px; padding: 0.15rem 0.5rem; font-size: 0.75rem; font-weight: 600; }
  .ref-examples { display: grid; grid-template-columns: auto 1fr; gap: 0.375rem 1rem; align-items: center; font-size: 0.8125rem; }
  .ref-examples code { font-family: var(--font-mono, monospace); color: var(--accent); }
  .ref-examples span { color: var(--text-3); }

  .loading { text-align: center; color: var(--text-3); padding: 3rem; font-size: 0.875rem; }
  .empty { text-align: center; padding: 3.5rem 1rem; color: var(--text-3); }
  .empty p { margin: 0.25rem 0; }
  .empty-sub { font-size: 0.8125rem; }
  .empty :global(.empty-icon) { opacity: 0.3; margin-bottom: 0.75rem; }

  .jobs-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 0.875rem; }

  .job-card { padding: 1rem 1.125rem; display: flex; flex-direction: column; gap: 0.625rem; }
  .job-header { display: flex; align-items: center; gap: 0.625rem; flex-wrap: wrap; }
  .job-name { font-weight: 700; color: var(--text); font-size: 0.9375rem; flex: 1; }
  .job-badges { display: flex; gap: 0.375rem; flex-wrap: wrap; }
  .job-task { margin: 0; padding: 0; text-align: left; width: 100%; background: none; border: none; cursor: pointer; font: inherit; font-size: 0.8125rem; color: var(--text-2); line-height: 1.5; display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; transition: color 0.12s; }
  .job-task:hover { color: var(--text); }

  .job-meta { display: grid; grid-template-columns: 1fr 1fr; gap: 0.5rem; border-top: 1px solid var(--border); padding-top: 0.625rem; }
  .meta-item { display: flex; flex-direction: column; gap: 0.125rem; }
  .meta-label { font-size: 0.6875rem; color: var(--text-3); text-transform: uppercase; letter-spacing: 0.04em; font-weight: 600; }
  .meta-value { font-size: 0.8125rem; color: var(--text-2); }
  .relative { font-size: 0.75rem; color: var(--text-3); }

  .badge { display: inline-flex; padding: 0.15rem 0.5rem; border-radius: 99px; font-size: 0.6875rem; font-weight: 600; }
  .badge-blue { background: rgba(99,102,241,0.15); color: #a5b4fc; font-family: var(--font-mono, monospace); letter-spacing: 0.02em; }

  .icon-btn { background: none; border: none; color: var(--text-3); cursor: pointer; padding: 0.25rem; border-radius: 4px; display: inline-flex; align-items: center; transition: background 0.12s, color 0.12s; }
  .icon-btn:hover { background: var(--surface-2); color: var(--text); }
  .icon-btn.danger:hover { background: rgba(239,68,68,0.12); color: #fca5a5; }
</style>
