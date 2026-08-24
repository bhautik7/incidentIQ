import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    port: Number(process.env.PORT ?? 5173),
  },
  preview: {
    host: true,
    port: Number(process.env.PORT ?? 5173),
  },
})
