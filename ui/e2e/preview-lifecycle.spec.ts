import { test, expect } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

type MotionCounts = { adds: number; removes: number; active: number };

test('the actual StrictMode preview releases preference listeners on every unmount', async ({ page }) => {
  await page.addInitScript(() => {
    const original = window.matchMedia.bind(window);
    const tracked = new Map<MediaQueryList, Set<unknown>>();
    let adds = 0, removes = 0;
    Object.defineProperty(window, 'motionListenerCounts', { value: () => ({ adds, removes,
      active: Array.from(tracked.values()).reduce((count, listeners) => count + listeners.size, 0) }) });
    window.matchMedia = query => {
      const media = original(query);
      if (query === '(prefers-reduced-motion: reduce)') {
        const listeners = new Set<unknown>();
        tracked.set(media, listeners);
        const add = media.addEventListener, remove = media.removeEventListener;
        Object.defineProperty(media, 'addEventListener', { value: (...args: unknown[]) => {
          if (args[0] === 'change' && args[1]) { adds++; listeners.add(args[1]); }
          return Reflect.apply(add, media, args);
        } });
        Object.defineProperty(media, 'removeEventListener', { value: (...args: unknown[]) => {
          if (args[0] === 'change' && args[1]) { removes++; listeners.delete(args[1]); }
          return Reflect.apply(remove, media, args);
        } });
      }
      return media;
    };
  });
  // Only this fixture uses a temporary dev server; all application flows test the production build.
  const server = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)),
    logLevel: 'error', server: { host: '127.0.0.1', port: 0, strictPort: true, hmr: false } });
  try {
    await server.listen();
    const address = server.httpServer?.address();
    if (!address || typeof address === 'string') throw new Error('The isolated fixture did not acquire a loopback port.');
    await page.goto(`http://127.0.0.1:${address.port}/e2e/fixtures/preview-lifecycle.html`);
    const counts = () => page.evaluate(() => (window as Window & { motionListenerCounts(): MotionCounts }).motionListenerCounts());
    const mount = page.getByRole('button', { name: 'Mount preview', exact: true });
    await expect(mount).toBeEnabled();
    for (let cycle = 0; cycle < 3; cycle++) {
      await mount.click();
      await expect(page.locator('.preview')).toBeVisible();
      await expect.poll(async () => (await counts()).active).toBe(1);
      await page.getByRole('button', { name: 'Unmount preview', exact: true }).click();
      await expect(page.locator('.preview')).toHaveCount(0);
      await expect.poll(async () => (await counts()).active).toBe(0);
    }
    const final = await counts();
    expect(final.adds).toBeGreaterThanOrEqual(3);
    expect(final.removes).toBe(final.adds);
  } finally { await server.close(); }
});
