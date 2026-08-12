<script lang="ts">
  import type { StockAssessment } from './api';
  import { Brain, ShieldCheck, ShieldAlert, ShieldX, HelpCircle } from 'lucide-svelte';

  export let assessment: StockAssessment;
  /** Compact form for the alert list; the chart pane uses the full one. */
  export let compact = false;

  // The recommendation drives the colour, not the confidence: a HIGH-confidence AVOID is a warning,
  // and colouring it green because the model was sure would be exactly backwards.
  $: tone =
    assessment.recommendation === 'PROCEED' ? 'good'
    : assessment.recommendation === 'CAUTION' ? 'warn'
    : assessment.recommendation === 'AVOID' ? 'bad'
    : 'unknown';

  $: icon =
    tone === 'good' ? ShieldCheck
    : tone === 'warn' ? ShieldAlert
    : tone === 'bad' ? ShieldX
    : HelpCircle;

  const when = (iso: string) => new Date(iso).toLocaleString();
</script>

<div class="assessment {tone}" class:compact>
  <div class="verdict">
    <svelte:component this={icon} size={14} />
    <b>{assessment.recommendation.replace('_', ' ')}</b>
    <span class="conf">{assessment.confidence} · {assessment.confidenceScore}/100</span>
    {#if assessment.invalidationLevel != null}
      <span class="invalidation" title="The price at which this view is wrong — taken from the levels in the evidence, not invented">
        invalid below/above {assessment.invalidationLevel}
      </span>
    {/if}
  </div>

  <p class="rationale">{assessment.rationale}</p>

  {#if !compact}
    {#if assessment.supportingFactors.length}
      <div class="factors">
        <b>Supporting</b>
        <ul>{#each assessment.supportingFactors as factor}<li>{factor}</li>{/each}</ul>
      </div>
    {/if}
    {#if assessment.riskFactors.length}
      <div class="factors risks">
        <b>Risks</b>
        <ul>{#each assessment.riskFactors as risk}<li>{risk}</li>{/each}</ul>
      </div>
    {/if}
  {/if}

  <!-- Provenance, so a verdict in the audit trail can be traced to a model and a moment. -->
  <div class="meta">
    {assessment.model ?? 'model unknown'} · {when(assessment.assessedUtc)}
    {#if assessment.fromCache} · cached this session{/if}
  </div>
</div>

<style>
  .assessment {
    border: 1px solid var(--border-md);
    border-left-width: 3px;
    border-radius: var(--radius-sm);
    padding: .6rem .7rem;
    display: flex;
    flex-direction: column;
    gap: .4rem;
    background: var(--surface-2);
  }
  .assessment.good    { border-left-color: var(--success); }
  .assessment.warn    { border-left-color: var(--warning); }
  .assessment.bad     { border-left-color: var(--danger); }
  .assessment.unknown { border-left-color: var(--text-3); }
  .assessment.compact { padding: .45rem .55rem; gap: .25rem; }

  .verdict { display:flex; align-items:center; gap:.4rem; flex-wrap:wrap; font-size:.75rem; }
  .assessment.good .verdict    { color: var(--success); }
  .assessment.warn .verdict    { color: var(--warning); }
  .assessment.bad .verdict     { color: var(--danger); }
  .assessment.unknown .verdict { color: var(--text-3); }
  .verdict b { letter-spacing: .02em; }
  .conf { color: var(--text-2); font-size: .7rem; }
  .invalidation {
    color: var(--text-3); font-size: .65rem; border: 1px solid var(--border-md);
    border-radius: 999px; padding: .05rem .4rem;
  }

  .rationale { margin:0; color:var(--text-2); font-size:.73rem; line-height:1.55; }

  .factors { display:flex; flex-direction:column; gap:.15rem; }
  .factors b { color:var(--text-3); font-size:.63rem; text-transform:uppercase; letter-spacing:.04em; }
  .factors ul { margin:0; padding-left:1rem; color:var(--text-2); font-size:.7rem; line-height:1.5; }
  .factors.risks ul { color:var(--warning); }

  .meta { color:var(--text-3); font-size:.63rem; }
</style>
