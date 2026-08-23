<script>
  import { onMount } from 'svelte';
  import '../app.css';
  import SiteHeader from '$lib/components/SiteHeader.svelte';
  import { nextTheme, resolveInitialTheme, THEME_STORAGE_KEY } from '$lib/theme.js';

  let { children } = $props();
  let theme = $state(/** @type {'dark' | 'light'} */ ('dark'));

  onMount(() => {
    let saved = null;
    try {
      saved = localStorage.getItem(THEME_STORAGE_KEY);
    } catch {
      // The current-page theme still works when storage is unavailable.
    }
    theme = resolveInitialTheme(saved);
    document.documentElement.dataset.theme = theme;
  });

  function toggleTheme() {
    theme = nextTheme(theme);
    document.documentElement.dataset.theme = theme;
    try {
      localStorage.setItem(THEME_STORAGE_KEY, theme);
    } catch {
      // Persistence is optional; the visible state has already changed.
    }
  }
</script>

<SiteHeader {theme} onToggleTheme={toggleTheme} />
{@render children()}