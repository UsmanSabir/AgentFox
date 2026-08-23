<script>
  import { ClipboardCheck, Hand, House, ShieldCheck } from '@lucide/svelte';
  import { trustPoints } from '$lib/content.js';
  const icons = [House, ShieldCheck, Hand, ClipboardCheck];
</script>

<section class="trust-strip" aria-label="Why AgentFox earns trust">
  <div class="shell trust-grid">
    {#each trustPoints as point, index}
      <div class="trust-point"><svelte:component this={icons[index]} size={18} aria-hidden="true" /><div><strong>{point.title}</strong><span>{point.description}</span></div></div>
    {/each}
  </div>
</section>
