import { test, expect } from '@playwright/test';
import { defaultSettings, emptyPlayer } from '../src/model';

test('saved API entry starts automatic matching without source-selection clicks', async ({ page }) => {
  await page.addInitScript(({ settings, player }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let sequence = 0;
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string }) => {
        if (['search', 'episodes', 'comments'].includes(message.type)) throw new Error('Automatic UI must not require a manual source command.');
        document.documentElement.dataset.lastCommand = message.type;
        const data = message.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1', 'automatic-match-v1', 'api-persistence-v1'],
          apiBase: '', apiSaved: false, apiNotice: '', settings, settingsRevision: 0, stateSequence: 0 } : { apiBase: 'http://fixture.invalid/token', saved: true };
        setTimeout(() => {
          emit({ type: 'response', id: message.id, ok: true, data });
          emit({ type: 'state', sequence: ++sequence, data: { commentCount: message.type === 'save-api' ? 23 : 0,
            player: { ...player, sequence, mediaRevision: 1, title: '当前作品', episodeNumber: 2 },
            automaticMatch: message.type === 'save-api' ?
              { state: 'loaded', message: '已自动匹配并载入 23 条弹幕。', sourceTitle: '当前作品(2025)源A' } :
              { state: 'failed', message: '请填写 API 地址。', sourceTitle: '' },
          } });
        }, 10);
      },
    } } });
  }, { settings: defaultSettings, player: emptyPlayer });
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '自动匹配弹幕' })).toBeVisible();
  await page.getByLabel('API 地址', { exact: true }).fill('http://fixture.invalid/token');
  await page.getByRole('button', { name: '保存地址', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-last-command', 'save-api');
  await expect(page.locator('.app-footer')).toContainText('23 条弹幕已准备');
  await expect(page.locator('.source-notice')).toContainText('已自动匹配');
  await expect(page.locator('.source-notice')).toContainText('源A');
  await expect(page.locator('.playback-strip')).not.toContainText('已自动同步');
});

test('all preview source types use rolling entry without fixed rows', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('checkbox', { name: '滚动', exact: true }).uncheck();
  await expect(page.locator('.preview-bullet')).toHaveCount(3);
  await expect(page.locator('.preview-fixed')).toHaveCount(0);
  for (const bullet of await page.locator('.preview-bullet').all()) {
    const before = await bullet.evaluate(e => e.getBoundingClientRect().left);
    await page.waitForTimeout(100);
    const after = await bullet.evaluate(e => e.getBoundingClientRect().left);
    expect(after).toBeLessThan(before);
  }
});
