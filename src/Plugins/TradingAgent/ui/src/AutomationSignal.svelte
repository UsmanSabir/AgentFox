<script lang="ts">
  /** Include the mode name where the surrounding view does not already provide it. */
  export let showLabel = false;
</script>

<span
  class="automation-signal"
  class:with-label={showLabel}
  role="img"
  aria-label="Auto trading enabled"
  title="Auto trading enabled — strategies may act subject to policy and risk controls"
>
  <span class="radar" aria-hidden="true"></span>
  {#if showLabel}<span>Auto</span>{/if}
</span>

<style>
  .automation-signal {
    --signal-color:var(--success);
    display:inline-flex; align-items:center; justify-content:center;
    flex:0 0 auto; width:12px; height:12px;
    color:var(--signal-color);
  }
  .automation-signal.with-label { width:auto; gap:.25rem; }
  .radar {
    position:relative; display:block; width:5px; height:5px;
    border-radius:50%; background:currentColor;
    box-shadow:0 0 0 2px color-mix(in srgb, currentColor 13%, transparent);
  }
  .radar::before,
  .radar::after {
    content:''; position:absolute; inset:-1px; border:1px solid currentColor;
    border-radius:50%; opacity:0;
    animation:automation-radar 2.4s ease-out infinite;
  }
  .radar::after { animation-delay:1.2s; }
  @keyframes automation-radar {
    0% { opacity:.62; transform:scale(.75); }
    68%,100% { opacity:0; transform:scale(2.35); }
  }
  @media (prefers-reduced-motion: reduce) {
    .radar::before,
    .radar::after { animation:none; opacity:.3; transform:scale(1.65); }
    .radar::after { display:none; }
  }
</style>
