<script>
  import { GitFork, Menu, X } from '@lucide/svelte';
  import ThemeToggle from './ThemeToggle.svelte';
  import { siteLinks } from '$lib/content.js';

  let { theme, onToggleTheme } = $props();
  let menuOpen = $state(false);
  const navItems = [
    ['Product', '#product'],
    ['Trading', '#trading'],
    ['Premium', '#premium'],
    ['For you', '#audiences'],
    ['Deployment', '#deployment'],
    ['Developers', '#developers']
  ];
</script>

<header class="site-header">
  <div class="shell nav-shell">
    <a class="brand" href="#top" aria-label="AgentFox home">
      <span class="brand-mark" aria-hidden="true"><span></span></span>
      <span>AgentFox</span>
    </a>

    <nav class:open={menuOpen} aria-label="Primary navigation">
      {#each navItems as item}
        <a href={item[1]} onclick={() => (menuOpen = false)}>{item[0]}</a>
      {/each}
      <a class="nav-github" href={siteLinks.repository} target="_blank" rel="noreferrer"><GitFork size={16} aria-hidden="true" /> GitHub</a>
    </nav>

    <div class="nav-actions">
      <ThemeToggle {theme} onToggle={onToggleTheme} />
      <button class="menu-toggle" type="button" aria-label={menuOpen ? 'Close navigation' : 'Open navigation'} aria-expanded={menuOpen} onclick={() => (menuOpen = !menuOpen)}>
        {#if menuOpen}<X size={19} aria-hidden="true" />{:else}<Menu size={19} aria-hidden="true" />{/if}
      </button>
    </div>
  </div>
</header>
