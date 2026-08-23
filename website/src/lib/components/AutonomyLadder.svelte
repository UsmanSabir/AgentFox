<script>
  import { autonomyModes } from '$lib/content.js';
</script>

<section class="section autonomy-section" id="trading">
  <span id="premium" class="anchor-offset" aria-hidden="true"></span>
  <div class="shell">
    <div class="section-heading split-heading"><div><p class="eyebrow">Premium trading · Automation you earn</p><h2>Start safely. Add autonomy deliberately.</h2></div><p>Premium cannot bypass the core kill switch, execution policy, risk engine, market window, idempotency, reconciliation, or audit ledger. Current strategy thresholds are conservative priors—not backtested findings.</p></div>
    <div class="ladder" role="list" aria-label="Trading automation modes">
      {#each autonomyModes as mode, index}
        <div class="rung" role="listitem"><span>{index + 1}</span><strong>{mode.name}</strong><p>{mode.description}</p></div>
      {/each}
    </div>
  </div>
</section>
