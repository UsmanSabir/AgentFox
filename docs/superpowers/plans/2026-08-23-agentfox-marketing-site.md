# AgentFox Marketing Website Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify the approved static AgentFox marketing homepage, GitHub Pages deployment, Cloudflare-compatible output, and README star prompt.

**Architecture:** Add a standalone `website/` SvelteKit project to the public AgentFox repository. Prerender one homepage with focused Svelte components, centralized content/links, a dark-default persisted theme, and no runtime backend dependency.

**Tech Stack:** Svelte 5, SvelteKit, TypeScript, Tailwind CSS 4, Lucide Svelte, adapter-static, Node test runner, GitHub Actions

**Spec:** `docs/superpowers/specs/2026-08-23-agentfox-marketing-site-design.md`

## Global Constraints

- The first release is one prerendered homepage with anchored sections.
- Dark is the default; light is an explicit persisted choice applied before paint.
- GitHub Pages is the reference deployment; the same static output must work on Cloudflare Pages.
- All product claims must match public or approved premium documentation.
- No profit, performance, accuracy, or investment-advice claims.
- Premium source must not be imported or copied into the public repository.
- Target WCAG 2.2 AA and support reduced motion, keyboard navigation, 320px width, and 200% zoom.

---

### Task 1: Static Project Foundation and Contracts

**Files:**
- Create: `website/package.json`
- Create: `website/package-lock.json`
- Create: `website/svelte.config.js`
- Create: `website/vite.config.ts`
- Create: `website/tsconfig.json`
- Create: `website/src/app.html`
- Create: `website/src/lib/content.js`
- Create: `website/src/lib/theme.js`
- Create: `website/tests/content.test.mjs`
- Create: `website/tests/theme.test.mjs`
- Modify: `.gitignore`

**Interfaces:**
- Produces `siteLinks`, `hero`, `audiences`, `deploymentOptions`, and `riskDisclaimer` from `content.js`.
- Produces `resolveInitialTheme(savedTheme)` and `nextTheme(theme)` from `theme.js`.

- [ ] Write Node tests asserting the installation URL, encoded early-access mailto, four audiences, self-host/cloud states, risk language, dark-default resolution, saved-light resolution, and theme toggling.
- [ ] Run `npm test` and verify it fails because the content and theme modules do not exist.
- [ ] Add the minimal package/configuration files and implementations needed to pass the tests. Configure adapter-static output as `build`, prerendering, trailing slashes, and a deployment base path from `BASE_PATH`.
- [ ] Run `npm test` and verify all contract tests pass.
- [ ] Commit the foundation.

### Task 2: Accessible Homepage and Theme

**Files:**
- Create: `website/src/app.css`
- Create: `website/src/routes/+layout.js`
- Create: `website/src/routes/+layout.svelte`
- Create: `website/src/routes/+page.svelte`
- Create: `website/src/lib/components/SiteHeader.svelte`
- Create: `website/src/lib/components/ThemeToggle.svelte`
- Create: `website/src/lib/components/Hero.svelte`
- Create: `website/src/lib/components/DecisionPreview.svelte`
- Create: `website/src/lib/components/TrustStrip.svelte`
- Create: `website/src/lib/components/DecisionSteps.svelte`
- Create: `website/src/lib/components/AutonomyLadder.svelte`
- Create: `website/src/lib/components/AudiencePaths.svelte`
- Create: `website/src/lib/components/DeploymentOptions.svelte`
- Create: `website/src/lib/components/PlatformCapabilities.svelte`
- Create: `website/src/lib/components/SiteFooter.svelte`

**Interfaces:**
- Consumes all copy and destinations from `content.js`.
- `ThemeToggle` consumes `theme: 'dark' | 'light'` and emits an `onToggle` callback.
- The layout owns theme persistence and applies `data-theme` to `document.documentElement`.

- [ ] Add a failing static-source verification test that expects the homepage section IDs, skip link, semantic main landmark, visible disclaimer, theme control accessible name, and reduced-motion stylesheet.
- [ ] Run `npm test` and verify the new assertions fail.
- [ ] Implement the approved safety-first homepage, semantic sections, responsive navigation, illustrative decision card, audience emphasis, autonomy ladder, deployment cards, footer, and both semantic themes.
- [ ] Add the pre-paint theme initializer to `app.html`, with dark fallback and safe local-storage handling.
- [ ] Run `npm test`, `npm run check`, and `npm run build`; verify they pass.
- [ ] Commit the homepage.

### Task 3: Static Output, SEO, and Hosting

**Files:**
- Create: `website/static/favicon.svg`
- Create: `website/static/robots.txt`
- Create: `website/static/site.webmanifest`
- Create: `website/scripts/verify-static.mjs`
- Create: `.github/workflows/pages.yml`
- Modify: `website/package.json`

**Interfaces:**
- `npm run verify:static` consumes `website/build` and fails unless `index.html`, `robots.txt`, manifest, favicon, critical metadata, canonical/OG tags, CTA URLs, and internal anchors exist.
- GitHub Actions sets `BASE_PATH=/${{ github.event.repository.name }}` for project Pages builds and uploads `website/build`.

- [ ] Write `verify-static.mjs` first and run it against the current build to expose missing metadata/assets.
- [ ] Add SEO metadata, canonical base configuration, favicon, manifest, robots file, and the GitHub Pages workflow.
- [ ] Rebuild with an empty base path and run `npm run verify:static`.
- [ ] Rebuild with `BASE_PATH=/AgentFox` and verify generated asset/internal URLs support the project subpath.
- [ ] Commit hosting and SEO support.

### Task 4: README Prompt and Final Verification

**Files:**
- Modify: `README.md`
- Modify: `website/README.md`

**Interfaces:**
- The root README star message uses the exact approved wording.
- The website README documents local development, GitHub Pages, and Cloudflare Pages build/output settings.

- [ ] Add a failing content assertion that the root README contains the approved star message exactly once.
- [ ] Run the assertion and verify it fails.
- [ ] Add the star message after the introductory summary and create concise website deployment documentation.
- [ ] Run `npm test`, `npm run check`, `npm run build`, and `npm run verify:static`.
- [ ] Run `git diff --check` and inspect `git status` to confirm no generated build or dependency directories are tracked.
- [ ] Perform browser checks in dark/light themes at 375px and desktop, keyboard navigation, reduced motion, and all real links.
- [ ] Commit the README and verified final state.
