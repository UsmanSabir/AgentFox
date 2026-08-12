import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig, loadEnv } from 'vite';

// Standalone Vite SPA (no SvelteKit): the host frames this page at /ext/trading, so there is no
// routing to do and nothing to prerender.
//
// Two settings matter:
//   base      — must match AgentFox's plugin-asset layout (PluginUiPaths.AssetPrefix), i.e.
//               /plugin-assets/{slug}/. That is where the host serves this plugin's embedded
//               wwwroot from; a default base of '/' would 404 inside the frame. Note it is NOT
//               /ext/trading — that path is the host page that frames us.
//   outDir    — ../wwwroot, which TradingAgent.csproj embeds into the plugin DLL.
export default defineConfig(({ mode }) => {
	const env = loadEnv(mode, '.', '');
	const backendUrl = env.BACKEND_URL ?? 'http://localhost:5000';

	return {
		base: '/plugin-assets/trading/',
		plugins: [svelte()],
		build: {
			outDir: '../wwwroot',
			emptyOutDir: true,
			// The host embeds these files as resources, so hashed names are fine but a manifest is not
			// needed; keep the output flat and predictable for the EmbeddedResource glob.
			assetsDir: 'assets'
		},
		server: {
			// Standalone dev: run this UI on its own port against a live backend. In the real app the
			// page is same-origin with the API, so no proxy is involved.
			proxy: {
				'/api': { target: backendUrl, changeOrigin: true }
			}
		}
	};
});
