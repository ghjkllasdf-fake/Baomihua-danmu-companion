import { test, expect, type Page } from '@playwright/test';
import { emptyPlayer } from '../src/model';

test.beforeEach(async ({ page }) => {
  await page.addInitScript(initialPlayer => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    const initial = { areaFraction: .5, fontSize: 28, scaleWithWindow: true, speedMultiplier: 1, opacity: .85, outlineWidth: 1, density: 60, rolling: true, top: true, bottom: true, dedupe: true, keywords: '', offsetMs: 0, enabled: true };
    let media = { title: '测试作品', episodeNumber: 1, syncMode: 'automatic', state: 'paused', positionMs: 10000 };
    let count = 0, hold = false, pending = '', sequence = 0;
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    const emitState = () => emit({ type: 'state', sequence: ++sequence, data: { player: { ...initialPlayer, ...media,
      sequence, mediaRevision: sequence, sampleMonotonicMs: 1000, positionResolutionMs: 200, source: 'fixture' }, commentCount: 0 } });
    window.addEventListener('fixture-media', event => { media = (event as CustomEvent<typeof media>).detail; emitState(); });
    window.addEventListener('fixture-hidden-switch', event => {
      media = { ...media, episodeNumber: (event as CustomEvent<number>).detail };
      sequence++; emitState();
    });
    window.addEventListener('fixture-hold', () => { hold = true; });
    window.addEventListener('fixture-finish', () => { emit({ type: 'response', id: pending, ok: true, data: { count: 999 } }); });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (msg: { id: string; type: string; payload?: { value?: string; expectedMediaRevision?: number } }) => {
        if (msg.type === 'comments') {
          document.documentElement.dataset.comments = String(++count);
          document.documentElement.dataset.commentEpisode = msg.payload?.value;
          document.documentElement.dataset.commentRevision = String(msg.payload?.expectedMediaRevision);
          if (hold) { pending = msg.id; return; }
        }
        const data = msg.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1'], apiBase: 'http://fixture.invalid/token', settings: initial, settingsRevision: 0, stateSequence: sequence }
          : msg.type === 'search' ? { animes: [{ animeId: 1, animeTitle: '匹配来源' }] }
          : msg.type === 'episodes' ? { bangumi: { episodes: [{ episodeId: 10, episodeNumber: 1 }, { episodeId: 20, episodeNumber: 2 }] } }
          : msg.type === 'comments' ? { count: 17 } : initial;
        setTimeout(() => { emit({ type: 'response', id: msg.id, ok: true, data }); if (msg.type === 'hello') emitState(); }, 10);
      },
    } } });
  }, emptyPlayer);
  await page.goto('/');
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
});

async function selectAndLoad(page: Page) {
  await page.getByRole('button', { name: '使用播放器片名', exact: false }).click();
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.getByText('17 条弹幕已准备')).toBeVisible();
}
async function media(page: Page, episodeNumber: number, title = '测试作品') {
  await page.evaluate(({ episodeNumber, title }) => window.dispatchEvent(new CustomEvent('fixture-media', { detail: { title, episodeNumber, syncMode: 'automatic', state: 'paused', positionMs: 10000 } })), { episodeNumber, title });
}

test('a same-work episode change automatically loads the matching bound episode', async ({ page }) => {
  await selectAndLoad(page);
  await media(page, 2);
  await expect(page.getByLabel('当前分集')).toHaveValue('20');
  await expect(page.locator('html')).toHaveAttribute('data-comments', '2');
  await expect(page.locator('html')).toHaveAttribute('data-comment-episode', '20');
  await expect(page.locator('html')).toHaveAttribute('data-comment-revision', '2');
  await expect(page.locator('.app-footer')).toContainText('17 条弹幕已准备');
});

test('disabling follow preserves manual control and explains that a reload is needed', async ({ page }) => {
  await selectAndLoad(page);
  await page.getByRole('switch', { name: /跟随播放器换集/ }).uncheck();
  await media(page, 2);
  await expect(page.locator('html')).toHaveAttribute('data-comments', '1');
  await expect(page.locator('.source-notice')).toContainText('自动跟随已关闭');
});

test('a missing episode is not silently replaced by the first episode', async ({ page }) => {
  await selectAndLoad(page); await media(page, 3);
  await expect(page.getByRole('button', { name: '加载这一集的弹幕' })).toBeDisabled();
  await expect(page.getByLabel('当前分集')).toHaveValue('');
  await page.getByRole('button', { name: '载入分集' }).click();
  await expect(page.getByRole('button', { name: '加载这一集的弹幕' })).toBeDisabled();
  await expect(page.locator('.source-notice')).toContainText('没有');
  await expect(page.locator('html')).toHaveAttribute('data-comments', '1');
});

test('a late download from a previous media cannot restore the cleared count', async ({ page }) => {
  await page.getByRole('button', { name: '使用播放器片名', exact: false }).click();
  await expect(page.getByLabel('当前分集')).toHaveValue('10');
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-hold')));
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-comments', '1');
  await media(page, 1, '另一部作品');
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-finish')));
  await expect(page.locator('.app-footer')).toContainText('尚未载入弹幕');
  await expect(page.locator('.source-notice')).toContainText('播放器');
});

test('manual other-work searches and API edits never auto-reuse the old source', async ({ page }) => {
  await selectAndLoad(page);
  await page.getByLabel('正在看什么？').fill('手动选择的另一作品');
  await page.getByRole('button', { name: '搜索弹幕来源' }).click();
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-comments', '2');
  await media(page, 2);
  await expect(page.locator('html')).toHaveAttribute('data-comments', '2');
  await page.locator('.api-details summary').click();
  await page.getByLabel('API 地址', { exact: true }).fill('http://other-fixture.invalid/token');
  await media(page, 1);
  await expect(page.locator('html')).toHaveAttribute('data-comments', '2');
  await expect(page.getByRole('button', { name: '加载这一集的弹幕' })).toHaveCount(0);
});

test('a same-turn A-to-B-to-A switch cannot carry a bound source forward', async ({ page }) => {
  await selectAndLoad(page);
  await expect(page.locator('html')).toHaveAttribute('data-comments', '1');
  await page.evaluate(() => {
    window.dispatchEvent(new CustomEvent('fixture-media', { detail: { title: '另一部作品', episodeNumber: 1, syncMode: 'automatic', state: 'paused', positionMs: 10000 } }));
    window.dispatchEvent(new CustomEvent('fixture-media', { detail: { title: '测试作品', episodeNumber: 1, syncMode: 'automatic', state: 'paused', positionMs: 10000 } }));
  });
  await expect(page.locator('.app-footer')).toContainText('尚未载入弹幕');
  await media(page, 2);
  await expect(page.getByText('第 2 集', { exact: true })).toBeVisible();
  await page.waitForTimeout(300);
  await expect(page.locator('html')).toHaveAttribute('data-comments', '1');
});

test('a native-cleared raw-title whitespace transition is not presented as loaded', async ({ page }) => {
  await selectAndLoad(page);
  await expect(page.locator('.source-notice')).toContainText('已载入');
  await media(page, 1, '测试作品 ');
  await expect(page.locator('.app-footer')).toContainText('尚未载入弹幕');
  await expect(page.locator('.source-notice')).not.toContainText('已载入');
  await expect(page.getByRole('button', { name: '加载这一集的弹幕' })).toHaveCount(0);
});

for (const episode of [1, 2]) {
  test(`unobserved native media changes ending on episode ${episode} invalidate a bound source`, async ({ page }) => {
    await selectAndLoad(page);
    await page.evaluate(episode => window.dispatchEvent(new CustomEvent('fixture-hidden-switch', { detail: episode })), episode);
    await expect(page.locator('.app-footer')).toContainText('尚未载入弹幕');
    await expect(page.locator('.source-notice')).not.toContainText('已载入');
    await expect(page.getByRole('button', { name: '加载这一集的弹幕' })).toHaveCount(0);
    await expect(page.locator('html')).toHaveAttribute('data-comments', '1');
  });
}
