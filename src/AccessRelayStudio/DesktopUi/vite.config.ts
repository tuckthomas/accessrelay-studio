import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({
  base: './',
  plugins: [react(), {
    name: 'production-csp',
    transformIndexHtml: {
      order: 'post',
      handler(html, context) {
        return context.server ? html : html.replace(' ws://127.0.0.1:5197', '');
      }
    }
  }],
  build: { target: 'es2022', sourcemap: false, outDir: 'dist', emptyOutDir: true },
  server: { host: '127.0.0.1', port: 5197, strictPort: true },
  preview: { host: '127.0.0.1', port: 5198, strictPort: true }
});
