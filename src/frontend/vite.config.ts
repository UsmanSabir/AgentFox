import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';

export default defineConfig({
	plugins: [
		tailwindcss(),
		sveltekit()
	],

	server: {
		// Dev proxy: forward /api calls to the running .NET backend
		proxy: {
			'/api': {
				target:       'http://localhost:8080',
				changeOrigin: true
			}
		}
	}
});
