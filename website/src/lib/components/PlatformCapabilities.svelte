<script>
  import { Brain, Cable, MessageCircleMore, TrendingUp } from '@lucide/svelte';
  import { capabilities } from '$lib/content.js';
  const icons = [MessageCircleMore, Brain, Cable, TrendingUp];
</script>

<section class="section" id="developers">
  <div class="shell capability-layout">
    <div class="section-heading"><p class="eyebrow">More than a trading bot</p><h2>Your own AI agent, with trading as a guarded capability.</h2><p>AgentFox works through channels you already use and grows through modular tools instead of a closed platform.</p></div>
    <div class="capability-grid">
      {#each capabilities as capability, index}<article><svelte:component this={icons[index]} size={19} aria-hidden="true" /><strong>{capability.title}</strong><p>{capability.description}</p></article>{/each}
    </div>
  </div>
</section>
