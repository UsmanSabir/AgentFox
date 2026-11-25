<script lang="ts">
  import { onMount } from 'svelte';
  import { Route } from 'lucide-svelte';
  import ChannelHelp from './ChannelHelp.svelte';
  import { aiRouting, type Routing } from '#lib/codex.ts';

  /** Bumped by the provider cards after a sign-in changes, so "in use" re-reads. */
  export let refreshKey = 0;

  let routing: Routing | null = null;
  let error = '';
  let busy = false;

  async function load() {
    try {
      routing = await aiRouting.read();
      error = '';
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }

  async function choose(purpose: string, provider: string) {
    busy = true;
    try {
      routing = await aiRouting.set(purpose, provider);
      error = '';
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }

  function label(id: string): string {
    return routing?.providers.find((p) => p.id === id)?.label ?? id;
  }

  function note(provider: string, inUse: string): string {
    if (provider === inUse) return '';
    const p = routing?.providers.find((x) => x.id === provider);
    if (p && !p.allowed) return `${p.label} is not available for this account; the configured AI answers.`;
    return `${label(provider)} is not signed in; the configured AI answers until it is.`;
  }

  $: refreshKey, load();
  onMount(load);
</script>

<section class="info-section">
  <h2 class="section-heading">
    <Route size={14} />
    AI routing
    <span class="heading-help">
      <ChannelHelp label="AI routing" text="Choose which AI answers each part of the agent. Claude Code and Codex use your own subscriptions and only answer in text: chat on either cannot read your account, look up prices or place orders. A choice that is not signed in falls back to the configured AI." />
    </span>
  </h2>

  {#if routing}
    <div class="rows">
      {#each routing.purposes as p (p.id)}
        <label class="row">
          <span class="label">{p.label}</span>
          <select class="input" value={p.provider} disabled={busy} on:change={(e) => choose(p.id, e.currentTarget.value)}
            aria-label={`AI for ${p.label}`}>
            {#each routing.providers as provider (provider.id)}
              <option value={provider.id}>{provider.label}</option>
            {/each}
          </select>
          {#if note(p.provider, p.inUse)}<span class="warn">{note(p.provider, p.inUse)}</span>{/if}
        </label>
      {/each}
    </div>
  {/if}
  {#if error}<div class="msg bad">{error}</div>{/if}
</section>

<style>
  .info-section { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 1.25rem; }
  .section-heading { display: flex; align-items: center; gap: 0.5rem; font-size: 0.8125rem; font-weight: 600; color: var(--text-2);
    margin: 0 0 0.875rem; text-transform: uppercase; letter-spacing: 0.04em; }
  .heading-help { text-transform: none; letter-spacing: normal; }
  .rows { display: flex; flex-direction: column; gap: 0.6rem; }
  .row { display: flex; align-items: center; gap: 0.6rem; flex-wrap: wrap; font-size: 0.8125rem; }
  .label { color: var(--text-2); min-width: 150px; font-weight: 500; }
  .input { flex: 0 1 200px; min-width: 150px; padding: 0.45rem 0.6rem; border-radius: var(--radius-sm); border: 1px solid var(--border);
    background: var(--surface-2); color: var(--text); }
  .warn { color: var(--warning, #f59e0b); font-size: 0.75rem; }
  .msg { font-size: 0.8125rem; margin-top: 0.5rem; }
  .bad { color: var(--danger); }
  @media (max-width: 480px) { .label { min-width: 100%; } }
</style>
