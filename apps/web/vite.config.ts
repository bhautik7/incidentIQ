import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

/**
 * The development key, added by the dev server rather than by the browser.
 *
 * Local only: this file is not part of the container image, which injects the
 * real key into nginx instead. Override with VITE_DEV_API_KEY when the local
 * stack is configured with a different one.
 */
const DEV_KEY_HEADER = {
  'X-Api-Key': process.env.VITE_DEV_API_KEY ?? 'iiq_dev_0123456789abcdef',
}

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    host: true,
    port: Number(process.env.PORT ?? 5173),
    // The same three prefixes nginx proxies in the container, pointed at the
    // published host ports. Without this, `npm run dev` would be the only
    // environment where the browser still needed an API key - which is exactly
    // the arrangement this replaced.
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true, headers: DEV_KEY_HEADER },
      '/hubs': { target: 'http://localhost:5080', changeOrigin: true, ws: true, headers: DEV_KEY_HEADER },
      '/ingest': {
        target: 'http://localhost:5081',
        changeOrigin: true,
        headers: DEV_KEY_HEADER,
        rewrite: (path) => path.replace(/^\/ingest/, ''),
      },
    },
  },
  preview: {
    host: true,
    port: Number(process.env.PORT ?? 5173),
  },
  build: {
    // Route chunks are lazy-loaded; this keeps the warning threshold honest
    // rather than silencing a real regression.
    chunkSizeWarningLimit: 700,
  },
})
