import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    host: true,
    port: Number(process.env.PORT ?? 5173),
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
