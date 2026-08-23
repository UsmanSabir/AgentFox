<script>
  import { Check, Cloud, Server } from '@lucide/svelte';
  import { deploymentOptions } from '$lib/content.js';
  const icons = [Server, Cloud];
</script>

<section class="section deployment-section" id="deployment">
  <div class="shell">
    <div class="section-heading"><p class="eyebrow">Choose where AgentFox runs</p><h2>Your machine now. Managed cloud when you’re ready.</h2></div>
    <div class="deployment-grid">
      {#each deploymentOptions as option, index}
        <article class:featured={option.featured}>
          <div class="deployment-top"><svelte:component this={icons[index]} size={22} aria-hidden="true" /><span>{option.status}{option.featured ? ' · Recommended' : ''}</span></div>
          <h3>{option.title}</h3><p>{option.description}</p>
          <ul>{#each option.features as feature}<li><Check size={15} aria-hidden="true" />{feature}</li>{/each}</ul>
          <a class:primary={option.featured} class="button secondary block" href={option.cta.href}>{option.cta.label}</a>
        </article>
      {/each}
    </div>
    <p class="email-fallback">No email app configured? Write to <a href="mailto:tradingsmartnow@outlook.com">tradingsmartnow@outlook.com</a>.</p>
  </div>
</section>
