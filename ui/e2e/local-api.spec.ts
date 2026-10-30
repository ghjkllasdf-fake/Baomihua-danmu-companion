import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';

async function desktop(page: Page, options: { failed?: boolean; invalid?: boolean; starting?: boolean; transitionDuringSave?: boolean; holdSave?: boolean } = {}) {
  await page.addInitScript(({ settings, options }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    const ids = ['douban', '360', 'tencent', 'youku', 'iqiyi', 'imgo', 'bilibili', 'migu', 'renren', 'hanjutv', 'sohu', 'leshi', 'xigua', 'maiduidui', 'aiyifan', 'hongguo', 'bahamut', 'animeko'];
    const defaults = ['douban', '360', 'renren', 'hanjutv'];
    let sources = defaults.slice(), state = options.failed ? 'failed' : options.starting ? 'starting' : 'ready', sequence = 0, writes: string[][] = [], helloCount = 0, failNext = false;
    let pendingSave: (() => void) | undefined;
    const localApi = () => ({ enabled: true, state, message: state === 'failed' ? '本地服务启动失败。' : state === 'starting' ? '正在启动。' : '本地服务已就绪。', sources, availableSources: ids.map(id => ({ id, label: id })) });
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    Object.assign(window, { localFixture: {
      writes: () => writes, helloCount: () => helloCount,
      failNext: () => { failNext = true; },
      ready: () => { state = 'ready'; emit({ type: 'state', sequence: ++sequence, data: { localApi: localApi() } }); },
      broadcast: (next: unknown) => emit({ type: 'state', sequence: ++sequence, data: { localApi: next } }),
      view: () => localApi(),
      finishSave: () => { const reply = pendingSave; pendingSave = undefined; reply?.(); },
    } });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string; payload: { sources?: string[] } }) => {
        let response: object = { ok: false, error: 'unsupported fixture command' };
        if (message.type === 'hello') {
          helloCount++;
          response = { ok: true, data: { apiBase: 'http://127.0.0.1:12345/FAKE_TOKEN_SHOULD_NOT_RENDER', settings, settingsRevision: 0, stateSequence: sequence,
            protocolVersion: 1, capabilities: ['media-context-v1', 'embedded-api-v1'], localApi: options.invalid ? { ...localApi(), token: 'FAKE_TOKEN_SHOULD_NOT_RENDER' } : localApi() } };
        } else if (message.type === 'set-local-sources') {
          writes.push(message.payload.sources!.slice());
          if (failNext) { failNext = false; response = { ok: false, error: 'http://127.0.0.1:12345/FAKE_TOKEN_SHOULD_NOT_RENDER' }; }
          else {
            sources = message.payload.sources!.slice();
            if (options.transitionDuringSave) {
              state = 'starting'; emit({ type: 'state', sequence: ++sequence, data: { localApi: localApi() } });
              state = 'ready'; emit({ type: 'state', sequence: ++sequence, data: { localApi: localApi() } });
            }
            state = 'ready'; response = { ok: true, data: localApi() };
          }
        } else if (message.type === 'get-local-api') response = { ok: true, data: localApi() };
        const reply = () => emit({ type: 'response', id: message.id, ...response });
        if (options.holdSave && message.type === 'set-local-sources') pendingSave = reply;
        else setTimeout(reply, 20);
      },
    } } });
  }, { settings: defaultSettings, options });
  await page.goto('/');
}

type Fixture = { writes(): string[][]; helloCount(): number; failNext(): void; ready(): void; broadcast(next: unknown): void; view(): unknown; finishSave(): void };


test('embedded mode edits ordered sources only on apply, restores defaults, and refreshes the handshake', async ({ page }) => {
  await desktop(page);
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(page.getByText('本地服务已就绪。')).toBeVisible();
  await expect(page.getByLabel('API 地址', { exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('FAKE_TOKEN_SHOULD_NOT_RENDER');
  await expect(page.locator('body')).not.toContainText('127.0.0.1');
  await page.getByText('选择弹幕来源', { exact: false }).click();
  const list = page.locator('.local-source-list li');
  await expect(list).toHaveCount(18);
  await expect(list.nth(0)).toContainText('douban');
  await page.getByRole('checkbox', { name: '启用 tencent' }).check();
  await page.getByRole('button', { name: '上移 tencent' }).click();
  await page.getByRole('checkbox', { name: '启用 360' }).uncheck();
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([]);
  await page.getByRole('button', { name: '应用来源' }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([['douban', 'renren', 'tencent', 'hanjutv']]);
  await expect.poll(() => page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.helloCount())).toBe(2);
  await expect(page.getByRole('button', { name: '已应用' })).toBeDisabled();
  await page.getByRole('button', { name: '恢复默认' }).click();
  await page.getByRole('button', { name: '应用来源' }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([['douban', 'renren', 'tencent', 'hanjutv'], ['douban', '360', 'renren', 'hanjutv']]);
  for (const id of ['douban', '360', 'renren', 'hanjutv']) await page.getByRole('checkbox', { name: `启用 ${id}` }).uncheck();
  await expect(page.getByText('请至少启用一个来源。')).toBeVisible();
  await expect(page.getByRole('button', { name: '应用来源' })).toBeDisabled();
  await page.getByRole('button', { name: '恢复默认' }).click();
  await expect(page.getByRole('button', { name: '已应用' })).toBeDisabled();
});
test('failure permits explicit same-list retry, preserves failed edits, validates broadcasts and never retries automatically', async ({ page }) => {
  await desktop(page, { failed: true });
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(page.getByText('本地服务启动失败。')).toBeVisible();
  await page.getByText('选择弹幕来源', { exact: false }).click();
  await page.waitForTimeout(500);
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([]);
  await page.getByRole('button', { name: '重试启动' }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([['douban', '360', 'renren', 'hanjutv']]);
  await expect(page.getByText('本地服务已就绪。')).toBeVisible();
  await page.getByRole('checkbox', { name: '启用 tencent' }).check();
  await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.failNext());
  await page.getByRole('button', { name: '应用来源' }).click();
  await expect(page.getByRole('alert')).toContainText('来源设置未确认');
  await expect(page.getByRole('checkbox', { name: '启用 tencent' })).toBeChecked();
  await expect(page.locator('body')).not.toContainText('FAKE_TOKEN_SHOULD_NOT_RENDER');
  await page.evaluate(() => { const f = (window as unknown as { localFixture: Fixture }).localFixture; f.broadcast({ ...(f.view() as object), state: 'starting', message: '正在启动。' }); });
  await expect(page.getByText('正在启动。')).toBeVisible();
  await expect(page.getByRole('checkbox', { name: '启用 tencent' })).toBeChecked();
  await page.evaluate(() => { const f = (window as unknown as { localFixture: Fixture }).localFixture; f.broadcast({ ...(f.view() as object), state: 'bogus', message: 'FAKE_TOKEN_SHOULD_NOT_RENDER' }); });
  await expect(page.getByText('正在启动。')).toBeVisible();
  await expect(page.locator('body')).not.toContainText('FAKE_TOKEN_SHOULD_NOT_RENDER');
});

test('an invalid embedded hello fails closed', async ({ page }) => {
  await desktop(page, { invalid: true });
  await expect(page.getByRole('alert')).toContainText('握手数据不兼容');
  await expect(page.getByText('选择弹幕来源', { exact: false })).toHaveCount(0);
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([]);
});

test('refreshes the hello once on ready without restarting and avoids racing a source write', async ({ page }) => {
  await desktop(page, { starting: true, transitionDuringSave: true });
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(page.getByText('正在启动。')).toBeVisible();
  await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.ready());
  await expect.poll(() => page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.helloCount())).toBe(2);
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([]);
  await page.getByText('选择弹幕来源', { exact: false }).click();
  await page.getByRole('checkbox', { name: '启用 tencent' }).check();
  await page.getByRole('button', { name: '应用来源' }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.helloCount())).toBe(3);
  await expect(page.getByRole('button', { name: '已应用' })).toBeDisabled();
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.helloCount())).toBe(3);
});

test('the local source picker is keyboard accessible and fits a narrow screen', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 900 });
  await desktop(page);
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(page.getByText('本地服务已就绪。')).toBeVisible();
  const disclosure = page.locator('.local-sources > summary');
  await disclosure.focus(); await page.keyboard.press('Enter');
  await expect(page.locator('.local-sources')).toHaveAttribute('open', '');
  const toggle = page.getByRole('checkbox', { name: '启用 tencent' });
  await toggle.focus(); await page.keyboard.press('Space');
  await expect(toggle).toBeChecked();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
  expect(violations.filter(value => ['critical', 'serious'].includes(value.impact ?? '')).map(value => value.id)).toEqual([]);
  await page.screenshot({ path: 'test-results/local-api-mobile.png', fullPage: true });
});

test('bulk source selection keeps the chosen priority, saves only on apply, and blocks an empty submission', async ({ page }) => {
  await desktop(page);
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await page.locator('.local-sources > summary').click();
  await page.getByRole('button', { name: '上移 360', exact: true }).click();
  const expected = await page.locator('.local-source-list label span').allTextContents();
  expect(expected.slice(0, 4)).toEqual(['360', 'douban', 'renren', 'hanjutv']);
  const all = page.getByRole('button', { name: '全选', exact: true });
  const none = page.getByRole('button', { name: '全不选', exact: true });
  await expect(all).toBeVisible();
  await all.click();
  await expect(page.locator('.local-source-list input:checked')).toHaveCount(18);
  await expect(page.locator('.local-sources > summary')).toContainText('已选 18 个');
  await expect(all).toBeDisabled();
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([]);
  await page.getByRole('button', { name: '应用来源', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([expected]);
  await expect(page.getByRole('button', { name: '已应用', exact: true })).toBeDisabled();
  await none.click();
  await expect(page.locator('.local-source-list input:checked')).toHaveCount(0);
  await expect(page.locator('.local-sources > summary')).toContainText('已选 0 个');
  await expect(page.getByText('请至少启用一个来源。', { exact: true })).toBeInViewport({ ratio: 1 });
  await expect(page.getByRole('button', { name: '应用来源', exact: true })).toBeDisabled();
  await expect(none).toBeDisabled();
  await expect(all).toBeEnabled();
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([expected]);
  await page.getByRole('button', { name: '恢复默认', exact: true }).click();
  await expect(page.locator('.local-source-list input:checked')).toHaveCount(4);
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([expected]);
});

test('bulk source actions use the current catalogue and stay locked while an apply is pending', async ({ page }) => {
  await desktop(page, { holdSave: true });
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await page.locator('.local-sources > summary').click();
  await page.evaluate(() => {
    const fixture = (window as unknown as { localFixture: Fixture }).localFixture;
    fixture.broadcast({ ...(fixture.view() as object), sources: ['tencent'],
      availableSources: ['tencent', 'douban', '360'].map(id => ({ id, label: id })) });
  });
  await expect(page.locator('.local-source-list input')).toHaveCount(3);
  const all = page.getByRole('button', { name: '全选', exact: true });
  const none = page.getByRole('button', { name: '全不选', exact: true });
  await expect(all).toBeVisible();
  await all.click();
  await expect(page.locator('.local-source-list input:checked')).toHaveCount(3);
  await page.getByRole('checkbox', { name: '启用 360', exact: true }).uncheck();
  await expect(all).toBeEnabled();
  await page.getByRole('button', { name: '应用来源', exact: true }).click();
  await expect(page.getByRole('button', { name: '应用中…', exact: true })).toBeDisabled();
  await expect(all).toBeDisabled();
  await expect(none).toBeDisabled();
  await expect(page.getByRole('checkbox', { name: '启用 tencent', exact: true })).toBeDisabled();
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([['tencent', 'douban']]);
  await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.finishSave());
  await expect(page.getByRole('button', { name: '已应用', exact: true })).toBeDisabled();
  await expect(all).toBeEnabled();
  await expect(none).toBeEnabled();
});

test('bulk source controls work by keyboard in every skin and keep large touch targets', async ({ page, browser }, testInfo) => {
  await page.setViewportSize({ width: 320, height: 540 });
  await desktop(page);
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await page.locator('.local-sources > summary').click();
  for (const [skin, label] of [['fluent', 'A · 原生清爽'], ['glass', 'B · 流光玻璃'], ['winui', 'C · WinUI'], ['studio', 'D · 石墨工作室']]) {
    await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
    await page.getByRole('button', { name: label, exact: true }).click();
    await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
    await page.getByRole('button', { name: '返回设置', exact: true }).click();
    await page.getByRole('button', { name: '更多设置', exact: true }).click();
    await expect(page.locator('.local-sources')).toHaveAttribute('open', '');
    const all = page.getByRole('button', { name: '全选', exact: true });
    const none = page.getByRole('button', { name: '全不选', exact: true });
    await expect(all).toBeVisible();
    await all.focus(); await all.press('Enter');
    await expect(page.locator('.local-source-list input:checked')).toHaveCount(18);
    await none.focus(); await none.press('Enter');
    await expect(page.locator('.local-source-list input:checked')).toHaveCount(0);
    await expect(page.getByText('请至少启用一个来源。', { exact: true })).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
    expect(violations).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`${skin}-bulk-empty.png`) });
    await all.focus(); await all.press('Enter');
    await page.screenshot({ path: testInfo.outputPath(`${skin}-bulk-all.png`) });
    await none.click();
  }
  expect(await page.evaluate(() => (window as unknown as { localFixture: Fixture }).localFixture.writes())).toEqual([]);
  const touch = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  try {
    const mobile = await touch.newPage();
    await desktop(mobile);
    await mobile.getByRole('button', { name: '更多设置', exact: true }).click();
    await mobile.locator('.local-sources > summary').click();
    for (const name of ['全选', '全不选']) {
      const button = mobile.getByRole('button', { name, exact: true });
      expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
      await button.tap();
    }
    await expect(mobile.locator('.local-source-list input:checked')).toHaveCount(0);
  } finally { await touch.close(); }
});
