<script lang="ts">
  import '../app.css';
  import Sidebar from '$lib/components/Sidebar.svelte';
  import { sidebarCollapsed, agentStatus } from '$lib/stores';
  import { api } from '$lib/api';
  import { onMount } from 'svelte';
  import { page } from '$app/stores';
  import { MessageSquare } from 'lucide-svelte';

  // Poll agent status every 5 s
  onMount(() => {
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
    for (const [k, v] of Object.entries(pageTitles)) {
      if (p === '/' ? p === k : p.startsWith(k) && k !== '/') return v;
    }
    return pageTitles[p] ?? 'AgentFox';
  })();
</script>

<div class="app-shell" style="--offset: {collapsed ? '64px' : 'var(--sidebar-w)'}">
  <Sidebar />

  <div class="main-area">
    <!-- Header -->
    <header class="header">
      <div class="header-left">
        <h1 class="header-title">{title}</h1>
      </div>
      <div class="header-right">
        <!-- Quick chat shortcut -->
        <a href="/chat" class="chat-shortcut" title="Open Chat">
          <MessageSquare size={16} />
          <span>New chat</span>
        </a>

        <!-- Agent status pill -->
        <div class="status-pill" class:ready={status?.ready}>
          <span class="status-dot" class:ready={status?.ready}></span>
          <span>{status?.name ?? 'AgentFox'}</span>
          <span class="status-text">{status?.status ?? '…'}</span>
        </div>
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
  .chat-shortcut:hover { background: rgba(129,140,248,0.2); }

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
</style>
