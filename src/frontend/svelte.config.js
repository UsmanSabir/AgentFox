import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	preprocess: vitePreprocess(),

	kit: {
		adapter: adapter({
			// SvelteKit static build output → .NET wwwroot
			pages:       '../Agent/wwwroot',
			assets:      '../Agent/wwwroot',
			// SPA fallback: all unknown routes return index.html (handled by ASP.NET Core too)
			fallback:    'index.html',
			precompress: false,
			strict:      false
		})
	}
};

export default config;
