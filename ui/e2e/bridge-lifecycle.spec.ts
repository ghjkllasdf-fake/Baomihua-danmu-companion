import { test, expect } from '@playwright/test';
import { createServer } from 'vite';
import { fileURLToPath } from 'node:url';
import { defaultSettings, emptyPlayer } from '../src/model';

for (const conflict of [false, true]) {
test(`real App revokes playback and writes on ${conflict ? 'conflicting settings' : 'missing capability'} and recovers by read only`, async ({ page }) => {
  await page.addInitScript(({ settings: initial, player: empty, conflict }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let hellos = 0, settings = initial, settingsRevision = 0;
    let failSecond: (() => void) | undefined;
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    window.addEventListener('fixture-fail-handshake', () => failSecond?.());
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string; payload?: { settings?: typeof initial } }) => {
        if (message.type === 'update-settings') {
          settings = message.payload!.settings!; settingsRevision++;
          document.documentElement.dataset.updates = String(settingsRevision); return;
        }
        if (message.type === 'get-settings') {
          emit({ type: 'response', id: message.id, ok: true, data: { settings, settingsRevision } }); return;
        }
        if (message.type !== 'hello') return;
        const number = ++hellos;
        const reply = () => {
          emit({ type: 'response', id: message.id, ok: true, data: { apiBase: '', settings, settingsRevision: number === 2 && conflict ? 0 : settingsRevision,
            stateSequence: number === 1 ? 0 : 1, protocolVersion: 1, capabilities: number === 2 && !conflict ? [] : ['media-context-v1'] } });
          if (number !== 2) emit({ type: 'state', sequence: number === 1 ? 1 : 2, data: { player: { ...empty,
            sequence: number === 1 ? 1 : 2, mediaRevision: number === 1 ? 1 : 2, title: number === 1 ? '旧播放器 A' : '恢复后的播放器 B',
            episodeNumber: 1, syncMode: 'automatic', state: 'playing', positionMs: 1000, sampleMonotonicMs: 1000, positionResolutionMs: 200 } } });
        };
        if (number === 2) { failSecond = reply; document.documentElement.dataset.handshakePending = 'true'; }
        else setTimeout(reply, 10);
      },
    } } });
  }, { settings: defaultSettings, player: emptyPlayer, conflict });
  const server = await createServer({ root: fileURLToPath(new URL('../', import.meta.url)), logLevel: 'error', server: { host: '127.0.0.1', port: 0, strictPort: true, hmr: false } });
  try {
    await server.listen(); const address = server.httpServer?.address();
    if (!address || typeof address === 'string') throw new Error('The isolated bridge fixture did not acquire a loopback port.');
    await page.goto(`http://127.0.0.1:${address.port}/e2e/fixtures/bridge-lifecycle.html`);
    await expect(page.getByText('桌面宿主已连接')).toBeVisible();
    await expect(page.locator('.playback-title')).toContainText('旧播放器 A');
    await page.getByRole('button', { name: '1/4', exact: true }).click();
    await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
    await page.locator('#rehello').click();
    await expect(page.locator('html')).toHaveAttribute('data-handshake-pending', 'true');
    await expect(page.getByText('桌面宿主已连接')).toHaveCount(0);
    await expect(page.getByText('已自动同步 · 播放中')).toHaveCount(0);
    await expect(page.locator('.playback-title')).not.toContainText('旧播放器 A');
    await page.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
    await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeDisabled();
    await page.evaluate(() => window.dispatchEvent(new Event('fixture-fail-handshake')));
    await expect(page.locator('#rehello-result')).toHaveText('host-incompatible');
    await expect(page.getByText('桌面宿主已连接')).toHaveCount(0);
    await expect(page.getByText('已自动同步 · 播放中')).toHaveCount(0);
    await expect(page.locator('.playback-title')).not.toContainText('旧播放器 A');
    await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeDisabled();
    await expect(page.locator('.mini-footer')).toContainText('显示设置待确认');
    await page.getByRole('button', { name: '返回设置', exact: true }).click();
    await page.getByRole('button', { name: '3/4', exact: true }).click();
    await page.waitForTimeout(100);
    await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
    await page.locator('#rehello').click();
    await expect(page.locator('#rehello-result')).toHaveText('ready');
    await expect(page.getByText('桌面宿主已连接')).toBeVisible();
    await expect(page.locator('.playback-title')).toContainText('恢复后的播放器 B');
    await expect(page.locator('.mini-footer')).toContainText('显示偏好已保存');
    await expect(page.getByRole('button', { name: '1/4', exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('html')).toHaveAttribute('data-updates', '1');
  } finally { await server.close(); }
});
}
