<script>
  import { Moon, Sun } from '@lucide/svelte';
  let { theme, onToggle } = $props();
</script>

<button class="theme-toggle" type="button" aria-label={`Switch to ${theme === 'dark' ? 'light' : 'dark'} theme`} aria-pressed={theme === 'light'} onclick={onToggle}>
  {#if theme === 'dark'}
    <Sun size={17} aria-hidden="true" />
  {:else}
    <Moon size={17} aria-hidden="true" />
  {/if}
</button>
