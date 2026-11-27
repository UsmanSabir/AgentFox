<script lang="ts">
  import { onMount, untrack } from 'svelte';
  import { page } from '$app/state';
  import { afterNavigate } from '$app/navigation';
  import { sidebarCollapsed, uiTheme, type UiTheme } from '#lib/stores.ts';
  import { get } from 'svelte/store';
  import { pluginSidebarRequest } from '#lib/pluginLayout.ts';
  import { api, getManagementApiKey, type PluginUiPageInfo } from '#lib/api.ts';

  // Generic host for a plugin-supplied UI. This file is the ONLY thing the host app knows about
  // plugin pages: it looks the slug up in the manifest and frames the assets the backend serves at
  // /ext/{slug}/. No plugin-specific route, type, or dependency lives in this app.
  //
  // The frame is same-origin, so the page inside it already shares this tab's sessionStorage and can
  // read the management API key exactly as $lib/api does. We also post it (with the theme) on load,
  // so a plugin page can take either route and does not have to know our storage key.

  let pages: PluginUiPageInfo[] | null = $state<PluginUiPageInfo[] | null>(null);
  let error: string | null = $state<string | null>(null);
  let frame: HTMLIFrameElement | null = $state<HTMLIFrameElement | null>(null);
  let previousSidebar: boolean | null = null;
  function restoreSidebar() {
    if (previousSidebar === null) return;
    sidebarCollapsed.set(previousSidebar);
    previousSidebar = null;
  }

  let slug = $state(page.params.slug);
  afterNavigate(() => { slug = page.params.slug; });
  let current = $derived(pages?.find((p) => p.slug === slug) ?? null);
  // Svelte may reuse this route component for another plugin slug.
  $effect.pre(() => {
    // Reads only what the old `$:` named; the call is untracked so its own bookkeeping cannot re-trigger it.
    void slug;
    untrack(() => restoreSidebar());
  });

  onMount(() => {
    const receive = (event: MessageEvent) => {
      const request = pluginSidebarRequest(event.origin, window.location.origin, event.source, frame?.contentWindow, event.data);
      if (request === 'collapsed') {
        previousSidebar ??= get(sidebarCollapsed);
        sidebarCollapsed.set(true);
      } else if (request === 'default') restoreSidebar();
    };
    window.addEventListener('message', receive);
    return () => { window.removeEventListener('message', receive); restoreSidebar(); };
  });

  onMount(async () => {
    try {
      pages = await api.pluginUi.list();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  });

  function handoff(theme: UiTheme = $uiTheme) {
    frame?.contentWindow?.postMessage(
      {
        type:   'agentfox:context',
        apiKey: getManagementApiKey(),
        theme,
        base:   '/api'
      },
      window.location.origin
    );
  }

  // The frame may already be open when the header toggle changes. Re-send the host context so plugin
  // pages switch in place instead of waiting for their next navigation or reload.
  $effect.pre(() => {
    // Reads only what the old `$:` named; the call is untracked so its own bookkeeping cannot re-trigger it.
    const target = frame, theme = $uiTheme;
    untrack(() => { if (target) handoff(theme); });
  });
</script>

<svelte:head><title>{current?.title ?? 'Plugin'} · AgentFox</title></svelte:head>

{#if error}
  <div class="ext-msg error">Could not load plugin pages: {error}</div>
{:else if pages === null}
  <div class="ext-msg">Loading…</div>
{:else if !current}
  <div class="ext-msg error">
    No plugin UI is mounted at <code>/ext/{slug}</code>.
    <span>The plugin may be disabled, or built without its UI assets.</span>
  </div>
{:else}
  <iframe
    bind:this={frame}
    src={current.entry}
    title={current.title}
    allow="fullscreen"
    allowfullscreen
    onload={() => handoff()}
  ></iframe>
{/if}

<style>
  /* The plugin page owns its whole area and scrolls internally; the host chrome does not scroll. */
  iframe { width: 100%; height: 100%; border: 0; display: block; background: var(--bg); }
  .ext-msg {
    padding: 2rem; color: var(--text-3); display: flex; flex-direction: column; gap: .4rem;
    font-size: .85rem;
  }
  .ext-msg.error { color: var(--danger); }
  .ext-msg span { color: var(--text-3); font-size: .75rem; }
  .ext-msg code {
    background: var(--surface-2); padding: .1rem .3rem; border-radius: 3px; font-size: .78rem;
  }
</style>
