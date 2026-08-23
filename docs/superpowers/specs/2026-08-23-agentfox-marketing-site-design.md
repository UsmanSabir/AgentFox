# AgentFox Marketing Website Design

**Date:** 2026-08-23
**Status:** Approved for implementation planning

## 1. Purpose

Build a public marketing website for AgentFox that explains the core AI-agent platform, the community TradingAgent plugin, and the premium trading edition. The site must make two deployment paths clear:

1. **Self-hosted**, available now.
2. **Managed cloud**, offered through an early-access request.

The primary audience is an individual trader who is new, losing money, or unsure when to enter and exit. Secondary audiences are active traders, developers, and trading teams or funds. The site must address each audience without diluting the beginner-first homepage narrative.

The website markets software and analysis. It must never promise profits, imply validated trading performance, or present AI output as investment advice.

## 2. Product Positioning

### Primary message

> Trade with a plan, not a pulse.

### AI message

> You already use AI to write, build, research, and automate. Why should trading be the exception? AgentFox brings AI to market research and trade planning—while deterministic rules keep every action inside boundaries you control.

### Positioning statement

AgentFox is a local-first AI-agent platform for people who want useful automation without surrendering control. Its trading capabilities combine AI-assisted research and explanation with deterministic market analysis, risk gates, execution policy, idempotency, reconciliation, and audit history.

### Voice

The brand is:

- Confident, not promotional or arrogant.
- Empathetic to struggling traders, not fear-driven.
- Technically credible, not jargon-heavy.
- Innovative, not gimmicky.
- Honest about risk, uncertainty, and current limitations.

Use plain language on the homepage. Put implementation detail in developer and product sections. Prefer “decision support,” “guarded execution,” “controlled automation,” and “explainable checks” over “AI predicts the market” or “autonomous profits.”

## 3. Architecture

Create a standalone SvelteKit project under the public AgentFox repository, separate from `src/frontend` and the trading-plugin UIs. The proposed location is:

```text
AgentFox/
  website/
```

Use Svelte 5, SvelteKit, TypeScript, Tailwind CSS, Lucide Svelte, and `@sveltejs/adapter-static`. Prerender every route. The production result must be ordinary static HTML, CSS, JavaScript, images, and metadata with no required Node.js server.

This boundary is intentional:

- The existing `src/frontend` remains the local AgentFox management UI.
- The community trading UI remains isolated in its plugin project.
- Premium source remains private and is not imported, copied, or compiled into the public website.
- Public marketing copy may describe premium capabilities that are already documented, but must not expose proprietary thresholds, features, scores, or implementation details beyond existing public claims.

## 4. Hosting and Base Paths

GitHub Pages is the reference deployment. A GitHub Actions workflow must build and deploy the static output from `website/`.

The same static build must work on Cloudflare Pages with:

- Build command: the website production build script.
- Output directory: the adapter-static output directory.
- No Cloudflare-only runtime functions.

The site must support both a GitHub project subpath and a custom-domain root. Asset and internal link generation must use SvelteKit path helpers or configuration instead of hard-coded root-relative URLs.

## 5. Information Architecture

The first release is one prerendered homepage at `/`, with anchored navigation to its major sections. Components and content must be structured so these sections can become separate prerendered routes later without redesign:

- Product
- Trading
- Premium
- Deployment
- Developers

Primary navigation:

1. AgentFox brand/home.
2. Product.
3. Trading.
4. Premium.
5. Deployment.
6. Developers.
7. GitHub.
8. Theme control.

On small screens, use a labelled, keyboard-accessible navigation disclosure. Do not hide any primary destination behind hover-only interaction.

## 6. Homepage Flow

### 6.1 Hero

Eyebrow: **Decision support before execution**

Headline: **Trade with a plan, not a pulse.**

Supporting content explains that AgentFox helps users understand entries, exits, and valid no-trade decisions, while automation remains within user-controlled policy.

Primary CTA: **Start self-hosted**
Destination: `https://github.com/UsmanSabir/AgentFox#installation`

Secondary CTA: **Request cloud access**
Destination:

```text
mailto:tradingsmartnow@outlook.com?subject=AgentFox%20Managed%20Cloud%20Early%20Access
```

Include a concise trading-risk disclaimer in or immediately below the hero. The disclaimer must be visible without requiring a modal or tooltip.

The hero product visual is a code-native illustrative decision card, not a fake screenshot. It shows a symbol, a strategy label, a small chart, passed checks, `Shadow` mode, and a `WAIT` verdict with “No order created.” The example must be clearly illustrative and must not imply a recorded return.

### 6.2 Trust strip

Four proof themes:

1. Local-first.
2. Deterministic gates.
3. Human control.
4. Auditable actions.

### 6.3 Decision flow

Explain the product in four steps:

1. Watch the market.
2. Build the plan.
3. Check every gate.
4. Choose the control level.

The section must state that failed checks are shown and that doing nothing is a valid outcome.

### 6.4 Premium autonomy ladder

Present the supported control modes in a readable sequence:

- Paper.
- Shadow.
- Alert only.
- Approval required.
- Bounded auto.
- Off.

Explain that premium cannot exceed core execution policy and that execution-capable modes remain subject to the kill switch, risk engine, market rules, idempotency, reconciliation, and audit ledger.

Do not claim that premium strategies are backtested or validated. The current thresholds are conservative priors.

### 6.5 Audience paths

Provide one section with four audience cards:

- **New and struggling traders:** replace reactive trades with an explicit plan and understand invalid setups.
- **Active traders:** monitor symbols, alerts, proposals, conditional orders, and managed exits.
- **Developers:** extend AgentFox through plugins, tools, skills, and MCP integrations.
- **Teams and funds:** use approvals, audit trails, deterministic policy, and separated analysis/execution boundaries.

The individual-trader card receives the strongest visual emphasis.

### 6.6 Deployment options

Show two equal, directly comparable deployment cards.

**Self-hosted**

- Label: Available now / Recommended.
- Local ownership of data, credentials, updates, and customization.
- Windows, macOS, and Linux installer paths.
- CTA links to the public installation section.

**Managed cloud**

- Label: Early access.
- Describe guided onboarding, managed infrastructure, upgrades, and workspace isolation as intended benefits, not currently guaranteed service levels.
- CTA opens the prefilled email to `tradingsmartnow@outlook.com`.

### 6.7 Broader AgentFox platform

Make clear that AgentFox is more than a trading bot. Summarize:

- Channels: WhatsApp, Telegram, Discord, Slack, and Microsoft Teams.
- Memory: short-term, long-term, and hybrid memory.
- Tools and skills: files, shell, web, scheduling, and extension points.
- Trading: research, technical analysis, alerts, proposals, and guarded execution.

### 6.8 Closing CTA

Repeat the self-hosted and cloud early-access actions. Reinforce control and boundaries rather than profit or speed.

### 6.9 Footer

Include links to:

- GitHub repository: `https://github.com/UsmanSabir/AgentFox`
- Installation: `https://github.com/UsmanSabir/AgentFox#installation`
- Community trading documentation: `https://github.com/UsmanSabir/AgentFox/blob/main/src/Plugins/TradingAgent/README.md`
- Developer guide: `https://github.com/UsmanSabir/AgentFox/blob/main/docs/DEVELOPMENT.md`
- License: `https://github.com/UsmanSabir/AgentFox/blob/main/LICENSE`
- Cloud early-access email.

Include the full trading-risk disclaimer and a clear statement that AgentFox is software, not investment advice.

## 7. README Star Message

Add this message near the top of the public `README.md`, after the introductory product summary and before the feature list:

> ⭐ If AgentFox helps you build, automate, or trade with more discipline, please star the repository. It helps others discover the project and supports its continued development.

This is an implementation change, separate from the website build, but belongs in the same plan because the homepage links directly to the repository.

## 8. Visual System

### Direction

Use the approved **Safety-first companion** direction: spacious, dark, precise, and calm. The page should feel like trustworthy decision software, not a crypto promotion or a dense trading terminal.

### Theme

- Dark is the default theme.
- A complete light theme is required.
- On first load, use dark unless the visitor has explicitly saved a light preference.
- Persist explicit theme choice in local storage.
- Apply the theme before first paint to avoid a light/dark flash.
- The theme control must have a visible label for screen readers and a clear pressed/current state.

### Color

Use semantic tokens, not component-level raw hex values. The direction uses:

- Near-black or midnight neutral background.
- Elevated dark surfaces with visible borders.
- Green for primary action, guarded progress, and selected state.
- Amber for caution, waiting, and unresolved decisions.
- Red only for destructive or failed states.

Light theme colors must be independently contrast-tested. Do not invert the dark palette mechanically.

### Typography

Use Inter or IBM Plex Sans with a system-font fallback. Favor large, tightly tracked display headings and highly readable 16–18px body copy. Use tabular figures for prices, quantities, and percentages. Avoid all-monospace layouts; reserve monospace styling for data and code.

### Iconography and visual assets

- Use Lucide SVG icons consistently.
- Do not use emoji as structural icons.
- Use code-native product visuals and restrained geometric glow effects.
- Do not fabricate customer logos, testimonials, performance charts, or broker screenshots.
- No stock-market photography is required for the first release.

### Motion

- Use motion only to clarify state or hierarchy.
- Prefer opacity and transform.
- Avoid looping hero animation, parallax, animated tickers, or simulated live-market urgency.
- Respect `prefers-reduced-motion` and render the useful final state immediately.

## 9. Components

Create focused, reusable components with clear inputs:

- `SiteHeader`
- `ThemeToggle`
- `MobileNavigation`
- `Hero`
- `DecisionPreview`
- `TrustStrip`
- `DecisionSteps`
- `AutonomyLadder`
- `AudiencePaths`
- `DeploymentOptions`
- `PlatformCapabilities`
- `CallToAction`
- `SiteFooter`
- `RiskDisclaimer`

Content repeated between header, hero, deployment cards, footer, and metadata must come from a small typed content/config module so URLs and CTA wording cannot drift.

## 10. Data and Interaction Flow

The site has no production backend dependency.

1. SvelteKit prerenders route HTML at build time.
2. Static assets load from the configured base path.
3. Theme initialization reads only the visitor’s saved preference.
4. Internal navigation uses static routes or anchors.
5. Self-hosted links navigate to GitHub.
6. Cloud early access opens the visitor’s email client with a prefilled subject.

No analytics, cookies, tracking pixels, user accounts, remote APIs, or submission database are required for the first release.

## 11. Failure and Fallback Behavior

- Without JavaScript, content and navigation remain readable, external links work, and the default dark theme is usable.
- If local storage is unavailable, theme switching works for the current page without persistence.
- If an email client is not configured, the CTA visibly exposes `tradingsmartnow@outlook.com` so the address can be copied.
- External links must use normal anchors and descriptive text.
- A missing decorative visual must not remove the accompanying explanation.
- GitHub Pages subpath deployment must not break asset URLs or route navigation.

## 12. Accessibility

Target WCAG 2.2 AA.

- Normal text contrast at least 4.5:1.
- Meaningful UI boundaries and icons at least 3:1 where applicable.
- Sequential headings and semantic landmarks.
- A skip link to the main content.
- Visible keyboard focus for every interactive element.
- No hover-only information or actions.
- Web targets at least 24×24 CSS pixels, with primary touch targets at least 44×44 CSS pixels.
- Theme, menu, and disclosure controls expose accessible names and state.
- Color is never the only indication of verdict, risk, or mode.
- The illustrative chart includes a concise text alternative.
- Motion respects reduced-motion preferences.
- Layout remains usable at 200% zoom and without horizontal scrolling at 320px.

## 13. SEO and Sharing

Each prerendered route must include:

- Unique title and meta description.
- Canonical URL derived from deployment configuration.
- Open Graph and Twitter card metadata.
- A generated sitemap and `robots.txt` appropriate for a public marketing site.
- Structured data only where it truthfully describes the software product; do not invent ratings, offers, or reviews.

The homepage description should name AgentFox as a local-first AI agent and mention explainable trading analysis and guarded automation.

## 14. Testing and Verification

### Automated

- Type checking and Svelte checks.
- Production build with adapter-static.
- Link validation for internal routes and required external URLs.
- Unit coverage for theme initialization and CTA configuration where logic exists.
- Accessibility checks for semantic structure, accessible names, and common violations.
- A static-output check that confirms expected HTML files and assets are present.

### Browser verification

Verify at minimum:

- 375px mobile portrait.
- Mobile landscape.
- 768px tablet.
- 1024px desktop.
- 1440px desktop.
- Dark and light themes.
- Keyboard-only navigation.
- Reduced motion.
- 200% zoom.
- GitHub Pages project-subpath preview.
- Cloudflare-compatible root-path preview.

### Content verification

- Every product claim matches the public repository or the approved premium documentation.
- Premium thresholds are described as priors, not backtested findings.
- No profitability, accuracy, return, or performance guarantee appears.
- Every CTA uses its approved destination.
- The README star message is present once and does not interrupt installation instructions.

## 15. Out of Scope for the First Release

- Cloud account creation or provisioning.
- A hosted early-access form or CRM integration.
- Authentication.
- Pricing or payment processing.
- Customer testimonials or performance statistics.
- A live trading-data feed on the marketing site.
- Importing or exposing premium source code.
- Rebuilding the AgentFox management UI or either trading-plugin UI.

## 16. Success Criteria

The design is successful when:

1. A new trader can explain within one page that AgentFox helps form and check a trade plan, including a valid no-trade outcome.
2. A technical buyer can find the public repository, installation path, developer documentation, and extension story without searching the page.
3. A team buyer can identify the approval, policy, audit, and analysis/execution boundaries.
4. The distinction between self-hosted availability and managed-cloud early access is unmistakable.
5. The site is fast, accessible, fully static, and deploys unchanged to GitHub Pages and Cloudflare Pages.
6. The copy remains honest about trading risk, AI’s role, and the absence of validated strategy performance.
