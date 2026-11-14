import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig, loadEnv } from 'vite';

// Use the functional form so we can read env before the config resolves.
// BACKEND_URL is intentionally NOT prefixed with VITE_ — it stays server-side
// (used only for the dev proxy) and is never bundled into the browser output.
export default defineConfig(({ mode }) => {
	const env = loadEnv(mode, '.', ''); /* Vite resolves this from the frontend project root */
	const backendUrl = env.BACKEND_URL ?? 'http://localhost:5000';

	return {
		plugins: [
			tailwindcss(),
			sveltekit({
				preprocess: vitePreprocess(),
				adapter: adapter({
					// SvelteKit static build output → .NET wwwroot
					pages: '../Agent/wwwroot',
					assets: '../Agent/wwwroot',
					// SPA fallback: all unknown routes return index.html (handled by ASP.NET Core too)
					fallback: 'index.html',
					precompress: false,
					strict: false
				})
			})
		],
		server: {
			// Dev proxy: /api/* → .NET backend
			// In production (embedded or same-origin deploy) the browser hits /api
			// directly on the same host — no proxy needed.
			proxy: {
				'/api': {
					target:       backendUrl,
					changeOrigin: true
				}
			}
		}
	};
});
