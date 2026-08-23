<script>
  import { ArrowUpRight, GitFork, Star } from '@lucide/svelte';
  import { hero, riskDisclaimer, siteLinks } from '$lib/content.js';
</script>

<footer class="site-footer">
  <div class="shell footer-cta">
    <p class="eyebrow">Control first</p><h2>Give every trade a reason—and every automated action a boundary.</h2>
    <div class="cta-row"><a class="button primary" href={hero.primaryCta.href}>{hero.primaryCta.label}<ArrowUpRight size={15} aria-hidden="true" /></a><a class="button secondary" href={hero.secondaryCta.href}>Request early access</a></div>
  </div>
  <div class="shell footer-bottom">
    <div><a class="brand" href="#top"><span class="brand-mark" aria-hidden="true"><span></span></span><span>AgentFox</span></a><p>{riskDisclaimer}</p></div>
    <nav aria-label="Footer navigation"><a href={siteLinks.repository}><GitFork size={15} aria-hidden="true" />GitHub</a><a href={siteLinks.repository}><Star size={15} aria-hidden="true" />Star the repo</a><a href={siteLinks.tradingDocs}>Trading docs</a><a href={siteLinks.developerGuide}>Developer guide</a><a href={siteLinks.license}>MIT license</a></nav>
  </div>
</footer>
