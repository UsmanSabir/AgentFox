<script lang="ts">
  import '../app.css';
  import Sidebar from '$lib/components/Sidebar.svelte';
  import {
    sidebarCollapsed, agentStatus, resetChat, uiMode, uiTheme,
    type UiMode, type UiTheme
  } from '$lib/stores';
  import { api } from '$lib/api';
  import { onMount } from 'svelte';
  import { page } from '$app/stores';
  import { MessageSquare, Zap, Sun, Moon } from 'lucide-svelte';
  import { goto } from '$app/navigation';
  import { parseSidebarCollapsed, SIDEBAR_COLLAPSED_KEY } from '$lib/sidebarPreference';

  function startNewChat(event: MouseEvent) {
    event.preventDefault();
    resetChat();
    goto('/chat');
  }

  const UI_MODE_KEY = 'agentfox.uiMode';
  const UI_THEME_KEY = 'agentfox.theme';

  function selectMode(mode: UiMode) {
    uiMode.set(mode);
    localStorage.setItem(UI_MODE_KEY, mode);
    if (mode === 'simple' && $page.url.pathname !== '/chat') goto('/chat');
  }

  function applyTheme(theme: UiTheme, persist = true) {
    uiTheme.set(theme);
    document.documentElement.dataset.theme = theme;
    document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')
      ?.setAttribute('content', theme === 'light' ? '#f5f6f8' : '#0c0d10');
    if (persist) localStorage.setItem(UI_THEME_KEY, theme);
  }

  function toggleTheme() {
    applyTheme($uiTheme === 'light' ? 'dark' : 'light');
  }

  // Restore the display preference and poll agent status every 5 s.
  onMount(() => {
    // app.html applies the saved value before first paint; mirror it into the shared store so open
    // plugin frames receive the same theme and future changes propagate without a reload.
    const initialTheme = document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
    applyTheme(initialTheme, false);

    // An explicit user choice wins on every viewport. Without one, narrow screens start on the
    // compact rail so plugin dashboards and chat retain usable width.
    let savedSidebar: boolean | null = null;
    try { savedSidebar = parseSidebarCollapsed(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)); }
    catch { /* Browser storage is optional. */ }
    sidebarCollapsed.set(savedSidebar ?? window.matchMedia('(max-width: 760px)').matches);

    const savedMode = localStorage.getItem(UI_MODE_KEY);
    if (savedMode === 'simple' || savedMode === 'advanced') {
      uiMode.set(savedMode);
      if (savedMode === 'simple' && $page.url.pathname !== '/chat') goto('/chat');
    }

    async function poll() {
      try {
        agentStatus.set(await api.status());
      } catch {
        agentStatus.set(null);
      }
    }
    poll();
    const id = setInterval(poll, 5000);
    return () => clearInterval(id);
  });

  $: collapsed = $sidebarCollapsed;
  $: status    = $agentStatus;
  $: simpleMode = $uiMode === 'simple';

  const pageTitles: Record<string, string> = {
    '/':        'Dashboard',
    '/chat':    'Chat',
    '/agents':  'Agents',
    '/memory':  'Memory',
    '/skills':  'Skills',
    '/tools':   'Tools',
    '/mcp':     'MCP Servers',
    '/settings':'Settings',
  };

  $: title = (() => {
    const p = $page.url.pathname;
    // Plugin-supplied pages (/ext/{slug}) are not in the static map — title them from the slug so a
    // plugin page doesn't have to register anything host-side just to name its own header.
    if (p.startsWith('/ext/')) {
      const slug = p.slice('/ext/'.length).split('/')[0];
      return slug ? slug.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase()) : 'Plugin';
    }
    for (const [k, v] of Object.entries(pageTitles)) {
      if (p === '/' ? p === k : p.startsWith(k) && k !== '/') return v;
    }
    return pageTitles[p] ?? 'AgentFox';
  })();
</script>

<div class="app-shell" class:simple-mode={simpleMode} style="--offset: {collapsed ? '64px' : 'var(--sidebar-w)'}">
  {#if !simpleMode}
    <Sidebar />
  {/if}

  <div class="main-area" class:simple={simpleMode}>
    <!-- Header -->
    <header class="header">
      <div class="header-left">
        {#if simpleMode}
          <span class="simple-brand"><Zap size={16} /> AgentFox</span>
        {:else}
          <h1 class="header-title">{title}</h1>
        {/if}
      </div>
      <div class="header-right">
        <button
          class="theme-toggle"
          on:click={toggleTheme}
          aria-label={$uiTheme === 'light' ? 'Use dark theme' : 'Use light theme'}
          title={$uiTheme === 'light' ? 'Use dark theme' : 'Use light theme'}
        >
          {#if $uiTheme === 'light'}<Moon size={16} />{:else}<Sun size={16} />{/if}
        </button>

        <!-- Quick chat shortcut -->
        <a href="/chat" class="chat-shortcut" title="Start a new chat" on:click={startNewChat}>
          <MessageSquare size={16} />
          <span>New chat</span>
        </a>

        <div class="mode-switch" aria-label="Interface mode">
          <button
            class:active={simpleMode}
            aria-pressed={simpleMode}
            on:click={() => selectMode('simple')}
          >Simple</button>
          <button
            class:active={!simpleMode}
            aria-pressed={!simpleMode}
            on:click={() => selectMode('advanced')}
          >Advanced</button>
        </div>

        {#if !simpleMode}
        <!-- Agent status pill -->
        <div
          class="status-pill"
          class:ready={status?.ready}
          title={`${status?.name ?? 'AgentFox'} ${status?.status ?? '…'}`}
        >
          <span class="status-dot" class:ready={status?.ready}></span>
          <span>{status?.name ?? 'AgentFox'}</span>
          <span class="status-text">{status?.status ?? '…'}</span>
        </div>
        {/if}
      </div>
    </header>

    <!-- Page content -->
    <main class="content">
      <slot />
    </main>
  </div>
</div>

<style>
  .app-shell {
    display: flex;
    height: 100vh;
    overflow: hidden;
  }

  .main-area {
    display: flex;
    flex-direction: column;
    flex: 1;
    margin-left: var(--offset);
    transition: margin-left 0.2s cubic-bezier(0.4, 0, 0.2, 1);
    overflow: hidden;
  }
  .main-area.simple { margin-left: 0; }

  /* Header */
  .header {
    height: var(--header-h);
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0 1.5rem;
    border-bottom: 1px solid var(--border);
    background: var(--surface);
    flex-shrink: 0;
    gap: 1rem;
  }

  .header-left { display: flex; align-items: center; gap: 0.75rem; }
  .header-right { display: flex; align-items: center; gap: 0.75rem; margin-left: auto; }

  .header-title {
    font-size: 0.9375rem;
    font-weight: 600;
    margin: 0;
    color: var(--text);
  }

  .theme-toggle {
    width: 32px;
    height: 32px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex: 0 0 auto;
    border: 1px solid var(--border-md);
    border-radius: var(--radius-sm);
    background: var(--surface-2);
    color: var(--text-2);
    cursor: pointer;
    transition: color .15s, background .15s, border-color .15s;
  }
  .theme-toggle:hover { color:var(--primary); background:var(--surface-3); border-color:var(--border-high); }
  .theme-toggle:focus-visible { outline:2px solid var(--primary); outline-offset:2px; }

  .simple-brand {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    color: var(--text);
    font-weight: 700;
  }
  .simple-brand :global(svg) { color: var(--primary); }

  .mode-switch {
    display: flex;
    padding: 2px;
    border: 1px solid var(--border-md);
    border-radius: 8px;
    background: var(--surface-2);
  }
  .mode-switch button {
    border: 0;
    border-radius: 5px;
    padding: 0.25rem 0.625rem;
    background: transparent;
    color: var(--text-3);
    font: inherit;
    font-size: 0.75rem;
    cursor: pointer;
  }
  .mode-switch button.active {
    background: var(--surface-3);
    color: var(--text);
    box-shadow: 0 1px 2px rgba(0, 0, 0, 0.25);
  }

  /* Quick chat link */
  .chat-shortcut {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.3rem 0.75rem;
    border-radius: var(--radius-sm);
    background: var(--primary-dim);
    color: var(--primary);
    text-decoration: none;
    font-size: 0.75rem;
    font-weight: 500;
    transition: background 0.15s;
  }
  .chat-shortcut:hover { background: var(--primary-glow); }

  /* Status pill */
  .status-pill {
    display: flex;
    align-items: center;
    gap: 0.375rem;
    padding: 0.275rem 0.75rem;
    border-radius: 99px;
    background: var(--surface-2);
    border: 1px solid var(--border-md);
    font-size: 0.75rem;
    color: var(--text-2);
    white-space: nowrap;
  }

  .status-dot {
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--text-3);
    flex-shrink: 0;
    transition: background 0.3s;
  }
  .status-dot.ready { background: var(--success); box-shadow: 0 0 5px var(--success); }

  .status-text { color: var(--text-3); }

  /* Content */
  .content {
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden;
  }

  @media (max-width: 640px) {
    .header { padding: 0 0.75rem; }
    .chat-shortcut span { display: none; }
    .simple-brand { font-size: 0.875rem; }
    .status-text { display: none; }
    .status-pill { padding-inline: 0.55rem; }
    .header-right { gap: 0.4rem; }
  }

  @media (max-width: 760px) {
    /* The expanded sidebar overlays the page as a drawer at narrow widths instead of reducing the
       already-small content viewport to 80px or less. */
    .app-shell { --offset: 64px !important; }
  }

  @media (max-width: 480px) {
    .header { gap: 0.5rem; padding-inline: 0.5rem; }
    .header-left { min-width: 0; }
    .header-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .mode-switch button { padding-inline: 0.4rem; font-size: 0.6875rem; }
    .status-pill { padding: 0.5rem; }
    .status-pill span:not(.status-dot) { display: none; }
  }
</style>
