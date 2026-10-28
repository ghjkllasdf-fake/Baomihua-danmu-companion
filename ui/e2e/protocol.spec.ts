import { test, expect } from '@playwright/test';
import { defaultSettings, emptyPlayer } from '../src/model';

for (const mode of ['old-host', 'missing-capability', 'invalid-settings']) {
  test(`incompatible ${mode} never enables native controls or trusts player events`, async ({ page }) => {
    await page.addInitScript(({ settings, initialPlayer, mode }) => {
      const callbacks: ((event: { data: unknown }) => void)[] = [];
      const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
      const player = { ...initialPlayer, sequence: 10, mediaRevision: 1, title: '不可信播放状态', episodeNumber: 1,
        syncMode: 'automatic', state: 'playing', positionMs: 1000, sampleMonotonicMs: 1000, positionResolutionMs: 200 };
      Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
        addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
        postMessage: (message: { id: string; type: string }) => {
          document.documentElement.dataset.commands = `${document.documentElement.dataset.commands ?? ''}${message.type};`;
          const compatibility = mode === 'old-host' ? {} : { protocolVersion: 1, capabilities: mode === 'missing-capability' ? [] : ['media-context-v1'] };
          setTimeout(() => {
            emit({ type: 'response', id: message.id, ok: true, data: { apiBase: '', settings: mode === 'invalid-settings' ? { ...settings, fontSize: 999 } : settings, settingsRevision: 0, stateSequence: 0, ...compatibility } });
            emit({ type: 'state', sequence: 10, data: { player } });
          }, 10);
        },
      } } });
    }, { settings: defaultSettings, initialPlayer: emptyPlayer, mode });
    await page.goto('/');
    await expect(page.getByRole('alert')).toContainText('宿主版本或握手数据不兼容');
    await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeDisabled();
    await expect(page.getByText('桌面宿主已连接')).toHaveCount(0);
    await expect(page.locator('.playback-title')).not.toContainText('不可信');
    await page.getByRole('button', { name: '1/4', exact: true }).click();
    await page.getByRole('switch', { name: '显示弹幕', exact: true }).uncheck();
    await page.waitForTimeout(100);
    await expect(page.locator('html')).toHaveAttribute('data-commands', 'hello;');
    await expect(page.locator('.app-footer')).toContainText('仅预览，未保存');
  });
}

test('malformed search success is rejected without breaking the source panel and explicit retry still loads comments', async ({ page }) => {
  await page.addInitScript(settings => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let searches = 0;
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string }) => {
        const data = message.type === 'hello' ? { apiBase: 'http://fixture.invalid/token', settings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1, capabilities: ['media-context-v1'] }
          : message.type === 'search' ? { animes: ++searches === 1 ? [{ animeId: {}, animeTitle: 'bad' }] : [{ animeId: '1', animeTitle: '测试来源' }] }
          : message.type === 'episodes' ? { bangumi: { episodes: [{ episodeId: '10', episodeNumber: '1' }] } }
          : message.type === 'comments' ? { count: 17 } : {};
        setTimeout(() => callbacks.forEach(callback => callback({ data: { type: 'response', id: message.id, ok: true, data } })), 10);
      },
    } } });
  }, defaultSettings);
  await page.goto('/');
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
  await page.getByLabel('正在看什么？').fill('测试作品');
  await page.getByRole('button', { name: '搜索弹幕来源' }).click();
  await expect(page.getByRole('alert')).toContainText('宿主响应数据无效');
  await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeEnabled();
  await page.getByRole('button', { name: '搜索弹幕来源' }).click();
  await expect(page.getByLabel('当前分集')).toHaveValue('10');
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.locator('.app-footer')).toContainText('17 条弹幕已准备');
  await expect(page.getByRole('alert')).toHaveCount(0);
});
