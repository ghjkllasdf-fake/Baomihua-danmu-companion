import { test as base, expect } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

export { expect };
export type { Locator, Page } from '@playwright/test';

// Only legacy preview-specific regressions use this isolated fixture. Product
// interaction tests always exercise the actual compact production build.
export const test = base.extend<{ previewUrl: string }>({
  previewUrl: async ({}, use) => {
    const server = await createServer({ root: fileURLToPath(new URL('../../', import.meta.url)),
      logLevel: 'error', server: { host: '127.0.0.1', port: 0, strictPort: true, hmr: false } });
    try {
      await server.listen();
      const address = server.httpServer?.address();
      if (!address || typeof address === 'string') throw new Error('The preview fixture did not acquire a loopback port.');
      await use(`http://127.0.0.1:${address.port}/e2e/fixtures/preview-controls.html`);
    } finally { await server.close(); }
  },
});
