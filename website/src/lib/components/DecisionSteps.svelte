<script>
  import { decisionSteps } from '$lib/content.js';
</script>

<section class="section" id="product">
  <div class="shell">
    <div class="section-heading"><p class="eyebrow">From uncertainty to a controlled decision</p><h2>Know why before you act.</h2><p>AgentFox turns market data into a visible decision path. Failed checks are shown—not hidden—so “do nothing” remains a valid, understandable outcome.</p></div>
    <ol class="steps-grid">
      {#each decisionSteps as step, index}
        <li><span class="step-number">0{index + 1}</span><h3>{step.title}</h3><p>{step.description}</p></li>
      {/each}
    </ol>
  </div>
</section>
