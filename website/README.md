# AgentFox marketing website

Static SvelteKit website for AgentFox.

## Local development

```bash
npm ci
npm run dev
```

Run the production checks and build:

```bash
npm run check
npm run build
```

The static output is written to `build/`.

## GitHub Pages

`.github/workflows/pages.yml` builds with `BASE_PATH=/AgentFox` and deploys `website/build` whenever website files change on `dev` or `main`. Enable **GitHub Pages → Source: GitHub Actions** in the repository settings.

## Cloudflare Pages

Create a Pages project with these settings:

- Root directory: `website`
- Build command: `npm ci && npm run build`
- Build output directory: `build`
- Environment variable: `BASE_PATH` left empty
- Node.js: 20 or newer

No server functions or runtime environment variables are required.
