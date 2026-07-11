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
    // fight in local dev. See CLAUDE.md's Dashboard section. Only exercised when running the Dashboard
    // standalone (`npm run dev`) — the aspire/ AppHost's YARP gateway intercepts /api/** before it ever reaches
    // this dev server, see aspire/README.md.
    proxy: {
      '/api': {
        target: 'http://localhost:5162',
        changeOrigin: true,
        rewrite: (requestPath) => requestPath.replace(/^\/api/, ''),
      },
    },
    // Vite 5+ rejects requests whose Host header it doesn't recognize by default. When fronted by the aspire/
    // AppHost's YARP gateway, the proxied request's Host header is Aspire's own internal service-discovery
    // hostname (e.g. "aspire.dev.internal"), not "localhost" — allow all hosts rather than chase that name
    // across environments; this dev server was never reachable from outside the host machine anyway.
    allowedHosts: true,
  },
})
