<script>
  import { audiences } from '$lib/content.js';
</script>

<section class="section" id="audiences">
  <div class="shell">
    <div class="section-heading"><p class="eyebrow">Built for different starting points</p><h2>One platform. A clearer next step for you.</h2></div>
    <div class="audience-grid">
      {#each audiences as audience}
        <article class:featured={audience.featured}>
          <p class="card-label">{audience.label}</p><h3>{audience.title}</h3><p>{audience.description}</p>
        </article>
      {/each}
    </div>
  </div>
</section>
