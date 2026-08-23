<script>
  import { ArrowUpRight, Cloud, Download } from '@lucide/svelte';
  import DecisionPreview from './DecisionPreview.svelte';
  import { hero, riskDisclaimer } from '$lib/content.js';
</script>

<section class="hero" id="top">
  <div class="shell hero-grid">
    <div class="hero-copy">
      <p class="eyebrow">{hero.eyebrow}</p>
      <h1>{hero.title}</h1>
      <p class="lead">{hero.description}</p>
      <p class="ai-line">{hero.aiMessage}</p>
      <div class="cta-row">
        <a class="button primary" href={hero.primaryCta.href}><Download size={17} aria-hidden="true" />{hero.primaryCta.label}<ArrowUpRight size={15} aria-hidden="true" /></a>
        <a class="button secondary" href={hero.secondaryCta.href}><Cloud size={17} aria-hidden="true" />{hero.secondaryCta.label}</a>
      </div>
      <p class="risk-note">{riskDisclaimer}</p>
    </div>
    <DecisionPreview />
  </div>
</section>
