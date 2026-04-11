import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig, loadEnv } from 'vite';

// Use the functional form so we can read env before the config resolves.
// BACKEND_URL is intentionally NOT prefixed with VITE_ — it stays server-side
// (used only for the dev proxy) and is never bundled into the browser output.
export default defineConfig(({ mode }) => {
	const env = loadEnv(mode, process.cwd(), ''); // load all env vars (no prefix filter)
	const backendUrl = env.BACKEND_URL ?? 'http://localhost:5000';

	return {
		plugins: [tailwindcss(), sveltekit()],

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
