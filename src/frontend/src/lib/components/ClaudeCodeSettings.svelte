<script lang="ts">
  import { createEventDispatcher, onDestroy, onMount } from 'svelte';
  import { Bot, RefreshCw, CheckCircle, XCircle, AlertTriangle, ExternalLink } from 'lucide-svelte';
  import ChannelHelp from './ChannelHelp.svelte';
  import { claudeCode, type ClaudeCodeStatus, type SignInSnapshot } from '#lib/claudeCode.ts';

  const dispatch = createEventDispatcher<{ changed: void }>();

  let status: ClaudeCodeStatus | null = $state<ClaudeCodeStatus | null>(null);
  let loading = $state(true);
  let busy = $state('');            // which action is running
  let error = $state('');           // last action's error, shown until the next action
  let notice = $state('');          // last action's result

  // Sign-in relay
  let signIn: SignInSnapshot | null = $state<SignInSnapshot | null>(null);
  let code = $state('');

  // Paste a token
  let showPaste = $state(false);
  let token = $state('');

  // The model (edited locally, saved on change). Which purposes use Claude Code is the AI routing card's.
  let model = $state('');

  const STATE_TEXT: Record<string, string> = {
    'ready': 'Ready',
    'not-installed': 'Not installed',
    'not-allowed': 'Not available for this account',
    'not-signed-in': 'Not signed in',
    'reauthentication-required': 'Sign in again',
    'expired': 'Sign-in has probably expired',
    'rate-limited': 'Usage limit reached',
    'expiring-soon': 'Sign-in expires soon',
  };

  function apply(s: ClaudeCodeStatus) {
    status = s;
    model = s.model;
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
    await run(refresh ? 'refresh' : '', async () => apply(await claudeCode.status(refresh)));
    loading = false;
  }

  function saveChoices() {
    run('choices', async () => {
      apply(await claudeCode.saveChoices({ model }));
      notice = 'Saved.';
    });
  }

  function test() {
    run('test', async () => {
      const r = await claudeCode.test();
      apply(r.status);
      if (r.passed) notice = 'Claude answered.';
      else error = r.message;
    });
  }

  function savePastedToken() {
    run('token', async () => {
      const value = token.trim();
      token = '';                       // never kept in the page after sending
      apply(await claudeCode.saveToken(value));
      showPaste = false;
      notice = 'Signed in.';
      dispatch('changed');
    });
  }

  function removeToken() {
    if (!confirm('Remove the Claude sign-in from this agent? Everything switched to Claude goes back to the configured AI.')) return;
    run('remove', async () => {
      apply(await claudeCode.removeToken());
      notice = 'Signed out.';
      dispatch('changed');
    });
  }

  function startSignIn() {
    run('signin', async () => {
      code = '';
      signIn = await claudeCode.startSignIn();
      if (signIn.state === 'Failed') error = signIn.error ?? 'The sign-in could not start.';
    });
  }

  function submitCode() {
    const value = code.trim();
    code = '';
    run('code', async () => {
      signIn = await claudeCode.submitCode(value);
      if (signIn.state === 'Completed') {
        signIn = null;
        notice = 'Signed in.';
        await load(true);
        dispatch('changed');
      } else if (signIn.error) {
        error = signIn.error;
      }
    });
  }

  function cancelSignIn() {
    run('cancel', async () => {
      await claudeCode.cancelSignIn();
      signIn = null;
      code = '';
    });
  }

  function when(utc: string | null): string {
    return utc ? new Date(utc).toLocaleString() : '—';
  }

  function day(utc: string | null): string {
    return utc ? new Date(utc).toLocaleDateString() : '—';
  }

  let awaitingCode = $derived(signIn?.state === 'AwaitingCode');
  let tone = $derived(!status ? 'muted'
    : status.state === 'ready' ? 'ok'
    : status.state === 'expiring-soon' || status.state === 'rate-limited' ? 'warn'
    : status.state === 'not-signed-in' ? 'muted' : 'bad');

  onMount(() => load(true));
  onDestroy(() => {
    // An unfinished sign-in is left to its own deadline on the server; nothing to clear here but the code.
    code = '';
    token = '';
  });
</script>

<section class="info-section">
  <h2 class="section-heading">
    <Bot size={14} />
    Claude Code (your subscription)
    <span class="heading-help">
      <ChannelHelp label="Claude Code" text="Use your own Claude Pro or Max subscription for this agent's AI, through the official Claude Code program. Choose which parts use it under AI routing; everything else keeps the configured AI. Only you should sign in here: the subscription is yours alone and is not for sharing." />
    </span>
    <button class="btn btn-ghost icon-btn" onclick={() => load(true)} disabled={!!busy} title="Refresh status" aria-label="Refresh Claude Code status">
      <RefreshCw size={13} />
    </button>
  </h2>

  {#if loading}
    <div class="row"><div class="spinner"></div><span class="muted">Checking…</span></div>
  {:else if status}
    <div class="status-line">
      {#if tone === 'ok'}<CheckCircle size={16} style="color: var(--success)" />{:else if tone === 'warn'}<AlertTriangle size={16} style="color: var(--warning, #f59e0b)" />{:else if tone === 'bad'}<XCircle size={16} style="color: var(--danger)" />{/if}
      <span class="state {tone}">{STATE_TEXT[status.state] ?? status.state}</span>
      {#if status.version}<span class="muted">Claude Code {status.version}</span>{/if}
    </div>

    {#if status.problem}<div class="msg bad">{status.problem}</div>{/if}

    {#if !status.installed}
      <div class="row">
        <span>Claude Code is not installed where this agent runs.</span>
        <ChannelHelp label="installing Claude Code" text="Hosted agents get it with the agent's image. On your own computer, install Claude Code from claude.com/code (Windows: winget install Anthropic.ClaudeCode), then refresh." />
      </div>
    {:else if !status.allowed}
      <div class="msg bad">{status.notAllowedReason ?? 'Claude Code is not available for this account.'}</div>
    {:else}
      <!-- Sign-in -->
      {#if status.signedIn}
        <div class="facts">
          <div><span class="label">Signed in</span><span>{day(status.tokenSavedUtc)}{status.tokenSource === 'signin' ? ' · with Claude' : ' · pasted token'}</span></div>
          <div>
            <span class="label">Valid until about</span>
            <span class:warn={status.expiringSoon}>{day(status.tokenExpiresEstimateUtc)}</span>
            <ChannelHelp label="sign-in expiry" text="A Claude Code sign-in lasts one year. This date is counted from when it was saved here, so it is an estimate. Sign in again before then; the old one keeps working until the new one is saved." />
          </div>
          {#if status.lastSuccessUtc}<div><span class="label">Last answer</span><span>{when(status.lastSuccessUtc)}{status.lastModel ? ` · ${status.lastModel}` : ''}</span></div>{/if}
          {#if status.lastTestUtc}<div><span class="label">Last test</span><span class:ok={status.lastTestPassed} class:bad={status.lastTestPassed === false}>{status.lastTestPassed ? 'Passed' : 'Failed'} · {when(status.lastTestUtc)}</span></div>{/if}
          {#if status.usage}
            <div>
              <span class="label">Subscription use</span>
              <span>
                {#if status.usage.fiveHourPercent !== null}5-hour {status.usage.fiveHourPercent}%{/if}
                {#if status.usage.sevenDayPercent !== null} · week {status.usage.sevenDayPercent}% (resets {when(status.usage.sevenDayResetsUtc)}){/if}
              </span>
              <ChannelHelp label="subscription use" text="As Claude Code last reported it, after this agent's latest call. It covers all your use of Claude, not only this agent. Shown only when Claude reports it." />
            </div>
          {/if}
          {#if status.lastError}<div><span class="label">Last error</span><span class="bad">{status.lastError}</span></div>{/if}
        </div>
        <div class="actions">
          <button class="btn btn-primary" onclick={test} disabled={!!busy}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
          <button class="btn btn-ghost" onclick={removeToken} disabled={!!busy}>Remove sign-in</button>
          <ChannelHelp label="testing the connection" text="Test sends Claude one tiny message, so it uses a sliver of your subscription. Refresh only checks the program and the sign-in, and uses none." />
        </div>
      {/if}

      {#if !awaitingCode}
        <div class="actions">
          {#if status.signInSupported}
            <button class="btn" class:btn-primary={!status.signedIn} class:btn-ghost={status.signedIn} onclick={startSignIn} disabled={!!busy}>
              {busy === 'signin' ? 'Starting…' : status.signedIn ? 'Sign in again' : 'Sign in with Claude'}
            </button>
          {/if}
          <button class="btn btn-ghost" onclick={() => (showPaste = !showPaste)} disabled={!!busy}>
            {showPaste ? 'Hide token' : status.signInSupported ? 'Paste a token instead' : status.signedIn ? 'Replace token' : 'Paste a token'}
          </button>
          <ChannelHelp label="signing in" text={status.signInSupported
            ? 'Sign in with Claude: open the link, approve, and paste back the code Claude shows. Or, on a computer with Claude Code installed, run "claude setup-token" in a terminal and paste the token it prints.'
            : 'On a computer with Claude Code installed, run "claude setup-token" in a terminal, approve in the browser, and paste the token it prints (it starts with sk-ant-oat01-).'} />
        </div>
      {/if}

      {#if awaitingCode && signIn}
        <div class="signin">
          <a class="btn btn-primary" href={signIn.link} target="_blank" rel="noopener noreferrer">
            <ExternalLink size={13} /> Open claude.ai to approve
          </a>
          <div class="row">
            <input class="input" type="password" bind:value={code} placeholder="Code from Claude" autocomplete="off" aria-label="Code from Claude" />
            <button class="btn btn-primary" onclick={submitCode} disabled={!code.trim() || !!busy}>{busy === 'code' ? 'Finishing…' : 'Finish sign-in'}</button>
            <button class="btn btn-ghost" onclick={cancelSignIn} disabled={busy === 'code'}>Cancel</button>
          </div>
        </div>
      {/if}

      {#if showPaste && !awaitingCode}
        <div class="row">
          <input class="input" type="password" bind:value={token} placeholder="sk-ant-oat01-…" autocomplete="off" aria-label="Claude Code token" />
          <button class="btn btn-primary" onclick={savePastedToken} disabled={!token.trim() || !!busy}>{busy === 'token' ? 'Checking…' : 'Save'}</button>
        </div>
      {/if}

      <!-- Choices -->
      {#if status.signedIn}
        <div class="choices">
          <label class="choice">
            <span class="label">Model</span>
            <select class="input select" bind:value={model} onchange={saveChoices} disabled={!!busy}>
              {#each status.models as m}<option value={m}>{m}</option>{/each}
            </select>
            <ChannelHelp label="the model" text="Sonnet suits most work; Opus is stronger and uses more of your subscription; Haiku is fastest and lightest. Not every subscription offers every model: if one is refused, the last error says so." />
          </label>
        </div>
      {/if}
    {/if}

    {#if error}<div class="msg bad">{error}</div>{/if}
    {#if notice}<div class="msg ok">{notice}</div>{/if}
  {:else}
    <div class="msg bad">{error || 'Claude Code status could not be read.'}</div>
  {/if}
</section>

<style>
  .info-section { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 1.25rem; }
  .section-heading { display: flex; align-items: center; gap: 0.5rem; font-size: 0.8125rem; font-weight: 600; color: var(--text-2);
    margin: 0 0 0.875rem; text-transform: uppercase; letter-spacing: 0.04em; }
  .heading-help { text-transform: none; letter-spacing: normal; }
  .icon-btn { margin-left: auto; padding: 0.25rem 0.5rem; }
  .status-line, .row, .actions { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; margin-bottom: 0.75rem; }
  .state { font-weight: 600; font-size: 0.875rem; }
  .muted { color: var(--text-3); font-size: 0.8125rem; }
  .facts { display: flex; flex-direction: column; gap: 0.35rem; margin-bottom: 0.75rem; font-size: 0.8125rem; }
  .facts > div { display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
  .label { color: var(--text-2); min-width: 130px; font-weight: 500; }
  .input { flex: 1; min-width: 180px; padding: 0.5rem 0.7rem; border-radius: var(--radius-sm); border: 1px solid var(--border);
    background: var(--surface-2); color: var(--text); }
  .select { flex: 0 1 180px; min-width: 120px; }
  .signin { display: flex; flex-direction: column; gap: 0.6rem; margin-bottom: 0.75rem; align-items: flex-start; }
  .signin .row { width: 100%; margin-bottom: 0; }
  .signin a { display: inline-flex; align-items: center; gap: 0.4rem; text-decoration: none; }
  .choices { display: flex; flex-direction: column; gap: 0.5rem; border-top: 1px solid var(--border); padding-top: 0.75rem; }
  .choice { display: flex; align-items: center; gap: 0.5rem; font-size: 0.8125rem; color: var(--text); }
  .msg { font-size: 0.8125rem; margin-top: 0.5rem; }
  .ok { color: var(--success); }
  .warn { color: var(--warning, #f59e0b); }
  .bad { color: var(--danger); }
  @media (max-width: 480px) { .label { min-width: 100%; } }
</style>
