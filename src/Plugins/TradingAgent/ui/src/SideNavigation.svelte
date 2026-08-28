<script lang="ts">
  import { onMount } from 'svelte';
  import { Navigation } from 'lucide-svelte';
  import type { SectionNavigationItem } from './sectionNavigation';

  export let items: SectionNavigationItem[] = [];
  export let label = 'Page sections';

  let activeId = items[0]?.id ?? '';

  function activateVisibleSection(elements: HTMLElement[]) {
    const marker = Math.min(180, window.innerHeight * 0.28);
    const visible = elements.filter(element => element.getBoundingClientRect().top <= marker);
    activeId = (visible.at(-1) ?? elements[0])?.id ?? '';
  }

  function navigate(event: MouseEvent, item: SectionNavigationItem) {
    event.preventDefault();
    const target = document.getElementById(item.id);
    if (!target) return;

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    target.scrollIntoView({ behavior: reducedMotion ? 'auto' : 'smooth', block: 'start' });
    activeId = item.id;
    history.replaceState(null, '', `#${item.id}`);
  }

  onMount(() => {
    const elements = items
      .map(item => document.getElementById(item.id))
      .filter((element): element is HTMLElement => element != null);
    if (!elements.length) return;

    const sync = () => activateVisibleSection(elements);
    sync();
    window.addEventListener('scroll', sync, { passive: true });
    window.addEventListener('resize', sync);

    return () => {
      window.removeEventListener('scroll', sync);
      window.removeEventListener('resize', sync);
    };
  });
</script>

<nav aria-label={label}>
  <div class="navigation-title" aria-hidden="true">
    <Navigation size={16} strokeWidth={1.9} />
    <span>Navigate</span>
  </div>

  <div class="navigation-items">
    {#each items as item}
      <a
        href={`#${item.id}`}
        class:active={activeId === item.id}
        aria-current={activeId === item.id ? 'location' : undefined}
        title={item.label}
        on:click={(event) => navigate(event, item)}
      >
        <span class="icon" aria-hidden="true">
          <svelte:component this={item.icon} size={17} strokeWidth={1.85} />
        </span>
        <span class="label">{item.label}</span>
      </a>
    {/each}
  </div>
</nav>

<style>
  nav {
    position: sticky;
    top: .75rem;
    z-index: 30;
    width: 3.45rem;
    max-height: calc(100vh - 1.5rem);
    padding: .42rem;
    overflow: hidden;
    border: 1px solid var(--border-md);
    border-radius: 14px;
    background: color-mix(in srgb, var(--surface) 88%, transparent);
    box-shadow: 0 16px 38px rgba(0, 0, 0, .18), inset 0 1px 0 color-mix(in srgb, white 8%, transparent);
    backdrop-filter: blur(14px) saturate(130%);
    transition: width .24s cubic-bezier(.2, .8, .2, 1), border-color .2s ease, box-shadow .2s ease;
  }
  nav:hover, nav:focus-within {
    width: 11.75rem;
    border-color: color-mix(in srgb, var(--primary) 38%, var(--border-md));
    box-shadow: 0 18px 44px rgba(0, 0, 0, .24), 0 0 28px color-mix(in srgb, var(--primary) 10%, transparent);
  }
  .navigation-title, a { display:flex; align-items:center; min-width:10.75rem; }
  .navigation-title {
    height: 2.45rem;
    gap: .72rem;
    padding: 0 .47rem;
    color: var(--primary);
  }
  .navigation-title span {
    opacity:0;
    transform:translateX(-.35rem);
    color: var(--text-3);
    font-size: .61rem;
    font-weight: 750;
    letter-spacing: .11em;
    text-transform: uppercase;
    transition:opacity .16s ease .03s, transform .2s cubic-bezier(.2, .8, .2, 1);
  }
  .navigation-items { display:flex; flex-direction:column; gap:.22rem; }
  a {
    position: relative;
    height: 2.55rem;
    gap: .72rem;
    padding: 0 .47rem;
    overflow: hidden;
    color: var(--text-3);
    border-radius: 9px;
    text-decoration: none;
    cursor: pointer;
    transition: color .18s ease, background .18s ease;
  }
  a::before {
    content: '';
    position: absolute;
    inset: 50% auto auto 0;
    width: 2px;
    height: 0;
    border-radius: 999px;
    background: linear-gradient(var(--primary), var(--accent));
    box-shadow: 0 0 12px var(--primary);
    transform: translateY(-50%);
    transition: height .22s cubic-bezier(.2, .8, .2, 1);
  }
  a:hover { color:var(--text); background:var(--surface-2); }
  a:focus-visible { outline:2px solid var(--primary); outline-offset:-2px; }
  a.active {
    color:var(--primary);
    background:linear-gradient(90deg, color-mix(in srgb, var(--primary) 15%, transparent), transparent);
  }
  a.active::before { height:1.35rem; }
  .icon { width:1.55rem; display:grid; place-items:center; flex:none; }
  .label {
    opacity: 0;
    transform: translateX(-.35rem);
    color: inherit;
    font-size: .72rem;
    font-weight: 650;
    white-space: nowrap;
    transition: opacity .16s ease .03s, transform .2s cubic-bezier(.2, .8, .2, 1);
  }
  nav:hover .label, nav:focus-within .label,
  nav:hover .navigation-title span, nav:focus-within .navigation-title span { opacity:1; transform:translateX(0); }

  @media (max-width: 980px) {
    nav, nav:hover, nav:focus-within {
      position: static;
      width: auto;
      max-width: 100%;
      padding: .3rem;
      border-radius: 12px;
    }
    .navigation-title { display:none; }
    .navigation-items { flex-direction:row; overflow-x:auto; scrollbar-width:none; }
    .navigation-items::-webkit-scrollbar { display:none; }
    a { min-width:2.5rem; width:2.5rem; height:2.4rem; padding:0 .46rem; gap:0; flex:none; }
    .label { position:absolute; width:1px; height:1px; overflow:hidden; clip-path:inset(50%); }
    a::before { inset:auto auto 0 50%; width:0; height:2px; transform:translateX(-50%); transition:width .2s ease; }
    a.active::before { width:1.25rem; height:2px; }
  }

  @media (prefers-reduced-motion: reduce) {
    nav, a, a::before, .label, .navigation-title span { transition:none; }
  }
</style>
