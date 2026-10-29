import { test, expect, type Page } from '@playwright/test';
import { defaultSettings, emptyPlayer } from '../src/model';

async function openFixture(page: Page, preload = false) {
  await page.addInitScript(({ settings, initialPlayer, preload }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let revision = preload ? 0 : 41, title = preload ? '' : '测试作品 A', sequence = 0;
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    const state = () => emit({ type: 'state', sequence: ++sequence, data: { commentCount: 0, player: title ? {
      ...initialPlayer, sequence, mediaRevision: revision, title, episodeNumber: 1,
      syncMode: 'automatic', state: 'paused', positionMs: 10000, sampleMonotonicMs: 1000, positionResolutionMs: 200,
    } : { ...initialPlayer, sequence, mediaRevision: revision } } });
    window.addEventListener('fixture-unpublished-media', () => { title = '测试作品 B'; revision++; });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string; payload?: { expectedMediaRevision?: unknown } }) => {
        setTimeout(() => {
          if (message.type === 'comments') {
            const expected = message.payload?.expectedMediaRevision;
            document.documentElement.dataset.expectedMediaRevision = String(expected);
            if (expected !== revision) {
              state();
              emit({ type: 'response', id: message.id, ok: false, code: 'media-changed', error: '播放器已切换，请核对分集并重新加载弹幕。' });
              return;
            }
            document.documentElement.dataset.acceptedDownloads = String(Number(document.documentElement.dataset.acceptedDownloads ?? 0) + 1);
          }
          const data = message.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1'], apiBase: 'http://fixture.invalid/token', settings, settingsRevision: 0, stateSequence: sequence }
            : message.type === 'search' ? { animes: [{ animeId: 1, animeTitle: '测试来源' }] }
            : message.type === 'episodes' ? { bangumi: { episodes: [{ episodeId: 10, episodeNumber: 1 }] } }
            : message.type === 'comments' ? { count: 17 } : {};
          emit({ type: 'response', id: message.id, ok: true, data });
          if (message.type === 'hello') state();
        }, 10);
      },
    } } });
  }, { settings: defaultSettings, initialPlayer: emptyPlayer, preload });
  await page.goto('/');
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
  await page.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
}

async function chooseEpisode(page: Page, preload = false) {
  if (preload) {
    await page.getByLabel('正在看什么？').fill('预载作品');
    await page.getByRole('button', { name: '搜索弹幕来源' }).click();
  } else {
    await page.getByRole('button', { name: '使用播放器片名', exact: false }).click();
  }
  await expect(page.getByLabel('当前分集')).toHaveValue('10');
}

for (const preload of [false, true]) {
  test(`comments carry the observed native media revision ${preload ? 'zero for preload' : 'rather than a state sequence or UI epoch'}`, async ({ page }) => {
    await openFixture(page, preload);
    await chooseEpisode(page, preload);
    await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-expected-media-revision', preload ? '0' : '41');
    await expect(page.locator('html')).toHaveAttribute('data-accepted-downloads', '1');
    await expect(page.locator('.mini-footer')).toContainText('17 条弹幕已准备');
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}

test('a native admission conflict publishes the new player and permits explicit recovery without retrying old comments', async ({ page }) => {
  await openFixture(page);
  await chooseEpisode(page);
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-unpublished-media')));
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-expected-media-revision', '41');
  await expect(page.locator('.playback-title')).toContainText('测试作品 B');
  await expect(page.locator('.match-status')).toBeEmpty();
  await expect(page.locator('.mini-footer')).not.toContainText('条弹幕已准备');
  await expect(page.getByRole('button', { name: '取消当前请求' })).toHaveCount(0);
  expect(await page.locator('html').getAttribute('data-accepted-downloads')).toBeNull();
  await expect(page.locator('.source-notice')).not.toContainText('已载入');
  await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeEnabled();
  await chooseEpisode(page);
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-expected-media-revision', '42');
  await expect(page.locator('html')).toHaveAttribute('data-accepted-downloads', '1');
  await expect(page.locator('.mini-footer')).toContainText('17 条弹幕已准备');
  await expect(page.getByRole('alert')).toHaveCount(0);
});
