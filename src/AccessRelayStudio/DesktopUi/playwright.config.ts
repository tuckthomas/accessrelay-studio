import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  use: { baseURL: 'http://127.0.0.1:5197', channel: 'msedge', viewport: { width: 1440, height: 930 }, trace: 'retain-on-failure' },
  webServer: { command: 'pnpm dev', url: 'http://127.0.0.1:5197', reuseExistingServer: true },
  reporter: 'list'
});
