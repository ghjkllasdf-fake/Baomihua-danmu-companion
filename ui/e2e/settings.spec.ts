import { test, expect } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.clock.install();
  await page.addInitScript(() => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    const updates: { id: string; payload: { settings: unknown } }[] = [];
    let settings: unknown = { areaFraction: .5, fontSize: 28, scaleWithWindow: true, speedMultiplier: 1, opacity: .85, outlineWidth: 1, density: 60, rolling: true, top: true, bottom: true, dedupe: true, keywords: '', offsetMs: 0, enabled: true };
    let settingsRevision = 0, reads = 0, holdReads = false;
    window.addEventListener('fixture-commit', event => { settings = updates[(event as CustomEvent<number>).detail].payload.settings; settingsRevision++; });
    window.addEventListener('fixture-hold-reads', event => { holdReads = Boolean((event as CustomEvent).detail); });
    window.addEventListener('fixture-settle', event => {
      const { index, ok } = (event as CustomEvent<{ index: number; ok: boolean }>).detail;
      const request = updates[index];
      if (ok) { settings = request.payload.settings; settingsRevision++; }
      callbacks.forEach(cb => cb({ data: { type: 'response', id: request.id, ok, data: request.payload.settings, settingsRevision, code: 'settings-not-applied', error: '显示偏好保存失败，本次设置未应用。' } }));
    });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (msg: { id: string; type: string; payload: { settings: unknown } }) => {
        if (msg.type === 'hello') setTimeout(() => callbacks.forEach(cb => cb({ data: { type: 'response', id: msg.id, ok: true, data: { protocolVersion: 1, capabilities: ['media-context-v1'], apiBase: 'http://fixture.invalid/token', settings, settingsRevision, stateSequence: 0 } } })), 10);
        if (msg.type === 'get-settings') {
          document.documentElement.dataset.reads = String(++reads);
          if (!holdReads) setTimeout(() => callbacks.forEach(cb => cb({ data: { type: 'response', id: msg.id, ok: true, data: { settings, settingsRevision } } })), 10);
        }
        if (msg.type === 'update-settings') { updates.push(msg); document.documentElement.dataset.updates = String(updates.length); }
      },
    } } });
  });
  await page.goto('/');
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
});

test('a rejected save rolls controls back to the confirmed settings', async ({ page }) => {
  await page.getByRole('button', { name: '1/4', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-settle', { detail: { index: 0, ok: false } })));
  await expect(page.getByRole('alert')).toContainText('保存失败');
  await expect(page.getByRole('button', { name: '1/2', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: '1/4', exact: true })).toHaveAttribute('aria-pressed', 'false');
});

test('late acknowledgments cannot replace a newer pending edit, but remain the rollback baseline', async ({ page }) => {
  await page.getByRole('button', { name: '1/4', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
  await page.getByRole('button', { name: '3/4', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-updates', '2');
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-settle', { detail: { index: 0, ok: true } })));
  await expect(page.getByRole('button', { name: '3/4', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-settle', { detail: { index: 1, ok: false } })));
  await expect(page.getByRole('button', { name: '1/4', exact: true })).toHaveAttribute('aria-pressed', 'true');
});

test('an older success arriving after the latest failure still restores the last saved value', async ({ page }) => {
  await page.getByRole('button', { name: '1/4', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
  await page.getByRole('button', { name: '3/4', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-updates', '2');
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-settle', { detail: { index: 1, ok: false } })));
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-settle', { detail: { index: 0, ok: true } })));
  await expect(page.getByRole('button', { name: '1/4', exact: true })).toHaveAttribute('aria-pressed', 'true');
});

test('a lost save reply reads back the host value instead of silently assuming rollback', async ({ page }) => {
  await page.getByRole('button', { name: '1/4', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
  await page.clock.fastForward(10001);
  await expect(page.locator('html')).toHaveAttribute('data-reads', '1');
  await expect(page.getByRole('button', { name: '1/2', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.app-footer')).toContainText('显示偏好已保存');
  await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
});

test('an uncertain outcome offers a read-only retry without duplicating the write', async ({ page }) => {
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-hold-reads', { detail: true })));
  await page.getByRole('button', { name: '1/4', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-commit', { detail: 0 })));
  await page.clock.fastForward(10001);
  await expect(page.locator('html')).toHaveAttribute('data-reads', '1');
  await page.clock.fastForward(10001);
  await expect(page.locator('.app-footer')).toContainText('显示设置待确认');
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-hold-reads', { detail: false })));
  await page.getByRole('button', { name: '重新同步设置', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-reads', '2');
  await expect(page.locator('.app-footer')).toContainText('显示偏好已保存');
  await expect(page.getByRole('button', { name: '1/4', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
});
