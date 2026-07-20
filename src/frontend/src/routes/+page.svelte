<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { agentStatus, tools, skills, agents } from '$lib/stores';
  import StatusBadge from '$lib/components/StatusBadge.svelte';
  import {
    Bot, Wrench, Puzzle, Database, MessageSquare,
    Activity, Clock, ArrowRight, Zap
  } from 'lucide-svelte';
  import type { MemoryEntry, SessionInfo } from '$lib/api';

  let recentMemory: MemoryEntry[] = [];
  let sessions: SessionInfo[] = [];
  let loading = true;

  onMount(async () => {
    try {
      const [toolsData, skillsData, agentsData, memData, sessionsData] = await Promise.allSettled([
        api.tools(),
        api.skills(),
        api.agents(),
        api.memory(),
        api.sessions()
      ]);

      if (toolsData.status === 'fulfilled')   tools.set(toolsData.value);
      if (skillsData.status === 'fulfilled')  skills.set(skillsData.value);
      if (agentsData.status === 'fulfilled')  agents.set(agentsData.value);
      if (memData.status === 'fulfilled')     recentMemory = memData.value.slice(0, 5);
      if (sessionsData.status === 'fulfilled') sessions = sessionsData.value.slice(0, 5);
    } finally {
      loading = false;
    }
  });

  $: status   = $agentStatus;
  $: toolList = $tools;
  $: skillList = $skills;
  $: agentList = $agents;

  function timeAgo(ts: string) {
    const diff = Date.now() - new Date(ts).getTime();
    const m = Math.floor(diff / 60000);
    if (m < 1)  return 'just now';
    if (m < 60) return `${m}m ago`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ago`;
    return `${Math.floor(h / 24)}d ago`;
  }

  function memTypeColor(type: string) {
    const map: Record<string, string> = {
      Fact:           'var(--primary)',
      UserPreference: 'var(--accent)',
      Observation:    'var(--info)',
      Conversation:   'var(--text-2)',
      ToolExecution:  'var(--warning)',
    };
    return map[type] ?? 'var(--text-3)';
  }
</script>

<div class="page-wrap fade-in">
  <!-- ── Welcome banner ─────────────────────────────────────────── -->
  <div class="welcome-banner">
    <div class="welcome-left">
      <div class="welcome-icon">
        <Zap size={20} />
      </div>
      <div>
        <h2 class="welcome-title">
          {status?.ready ? `${status.name} is ready` : 'Connecting to AgentFox…'}
        </h2>
        <p class="welcome-sub">
          Multi-agent AI framework — sub-agents, memory, MCP, skills & channel integrations
        </p>
      </div>
    </div>
    <div class="welcome-right">
      {#if status}
        <StatusBadge status={status.status} />
      {:else}
        <span class="badge badge-neutral">connecting…</span>
      {/if}
      <a href="/chat" class="btn btn-primary">
        <MessageSquare size={14} />
        Start chatting
        <ArrowRight size={14} />
      </a>
    </div>
  </div>

  <!-- ── Stats grid ─────────────────────────────────────────────── -->
  <div class="grid-4 mb-6">
    <div class="stat-card">
      <div class="stat-icon" style="background: rgba(129,140,248,0.12); color: var(--primary)">
        <Bot size={18} />
      </div>
      <div class="stat-body">
        <div class="stat-value">{agentList.length || (loading ? '…' : '0')}</div>
        <div class="stat-label">Agents</div>
      </div>
    </div>
    <div class="stat-card">
      <div class="stat-icon" style="background: rgba(167,139,250,0.12); color: var(--accent)">
        <Puzzle size={18} />
      </div>
      <div class="stat-body">
        <div class="stat-value">{skillList.length || (loading ? '…' : '0')}</div>
        <div class="stat-label">Skills</div>
      </div>
    </div>
    <div class="stat-card">
      <div class="stat-icon" style="background: rgba(96,165,250,0.12); color: var(--info)">
        <Wrench size={18} />
      </div>
      <div class="stat-body">
        <div class="stat-value">{toolList.length || (loading ? '…' : '0')}</div>
        <div class="stat-label">Tools</div>
      </div>
    </div>
    <div class="stat-card">
      <div class="stat-icon" style="background: rgba(52,211,153,0.12); color: var(--success)">
        <Activity size={18} />
      </div>
      <div class="stat-body">
        <div class="stat-value">{sessions.length || (loading ? '…' : '0')}</div>
        <div class="stat-label">Sessions</div>
      </div>
    </div>
  </div>

  <!-- ── Two-column content ─────────────────────────────────────── -->
  <div class="two-col">

    <!-- Recent sessions -->
    <div class="card">
      <div class="section-head">
        <Clock size={15} />
        <span>Recent Sessions</span>
        <a href="/memory" class="section-link">View all <ArrowRight size={12} /></a>
      </div>
      {#if loading}
        <div class="loading-rows">
          {#each [1,2,3] as _}
            <div class="skeleton-row"></div>
          {/each}
        </div>
      {:else if sessions.length === 0}
        <div class="empty-state">
          <Clock size={32} />
          <h3>No sessions yet</h3>
          <p>Start a chat to see session history</p>
        </div>
      {:else}
        <ul class="item-list">
          {#each sessions as session}
            <li class="item-row">
              <div class="item-icon" style="background: var(--surface-3)">
                <MessageSquare size={13} style="color: var(--text-3)" />
              </div>
              <div class="item-body">
                <span class="item-name">{session.id}</span>
                <span class="item-meta">{session.origin} · {timeAgo(session.lastActive)}</span>
              </div>
              <span class="badge badge-neutral" style="font-size:0.625rem">{session.status}</span>
            </li>
          {/each}
        </ul>
      {/if}
    </div>

    <!-- Recent memory -->
    <div class="card">
      <div class="section-head">
        <Database size={15} />
        <span>Recent Memory</span>
        <a href="/memory" class="section-link">View all <ArrowRight size={12} /></a>
      </div>
      {#if loading}
        <div class="loading-rows">
          {#each [1,2,3] as _}
            <div class="skeleton-row"></div>
          {/each}
        </div>
      {:else if recentMemory.length === 0}
        <div class="empty-state">
          <Database size={32} />
          <h3>No memories stored</h3>
          <p>The agent will remember important facts here</p>
        </div>
      {:else}
        <ul class="item-list">
          {#each recentMemory as entry}
            <li class="item-row">
              <div class="item-icon" style="background: {memTypeColor(entry.type)}22">
                <span style="width:6px;height:6px;border-radius:50%;background:{memTypeColor(entry.type)};display:inline-block"></span>
              </div>
              <div class="item-body">
                <span class="item-name">{entry.content.length > 60 ? entry.content.slice(0,60) + '…' : entry.content}</span>
                <span class="item-meta">{entry.type} · {timeAgo(entry.timestamp)}</span>
              </div>
              <span style="font-size:0.6875rem;color:var(--text-3)">{(entry.importance * 100).toFixed(0)}%</span>
            </li>
          {/each}
        </ul>
      {/if}
    </div>

  </div>

  <!-- ── Quick actions ─────────────────────────────────────────── -->
  <div class="quick-actions">
    <h3 class="section-title">Quick Actions</h3>
    <div class="grid-3">
      {#each [
        { href: '/chat',    label: 'Open Chat',      icon: MessageSquare, color: 'var(--primary)',  bg: 'var(--primary-dim)',                desc: 'Start a streaming conversation' },
        { href: '/agents',  label: 'Manage Agents',  icon: Bot,           color: 'var(--accent)',   bg: 'rgba(167,139,250,0.12)',           desc: 'View active agents & sub-agents' },
        { href: '/memory',  label: 'Browse Memory',  icon: Database,      color: 'var(--success)',  bg: 'rgba(52,211,153,0.12)',            desc: 'Explore stored facts & context' },
        { href: '/skills',  label: 'View Skills',    icon: Puzzle,        color: 'var(--info)',     bg: 'rgba(96,165,250,0.12)',            desc: 'Composio + local skill catalog' },
        { href: '/tools',   label: 'Inspect Tools',  icon: Wrench,        color: 'var(--warning)',  bg: 'rgba(251,191,36,0.12)',            desc: 'Registered tool registry' },
        { href: '/settings',label: 'Settings',       icon: Activity,      color: 'var(--text-2)',   bg: 'var(--surface-2)',                 desc: 'LLM providers, modules & config' },
      ] as action}
        <a href={action.href} class="action-card card card-hover">
          <div class="action-icon" style="background:{action.bg}; color:{action.color}">
            <svelte:component this={action.icon} size={18} />
          </div>
          <div>
            <div class="action-label">{action.label}</div>
            <div class="action-desc">{action.desc}</div>
          </div>
        </a>
      {/each}
    </div>
  </div>
</div>

<style>
  .mb-6 { margin-bottom: 1.5rem; }

  .welcome-banner {
    display: flex;
    align-items: center;
    justify-content: space-between;
    background: linear-gradient(135deg, rgba(129,140,248,0.06), rgba(167,139,250,0.04));
    border: 1px solid rgba(129,140,248,0.15);
    border-radius: var(--radius-lg);
    padding: 1.25rem 1.5rem;
    margin-bottom: 1.75rem;
    gap: 1rem;
  }

  .welcome-left {
    display: flex;
    align-items: center;
    gap: 1rem;
  }

  .welcome-icon {
    width: 44px;
    height: 44px;
    border-radius: 12px;
    background: linear-gradient(135deg, var(--primary), var(--accent));
    display: flex;
    align-items: center;
    justify-content: center;
    color: #fff;
    flex-shrink: 0;
    box-shadow: 0 4px 16px rgba(129,140,248,0.3);
  }

  .welcome-title {
    font-size: 1rem;
    font-weight: 600;
    margin: 0 0 0.2rem;
  }

  .welcome-sub {
    font-size: 0.775rem;
    color: var(--text-2);
    margin: 0;
  }

  .welcome-right {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex-shrink: 0;
  }

  .two-col {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 1rem;
    margin-bottom: 1.75rem;
  }

  @media (max-width: 860px) {
    .two-col { grid-template-columns: 1fr; }
    .welcome-banner { flex-direction: column; align-items: flex-start; }
  }

  .section-head {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    color: var(--text-2);
    font-size: 0.8125rem;
    font-weight: 600;
    margin-bottom: 0.875rem;
  }

  .section-link {
    display: flex;
    align-items: center;
    gap: 0.2rem;
    margin-left: auto;
    font-size: 0.75rem;
    color: var(--primary);
    text-decoration: none;
  }
  .section-link:hover { text-decoration: underline; }

  .item-list {
    list-style: none;
    padding: 0;
    margin: 0;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .item-row {
    display: flex;
    align-items: center;
    gap: 0.625rem;
    padding: 0.5rem 0.375rem;
    border-radius: var(--radius-sm);
    transition: background 0.1s;
  }
  .item-row:hover { background: var(--surface-2); }

  .item-icon {
    width: 28px;
    height: 28px;
    border-radius: 6px;
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .item-body {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }

  .item-name {
    font-size: 0.8125rem;
    color: var(--text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .item-meta {
    font-size: 0.6875rem;
    color: var(--text-3);
  }

  .loading-rows {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    padding: 0.25rem 0;
  }

  .skeleton-row {
    height: 36px;
    border-radius: var(--radius-sm);
    background: linear-gradient(90deg, var(--surface-2) 25%, var(--surface-3) 50%, var(--surface-2) 75%);
    background-size: 200% 100%;
    animation: shimmer 1.5s infinite;
  }

  @keyframes shimmer {
    0%   { background-position: 200% 0; }
    100% { background-position: -200% 0; }
  }

  .section-title {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--text-2);
    margin: 0 0 0.875rem;
  }

  .quick-actions { margin-top: 0; }

  .action-card {
    display: flex;
    align-items: center;
    gap: 0.875rem;
    text-decoration: none;
    color: var(--text);
  }

  .action-icon {
    width: 38px;
    height: 38px;
    border-radius: var(--radius-sm);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .action-label {
    font-size: 0.8125rem;
    font-weight: 600;
    margin-bottom: 0.125rem;
  }

  .action-desc {
    font-size: 0.75rem;
    color: var(--text-2);
  }
</style>
