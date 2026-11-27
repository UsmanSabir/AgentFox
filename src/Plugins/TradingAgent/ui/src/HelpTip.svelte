<script lang="ts">
  
  interface Props {
    /**
   * The "?" beside a control or section (CLAUDE.md §0.3 in the premium repo: help lives behind a "?",
   * never as inline text between controls). Opens on hover (after 400 ms), keyboard focus and click/tap.
   */
    label: string;
    align?: 'start' | 'end';
    children?: import('svelte').Snippet;
  }

  let { label, align = 'start', children }: Props = $props();
  let open = $state(false);
  const id = `help-${Math.random().toString(36).slice(2, 9)}`;
</script>

<span class="help" class:end={align === 'end'}>
  <button type="button" aria-label={label} aria-controls={id} aria-expanded={open}
          onclick={() => open = !open} onblur={() => open = false}>?</button>
  <span {id} role="tooltip" class="popup" class:open>{@render children?.()}</span>
</span>

<style>
  .help { position:relative; display:inline-flex; vertical-align:middle; }
  button { width:1.05rem; height:1.05rem; border-radius:50%; border:1px solid var(--border); background:var(--surface-2, transparent);
    color:var(--text-2); font-size:.66rem; font-weight:700; line-height:1; padding:0; cursor:help; }
  button:hover, button:focus-visible { color:var(--text); border-color:var(--primary); }
  button:focus-visible { outline:2px solid var(--primary); outline-offset:1px; }
  .popup { visibility:hidden; opacity:0; position:absolute; z-index:30; top:calc(100% + .35rem); left:0; width:min(18rem, 70vw);
    padding:.55rem .65rem; border:1px solid var(--border); border-radius:6px; background:var(--surface, #111);
    color:var(--text-2); font-size:.72rem; font-weight:400; line-height:1.5; box-shadow:0 6px 18px rgb(0 0 0 / .25);
    white-space:normal; text-align:left; }
  .end .popup { left:auto; right:0; }
  /* Hover waits 400 ms (as the premium HelpBubble does) so a pointer passing over a "?" does not pop help open;
     keyboard focus and click open at once, and leaving hides at once. */
  .help:hover .popup { visibility:visible; opacity:1; transition:visibility 0s .4s, opacity .1s .4s; }
  .help:focus-within .popup, .popup.open { visibility:visible; opacity:1; transition:none; }
</style>
