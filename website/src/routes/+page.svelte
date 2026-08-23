<script>
  import SiteHeader from '$lib/components/SiteHeader.svelte';
  import Hero from '$lib/components/Hero.svelte';
  import TrustStrip from '$lib/components/TrustStrip.svelte';
  import DecisionSteps from '$lib/components/DecisionSteps.svelte';
  import AutonomyLadder from '$lib/components/AutonomyLadder.svelte';
  import AudiencePaths from '$lib/components/AudiencePaths.svelte';
  import DeploymentOptions from '$lib/components/DeploymentOptions.svelte';
  import PlatformCapabilities from '$lib/components/PlatformCapabilities.svelte';
  import SiteFooter from '$lib/components/SiteFooter.svelte';
  import { base } from '$app/paths';

  const siteUrl = 'https://usmansabir.github.io/AgentFox/';
</script>

<svelte:head>
  <title>AgentFox — AI-assisted trading decisions with rules you control</title>
  <meta name="description" content="AgentFox is a local-first AI agent for explainable market research, trade planning, alerts, and guarded automation." />
  <link rel="canonical" href={siteUrl} />
  <meta property="og:type" content="website" />
  <meta property="og:title" content="AgentFox — Trade with a plan, not a pulse." />
  <meta property="og:description" content="AI-assisted market research and trade planning, with deterministic rules around every action." />
  <meta property="og:url" content={siteUrl} />
  <meta name="twitter:card" content="summary" />
  <meta name="twitter:title" content="AgentFox — Trade with a plan, not a pulse." />
  <meta name="twitter:description" content="A local-first AI agent for explainable trading decisions and guarded automation." />
  <meta name="theme-color" content="#07100d" />
  <link rel="manifest" href={`${base}/site.webmanifest`} />
  <link rel="icon" href={`${base}/favicon.svg`} />
</svelte:head>

<a class="skip-link" href="#main-content">Skip to main content</a>

<main id="main-content">
  <Hero />
  <TrustStrip />
  <DecisionSteps />
  <AutonomyLadder />
  <AudiencePaths />
  <DeploymentOptions />
  <PlatformCapabilities />
</main>

<SiteFooter />
