export const siteLinks = {
  repository: 'https://github.com/UsmanSabir/AgentFox',
  install: 'https://github.com/UsmanSabir/AgentFox#installation',
  releases: 'https://github.com/UsmanSabir/AgentFox/releases',
  tradingDocs: 'https://github.com/UsmanSabir/AgentFox/blob/main/src/Plugins/TradingAgent/README.md',
  developerGuide: 'https://github.com/UsmanSabir/AgentFox/blob/main/docs/DEVELOPMENT.md',
  license: 'https://github.com/UsmanSabir/AgentFox/blob/main/LICENSE',
  earlyAccess:
    'mailto:tradingsmartnow@outlook.com?subject=AgentFox%20Managed%20Cloud%20Early%20Access'
};

export const hero = {
  eyebrow: 'Decision support before execution',
  title: 'Trade with a plan, not a pulse.',
  description:
    'AgentFox helps you understand when to enter, when to exit, and when the safest move is no trade at all—then keeps every automated action inside rules you control.',
  aiMessage:
    'You already use AI to write, build, research, and automate. Why should trading be the exception?',
  primaryCta: { label: 'Start self-hosted', href: siteLinks.install },
  secondaryCta: { label: 'Request cloud access', href: siteLinks.earlyAccess }
};

export const riskDisclaimer =
  'Trading involves substantial risk. AgentFox is software for research and decision support, not investment advice, and offers no guaranteed returns.';

export const trustPoints = [
  { title: 'Local-first', description: 'Your keys and data stay under your control.' },
  { title: 'Deterministic gates', description: 'Numbers come from reproducible analysis.' },
  { title: 'Human control', description: 'Paper, shadow, and approval modes come first.' },
  { title: 'Auditable actions', description: 'Decisions, refusals, and outcomes are recorded.' }
];

export const decisionSteps = [
  { title: 'Watch the market', description: 'Monitor symbols, levels, volume, alerts, and changing setups.' },
  { title: 'Build the plan', description: 'Define entry, stop, target, sizing, and invalidation before exposure.' },
  { title: 'Check every gate', description: 'Apply market, liquidity, portfolio, cost, and risk constraints.' },
  { title: 'Choose the control level', description: 'Observe, receive alerts, approve proposals, or allow bounded automation.' }
];

export const autonomyModes = [
  { name: 'Paper', description: 'Journal the decision only.' },
  { name: 'Shadow', description: 'Build the exact order, unsent.' },
  { name: 'Alert only', description: 'Send entry and exit alerts.' },
  { name: 'Approval', description: 'A human authorizes the proposal.' },
  { name: 'Bounded auto', description: 'Conditional orders within policy.' },
  { name: 'Off', description: 'Stop strategy passes entirely.' }
];

export const audiences = [
  { id: 'new-traders', label: 'New and struggling traders', title: 'Replace reactive trades with a visible plan.', description: 'Learn what makes an entry valid, what makes it unsafe, and how exits and risk should be defined first.', featured: true },
  { id: 'active-traders', label: 'Active traders', title: 'Monitor more without losing control.', description: 'Use alerts, proposals, conditional orders, watchlists, and position-management rules.', featured: false },
  { id: 'developers', label: 'Developers', title: 'Extend a local-first agent.', description: 'Build plugins, tools, skills, and MCP integrations in the existing C# and Svelte stack.', featured: false },
  { id: 'teams', label: 'Teams and funds', title: 'Make policy observable.', description: 'Separate analysis from execution with approvals, audit history, and deterministic limits.', featured: false }
];

export const deploymentOptions = [
  { id: 'self-hosted', status: 'Available now', title: 'Self-hosted', description: 'Run AgentFox on hardware and accounts you control.', features: ['Open-source AgentFox core', 'Your API keys and credentials', 'Windows, macOS, and Linux', 'Community trading plugin included'], cta: { label: 'View install options', href: siteLinks.install }, featured: true },
  { id: 'managed-cloud', status: 'Early access', title: 'Managed cloud', description: 'Request a managed workspace for guided onboarding, infrastructure, and updates.', features: ['Guided onboarding', 'Managed infrastructure', 'Workspace isolation', 'Premium trading eligibility'], cta: { label: 'Request early access', href: siteLinks.earlyAccess }, featured: false }
];

export const capabilities = [
  { title: 'Channels', description: 'WhatsApp, Telegram, Discord, Slack, and Microsoft Teams' },
  { title: 'Memory', description: 'Short-term, long-term, and hybrid memory' },
  { title: 'Tools and skills', description: 'Files, shell, web, scheduling, and extension points' },
  { title: 'Trading', description: 'Research, analysis, alerts, proposals, and guarded execution' }
];
