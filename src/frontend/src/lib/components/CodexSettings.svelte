<script lang="ts">
  import { createEventDispatcher, onDestroy, onMount } from 'svelte';
  import { Sparkles, RefreshCw, CheckCircle, XCircle, AlertTriangle, ExternalLink, Copy } from 'lucide-svelte';
  import ChannelHelp from './ChannelHelp.svelte';
  import { codex, type CodexStatus, type CodexSignIn } from '#lib/codex.ts';

  const dispatch = createEventDispatcher<{ changed: void }>();

  let status: CodexStatus | null = null;
  let loading = true;
  let busy = '';
  let error = '';
  let notice = '';
  let signIn: CodexSignIn | null = null;
  let poll: ReturnType<typeof setInterval> | null = null;
  let model = '';
  let effort = '';

  const STATE_TEXT: Record<string, string> = {
    'ready': 'Ready',
    'not-installed': 'Not installed',
    'not-allowed': 'Not available for this account',
    'not-signed-in': 'Not signed in',
    'reauthentication-required': 'Sign in again',
    'rate-limited': 'Usage limit reached',
  };

  function apply(s: CodexStatus) {
    status = s;
    model = s.model;
    effort = s.reasoningEffort ?? '';
  }

  async function run(name: string, action: () => Promise<void>) {
    busy = name;
    error = '';
    notice = '';
    try {
      await action();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = '';
    }
  }

  async function load(refresh = false) {
    loading = !status;
    await run(refresh ? 'refresh' : '', async () => apply(await codex.status(refresh)));
    loading = false;
  }

  function stopPolling() {
    if (poll) clearInterval(poll);
    poll = null;
  }

  function startSignIn() {
    run('signin', async () => {
      signIn = await codex.startSignIn();
      if (signIn.state === 'Failed') {
        error = signIn.error ?? 'The sign-in could not start.';
        signIn = null;
        return;
      }
      stopPolling();
      poll = setInterval(async () => {
        try {
          const s = await codex.signIn();
          if (s.state === 'AwaitingApproval') return;
          stopPolling();
          signIn = null;
          if (s.state === 'Completed') {
            notice = 'Signed in.';
            await load(true);
            dispatch('changed');
          } else if (s.error) {
            error = s.error;
          }
        } catch { /* the next tick tries again */ }
      }, 3000);
    });
  }

  function cancelSignIn() {
    stopPolling();
    run('cancel', async () => {
      await codex.cancelSignIn();
      signIn = null;
    });
  }

  function signOut() {
    if (!confirm('Sign Codex out on this agent? Everything routed to Codex goes back to the configured AI.')) return;
    run('signout', async () => {
      apply(await codex.signOut());
      notice = 'Signed out.';
      dispatch('changed');
    });
  }

  function test() {
    run('test', async () => {
      const r = await codex.test();
      apply(r.status);
      if (r.passed) notice = 'Codex answered.';
      else error = r.message;
    });
  }

  function save() {
    run('settings', async () => {
      apply(await codex.saveSettings(model, effort || null));
      notice = 'Saved.';
    });
  }

  async function copyCode() {
    if (signIn?.code) {
      try { await navigator.clipboard.writeText(signIn.code); notice = 'Code copied.'; } catch { /* copy by hand */ }
    }
  }

  function when(utc: string | null): string {
    return utc ? new Date(utc).toLocaleString() : '—';
  }

  $: tone = !status ? 'muted' : status.state === 'ready' ? 'ok' : status.state === 'rate-limited' ? 'warn'
    : status.state === 'not-signed-in' ? 'muted' : 'bad';

  onMount(() => load(true));
  onDestroy(stopPolling); // an unfinished sign-in ends on its own deadline on the server
</script>

<section class="info-section">
  <h2 class="section-heading">
    <Sparkles size={14} />
    Codex (your ChatGPT subscription)
    <span class="heading-help">
      <ChannelHelp label="Codex" text="Use your own ChatGPT plan for this agent's AI, through OpenAI's official Codex program. Choose which parts use it under AI routing. Codex answers in text only here; all of its tools are switched off. Only you should sign in: your plan is yours alone and is not for sharing." />
    </span>
    <button class="btn btn-ghost icon-btn" on:click={() => load(true)} disabled={!!busy} title="Refresh status" aria-label="Refresh Codex status">
      <RefreshCw size={13} />
    </button>
  </h2>

  {#if loading}
    <div class="row"><div class="spinner"></div><span class="muted">Checking…</span></div>
  {:else if status}
    <div class="status-line">
      {#if tone === 'ok'}<CheckCircle size={16} style="color: var(--success)" />{:else if tone === 'warn'}<AlertTriangle size={16} style="color: var(--warning, #f59e0b)" />{:else if tone === 'bad'}<XCircle size={16} style="color: var(--danger)" />{/if}
      <span class="state {tone}">{STATE_TEXT[status.state] ?? status.state}</span>
      {#if status.version}<span class="muted">Codex {status.version}</span>{/if}
    </div>

    {#if !status.installed}
      <div class="row">
        <span>Codex is not installed where this agent runs.</span>
        <ChannelHelp label="installing Codex" text="On your own computer, install the Codex CLI (npm install -g @openai/codex, or OpenAI's installer), then refresh. A hosted agent gets it only when its image includes Codex." />
      </div>
    {:else if !status.allowed}
      <div class="msg bad">{status.notAllowedReason ?? 'Codex is not available for this account.'}</div>
    {:else}
      {#if status.signedIn}
        <div class="facts">
          <div><span class="label">Signed in</span><span>{status.signedInWith ? `with ${status.signedInWith}` : 'yes'}</span></div>
          {#if status.lastSuccessUtc}<div><span class="label">Last answer</span><span>{when(status.lastSuccessUtc)}{status.lastModel ? ` · ${status.lastModel}` : ''}</span></div>{/if}
          {#if status.lastTestUtc}<div><span class="label">Last test</span><span class:ok={status.lastTestPassed} class:bad={status.lastTestPassed === false}>{status.lastTestPassed ? 'Passed' : 'Failed'} · {when(status.lastTestUtc)}</span></div>{/if}
          {#if status.lastError}<div><span class="label">Last error</span><span class="bad">{status.lastError}</span></div>{/if}
        </div>
        <div class="actions">
          <button class="btn btn-primary" on:click={test} disabled={!!busy}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
          <button class="btn btn-ghost" on:click={signOut} disabled={!!busy}>Sign out</button>
          <ChannelHelp label="testing Codex" text="Test sends one tiny message, which uses a little of your ChatGPT plan (Codex adds its own instructions to every call). Refresh only checks the program and the sign-in." />
        </div>
      {/if}

      {#if signIn?.state === 'AwaitingApproval'}
        <div class="signin">
          <a class="btn btn-primary" href={signIn.link} target="_blank" rel="noopener noreferrer">
            <ExternalLink size={13} /> Open ChatGPT to approve
          </a>
          <div class="code-row">
            <span class="label">Enter this code</span>
            <code class="code">{signIn.code}</code>
            <button class="btn btn-ghost icon-btn" on:click={copyCode} title="Copy the code" aria-label="Copy the code"><Copy size={13} /></button>
            <ChannelHelp label="the device code" text="The code only lets you approve this waiting sign-in in your own ChatGPT account; it expires in 15 minutes. If ChatGPT says device sign-in is off, turn it on in ChatGPT's security settings and start again." />
          </div>
          <div class="row"><span class="muted">Waiting for your approval…</span><button class="btn btn-ghost" on:click={cancelSignIn}>Cancel</button></div>
        </div>
      {:else}
        <div class="actions">
          <button class="btn" class:btn-primary={!status.signedIn} class:btn-ghost={status.signedIn} on:click={startSignIn} disabled={!!busy}>
            {busy === 'signin' ? 'Starting…' : status.signedIn ? 'Sign in again' : 'Sign in with ChatGPT'}
          </button>
        </div>
      {/if}

      {#if status.signedIn}
        <div class="choices">
          <label class="choice">
            <span class="label">Model</span>
            <input class="input" list="codex-models" bind:value={model} on:change={save} disabled={!!busy} aria-label="Codex model" />
            <datalist id="codex-models">{#each status.models as m}<option value={m}></option>{/each}</datalist>
            <ChannelHelp label="the Codex model" text="'default' lets Codex pick the model your plan offers. You can type any model name Codex accepts; if your plan does not offer it, the last error says so." />
          </label>
          <label class="choice">
            <span class="label">Reasoning</span>
            <select class="input select" bind:value={effort} on:change={save} disabled={!!busy} aria-label="Codex reasoning effort">
              <option value="">Default</option><option value="low">Low</option><option value="medium">Medium</option><option value="high">High</option>
            </select>
          </label>
        </div>
      {/if}
    {/if}

    {#if error}<div class="msg bad">{error}</div>{/if}
    {#if notice}<div class="msg ok">{notice}</div>{/if}
  {:else}
    <div class="msg bad">{error || 'Codex status could not be read.'}</div>
  {/if}
</section>

<style>
  .info-section { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 1.25rem; }
  .section-heading { display: flex; align-items: center; gap: 0.5rem; font-size: 0.8125rem; font-weight: 600; color: var(--text-2);
    margin: 0 0 0.875rem; text-transform: uppercase; letter-spacing: 0.04em; }
  .heading-help { text-transform: none; letter-spacing: normal; }
  .icon-btn { padding: 0.25rem 0.5rem; }
  .section-heading .icon-btn { margin-left: auto; }
  .status-line, .row, .actions, .code-row { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; margin-bottom: 0.75rem; }
  .state { font-weight: 600; font-size: 0.875rem; }
  .muted { color: var(--text-3); font-size: 0.8125rem; }
  .facts { display: flex; flex-direction: column; gap: 0.35rem; margin-bottom: 0.75rem; font-size: 0.8125rem; }
  .facts > div { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
  .label { color: var(--text-2); min-width: 130px; font-weight: 500; }
  .input { flex: 0 1 220px; min-width: 140px; padding: 0.45rem 0.6rem; border-radius: var(--radius-sm); border: 1px solid var(--border);
    background: var(--surface-2); color: var(--text); }
  .select { flex: 0 1 160px; }
  .signin { display: flex; flex-direction: column; gap: 0.4rem; margin-bottom: 0.75rem; align-items: flex-start; }
  .signin a { display: inline-flex; align-items: center; gap: 0.4rem; text-decoration: none; }
  .code { font-family: monospace; font-size: 1.1rem; font-weight: 700; letter-spacing: 0.08em; padding: 0.2rem 0.5rem;
    background: var(--surface-2); border-radius: var(--radius-sm); color: var(--text); }
  .choices { display: flex; flex-direction: column; gap: 0.5rem; border-top: 1px solid var(--border); padding-top: 0.75rem; }
  .choice { display: flex; align-items: center; gap: 0.5rem; font-size: 0.8125rem; color: var(--text); }
  .msg { font-size: 0.8125rem; margin-top: 0.5rem; }
  .ok { color: var(--success); }
  .warn { color: var(--warning, #f59e0b); }
  .bad { color: var(--danger); }
  @media (max-width: 480px) { .label { min-width: 100%; } }
</style>
