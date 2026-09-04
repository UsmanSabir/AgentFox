<script lang="ts">
  import { onMount } from 'svelte';
  import { page } from '$app/stores';
  import { sidebarCollapsed } from '$lib/stores';
  import { serializeSidebarCollapsed, SIDEBAR_COLLAPSED_KEY } from '$lib/sidebarPreference';
  import { api } from '$lib/api';
  import {
    LayoutDashboard,
    MessageSquare,
    Bot,
    Database,
    Puzzle,
    Wrench,
    Settings,
    ChevronLeft,
    Zap,
    Server,
    Heart,
    CalendarClock,
    Radio,
    Cpu,
    TrendingUp,
    LineChart,
    Globe,
    type Icon as IconType
  } from 'lucide-svelte';

  // Built-in pages. Plugin pages are fetched at runtime from /api/plugin-ui and rendered between
  // these and Settings, which stays last — nothing plugin-specific belongs in this file.
  const navItems = [
    { href: '/',            label: 'Dashboard',  icon: LayoutDashboard },
    { href: '/chat',        label: 'Chat',       icon: MessageSquare },
    { href: '/agents',      label: 'Agents',     icon: Bot },
    { href: '/memory',      label: 'Memory',     icon: Database },
    { href: '/skills',      label: 'Skills',     icon: Puzzle },
    { href: '/tools',       label: 'Tools',      icon: Wrench },
    { href: '/plugins',     label: 'Plugins',    icon: Cpu },
    { href: '/mcp',         label: 'MCP',        icon: Server },
    { href: '/channels',    label: 'Channels',   icon: Radio },
    { href: '/heartbeats',  label: 'Heartbeats', icon: Heart },
    { href: '/cron',        label: 'Cron Jobs',  icon: CalendarClock },
  ];

  const trailingNavItems = [
    { href: '/settings',    label: 'Settings',   icon: Settings },
  ];

  // Icons a plugin may request by name. A plugin ships no markup into the host DOM, so an unknown
  // name falls back to the generic plugin icon rather than rendering nothing.
  const pluginIcons: Record<string, typeof IconType> = {
    'trending-up': TrendingUp,
    'line-chart':  LineChart,
    'globe':       Globe,
    'database':    Database,
    'bot':         Bot,
    'puzzle':      Puzzle,
    'cpu':         Cpu
  };

  let pluginItems: { href: string; label: string; icon: typeof IconType }[] = [];

  $: collapsed = $sidebarCollapsed;
  $: current  = $page.url.pathname;

  // Live build version from the backend; falls back to the label below if unreachable.
  let version = 'v1.0.0';
  onMount(async () => {
    try {
      const info = await api.version();
      if (info?.display) version = info.display;
    } catch {
      // keep fallback — version endpoint unavailable (e.g. not yet authenticated)
    }

    try {
      const pages = await api.pluginUi.list();
      pluginItems = pages.map(p => ({
        href:  `/ext/${p.slug}`,
        label: p.title,
        icon:  pluginIcons[p.icon] ?? Puzzle
      }));
    } catch {
      // No plugin pages (or not yet authenticated) — the built-in nav still works.
    }
  });

  function isActive(href: string) {
    if (href === '/') return current === '/';
    return current.startsWith(href);
  }

  function toggleCollapsed() {
    sidebarCollapsed.update(value => {
      const next = !value;
      try { localStorage.setItem(SIDEBAR_COLLAPSED_KEY,serializeSidebarCollapsed(next)); }
      catch { /* Keep the session toggle working when browser storage is unavailable. */ }
      return next;
    });
  }
</script>

<aside
  class="sidebar"
  class:collapsed
  style="width: {collapsed ? '64px' : 'var(--sidebar-w)'};"
>
  <!-- Brand -->
  <div class="brand">
    <div class="brand-icon">
      <Zap size={18} />
    </div>
    {#if !collapsed}
      <span class="brand-name">AgentFox</span>
    {/if}
    <button
      class="collapse-btn"
      class:rotated={collapsed}
      on:click={toggleCollapsed}
      aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
      aria-expanded={!collapsed}
      aria-controls="agentfox-sidebar-navigation"
      title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
    >
      <ChevronLeft size={15} />
    </button>
  </div>

  <!-- Nav -->
  <nav class="nav" id="agentfox-sidebar-navigation">
    {#each [...navItems, ...pluginItems, ...trailingNavItems] as item}
      <a
        href={item.href}
        class="nav-item"
        class:active={isActive(item.href)}
        title={collapsed ? item.label : undefined}
      >
        <span class="nav-icon"><svelte:component this={item.icon} size={17} /></span>
        {#if !collapsed}
          <span class="nav-label">{item.label}</span>
        {/if}
      </a>
    {/each}
  </nav>

  <!-- Footer version -->
  {#if !collapsed}
    <div class="sidebar-footer">
      <span class="version" title={version}>{version}</span>
    </div>
  {/if}
</aside>

<style>
  .sidebar {
    display: flex;
    flex-direction: column;
    height: 100vh;
    background: var(--surface);
    border-right: 1px solid var(--border);
    transition: width 0.2s cubic-bezier(0.4, 0, 0.2, 1);
    overflow: hidden;
    flex-shrink: 0;
    position: fixed;
    top: 0;
    left: 0;
    z-index: 50;
  }

  /* Brand row */
  .brand {
    display: flex;
    align-items: center;
    gap: 0.625rem;
    padding: 0 0.875rem;
    height: var(--header-h);
    border-bottom: 1px solid var(--border);
    flex-shrink: 0;
  }

  .brand-icon {
    width: 30px;
    height: 30px;
    border-radius: 8px;
    background: linear-gradient(135deg, var(--primary), var(--accent));
    display: flex;
    align-items: center;
    justify-content: center;
    color: #fff;
    flex-shrink: 0;
  }

  .brand-name {
    font-size: 0.9375rem;
    font-weight: 700;
    color: var(--text);
    letter-spacing: -0.02em;
    flex: 1;
    white-space: nowrap;
  }

  .collapse-btn {
    width: 24px;
    height: 24px;
    border-radius: 6px;
    border: 1px solid var(--border-md);
    background: transparent;
    color: var(--text-3);
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    flex-shrink: 0;
    transition: all 0.2s;
    padding: 0;
    margin-left: auto;
  }
  .collapse-btn:hover { background: var(--surface-2); color: var(--text-2); }
  .collapse-btn :global(svg) { transition: transform 0.2s; }
  .collapse-btn.rotated :global(svg) { transform: rotate(180deg); }

  /* The collapsed rail is only 64px wide. Remove the expanded row's generous spacing so the
     30px brand mark and 24px expander both fit inside it instead of clipping the chevron. */
  .sidebar.collapsed .brand {
    gap: 1px;
    padding-inline: 4px;
  }
  .sidebar.collapsed .collapse-btn { margin-left: auto; }

  /* Nav */
  .nav {
    flex: 1;
    padding: 0.5rem 0.5rem;
    display: flex;
    flex-direction: column;
    gap: 2px;
    overflow-y: auto;
    overflow-x: hidden;
  }

  .nav-item {
    display: flex;
    align-items: center;
    gap: 0.625rem;
    padding: 0.5rem 0.625rem;
    border-radius: var(--radius-sm);
    color: var(--text-2);
    text-decoration: none;
    font-size: 0.8125rem;
    font-weight: 500;
    transition: background 0.12s, color 0.12s;
    white-space: nowrap;
  }

  .nav-item:hover {
    background: var(--surface-2);
    color: var(--text);
  }

  .nav-item.active {
    background: var(--primary-dim);
    color: var(--primary);
  }

  .nav-icon {
    width: 20px;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .nav-label {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  /* Footer */
  .sidebar-footer {
    padding: 0.75rem 1rem;
    border-top: 1px solid var(--border);
  }

  .version {
    font-size: 0.6875rem;
    color: var(--text-3);
  }

  @media (max-width: 760px) {
    /* An expanded mobile sidebar is a drawer. The page keeps the usable width of the 64px rail and
       all navigation remains available when the user opens it. */
    .sidebar:not(.collapsed) {
      box-shadow: 12px 0 32px rgba(0, 0, 0, 0.38);
    }
  }
</style>
