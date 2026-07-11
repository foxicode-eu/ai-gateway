import path from 'node:path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    // Proxies API calls to Management so the browser only ever talks to this origin — session cookies (see
    // Management's /auth/login) then work as same-origin, no CORS/SameSite=None/Secure-over-HTTP complexity to
    // fight in local dev. See CLAUDE.md's Dashboard section.
    proxy: {
      '/api': {
        target: 'http://localhost:5162',
        changeOrigin: true,
        rewrite: (requestPath) => requestPath.replace(/^\/api/, ''),
      },
    },
  },
})
