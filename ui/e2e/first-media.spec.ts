import { test, expect, type Page } from '@playwright/test';
import { defaultSettings, emptyPlayer } from '../src/model';

test.beforeEach(async ({ page }) => {
  await page.addInitScript(({ settings, initialPlayer }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    const counts: Record<string, number> = {};
    let sequence = 0, heldType = '', held: { id: string; type: string } | undefined;
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    window.addEventListener('fixture-hold', event => { heldType = (event as CustomEvent<string>).detail; });
    window.addEventListener('fixture-first-media', () => emit({ type: 'state', sequence: ++sequence, data: {
      player: { ...initialPlayer, sequence, mediaRevision: 1, title: '测试作品', episodeNumber: 1,
        syncMode: 'automatic', state: 'paused', positionMs: 10000, sampleMonotonicMs: 1000, positionResolutionMs: 200, source: 'fixture' },
      commentCount: 0,
    } }));
    window.addEventListener('fixture-lost-media', () => emit({ type: 'state', sequence: ++sequence, data: {
      player: { ...initialPlayer, sequence, mediaRevision: 1, syncMode: 'unavailable', state: 'unknown', source: 'fixture' },
      commentCount: 0,
    } }));
    window.addEventListener('fixture-finish-old', () => {
      if (held) emit({ type: 'response', id: held.id, ok: false, error: '请求已取消。' });
      held = undefined; heldType = '';
    });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string }) => {
        counts[message.type] = (counts[message.type] ?? 0) + 1;
        document.documentElement.dataset[message.type] = String(counts[message.type]);
        if (message.type === heldType) { held = message; return; }
        const data = message.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1'], apiBase: 'http://fixture.invalid/token', settings, settingsRevision: 0, stateSequence: sequence }
          : message.type === 'search' ? { animes: [{ animeId: 1, animeTitle: '测试来源' }] }
          : message.type === 'episodes' ? { bangumi: { episodes: [{ episodeId: 10, episodeNumber: 1 }] } }
          : message.type === 'comments' ? { count: 17 } : {};
        setTimeout(() => emit({ type: 'response', id: message.id, ok: true, data }), 10);
      },
    } } });
  }, { settings: defaultSettings, initialPlayer: emptyPlayer });
  await page.goto('/');
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
});

async function manualSearch(page: Page) {
  await page.getByRole('textbox', { name: '正在看什么？' }).fill('测试作品');
  await page.getByRole('button', { name: '搜索弹幕来源' }).click();
}

for (const phase of ['search', 'episodes', 'comments']) {
  test(`first media clears an in-flight ${phase} without leaving the source panel stuck`, async ({ page }) => {
    await page.evaluate(type => window.dispatchEvent(new CustomEvent('fixture-hold', { detail: type })), phase);
    await manualSearch(page);
    if (phase === 'comments') {
      await expect(page.getByLabel('当前分集')).toHaveValue('10');
      await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
    }
    await expect(page.locator('html')).toHaveAttribute(`data-${phase}`, '1');
    await expect(page.getByRole('button', { name: '取消当前请求' })).toBeVisible();
    await page.evaluate(() => window.dispatchEvent(new Event('fixture-first-media')));
    await page.evaluate(() => window.dispatchEvent(new Event('fixture-finish-old')));
    await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeEnabled();
    await expect(page.getByRole('button', { name: '取消当前请求' })).toHaveCount(0);
    await expect(page.locator('.source-notice')).toContainText('播放器');
    await expect(page.locator('.source-notice')).not.toContainText('正在');
    await page.getByRole('button', { name: '使用播放器片名', exact: false }).click();
    await expect(page.getByLabel('当前分集')).toHaveValue('10');
    await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
    await expect(page.locator('.app-footer')).toContainText('17 条弹幕已准备');
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
}

test('preloaded comments cleared at first media are not still described as ready to watch', async ({ page }) => {
  await manualSearch(page);
  await expect(page.getByLabel('当前分集')).toHaveValue('10');
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.locator('.app-footer')).toContainText('17 条弹幕已准备');
  await expect(page.locator('.source-notice')).toContainText('核对');
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-first-media')));
  await expect(page.locator('.app-footer')).toContainText('尚未载入弹幕');
  await expect(page.locator('.source-notice')).toContainText('播放器');
  await expect(page.locator('.source-notice')).not.toContainText('已载入');
  await expect(page.getByRole('button', { name: '加载这一集的弹幕' })).toBeEnabled();
});

test('temporary identity loss cannot strand a request when the same media returns', async ({ page }) => {
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-first-media')));
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-hold', { detail: 'search' })));
  await manualSearch(page);
  await expect(page.getByRole('button', { name: '取消当前请求' })).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-lost-media')));
  await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeEnabled();
  await expect(page.locator('.source-notice')).toContainText('暂');
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-first-media')));
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-finish-old')));
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(page.getByRole('button', { name: '取消当前请求' })).toHaveCount(0);
  await page.getByRole('button', { name: '使用播放器片名', exact: false }).click();
  await expect(page.getByLabel('当前分集')).toHaveValue('10');
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.locator('.app-footer')).toContainText('17 条弹幕已准备');
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('a same-turn identity loss and return cannot revive an earlier error', async ({ page }) => {
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-first-media')));
  await expect(page.getByRole('button', { name: '使用播放器片名', exact: false })).toBeEnabled();
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-hold', { detail: 'search' })));
  await manualSearch(page);
  await expect(page.getByRole('button', { name: '取消当前请求' })).toBeVisible();
  await page.evaluate(() => {
    window.dispatchEvent(new Event('fixture-lost-media'));
    window.dispatchEvent(new Event('fixture-first-media'));
    window.dispatchEvent(new Event('fixture-finish-old'));
  });
  await expect(page.getByRole('button', { name: '取消当前请求' })).toHaveCount(0);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('a same-turn identity loss and return cannot leave a held request busy', async ({ page }) => {
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-first-media')));
  await expect(page.getByRole('button', { name: '使用播放器片名', exact: false })).toBeEnabled();
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-hold', { detail: 'search' })));
  await manualSearch(page);
  await expect(page.getByRole('button', { name: '取消当前请求' })).toBeVisible();
  await page.evaluate(() => {
    window.dispatchEvent(new Event('fixture-lost-media'));
    window.dispatchEvent(new Event('fixture-first-media'));
  });
  await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeEnabled();
});

for (const action of [
  { command: 'search', button: '使用播放器片名' },
  { command: 'episodes', button: '载入分集' },
  { command: 'comments', button: '加载这一集的弹幕' },
]) {
  test(`a stale render cannot start ${action.command} after a new identity has arrived`, async ({ page }) => {
    await page.evaluate(() => window.dispatchEvent(new Event('fixture-first-media')));
    await expect(page.getByRole('button', { name: '使用播放器片名', exact: false })).toBeEnabled();
    if (action.command !== 'search') {
      await manualSearch(page);
      await expect(page.getByLabel('当前分集')).toHaveValue('10');
    }
    const before = await page.locator('html').getAttribute(`data-${action.command}`);
    await page.getByRole('button', { name: action.button, exact: false }).evaluate(button => {
      window.dispatchEvent(new Event('fixture-lost-media'));
      (button as HTMLButtonElement).click();
    });
    await expect(page.locator('.source-notice')).toContainText('暂不可用');
    expect(await page.locator('html').getAttribute(`data-${action.command}`)).toBe(before);
    await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeEnabled();
  });
}
