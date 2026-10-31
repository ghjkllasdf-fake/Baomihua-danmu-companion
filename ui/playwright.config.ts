import { defineConfig } from '@playwright/test';
const port = Number(process.env.DANMU_E2E_PORT || 5173);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid E2E preview port.');
export default defineConfig({
  testDir: './e2e', workers: 1, use: { channel: 'msedge', baseURL: `http://127.0.0.1:${port}`, headless: true },
  webServer: { command: `npm run preview -- --host 127.0.0.1 --port ${port}`, url: `http://127.0.0.1:${port}`, reuseExistingServer: false },
});
