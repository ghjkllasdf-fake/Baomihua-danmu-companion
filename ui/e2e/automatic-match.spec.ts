import { test, expect, type Page } from './fixtures/preview-test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings, emptyPlayer } from '../src/model';
import type { AutomaticMatchStatus } from '../src/automatic-match';

const manualMessage = '三轮自动匹配未找到弹幕，请手动搜索或换源。';

async function manualSearchFixture(page: Page, skin = 'fluent', apiBase = 'http://fixture.invalid/token') {
  await page.addInitScript(({ settings, empty, skin, apiBase, message: failure }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let sequence = 0, mediaRevision = 1, title = '当前作品';
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    const state = (automaticMatch?: AutomaticMatchStatus) => {
      sequence++;
      emit({ type: 'state', sequence, data: {
        player: { ...empty, sequence, mediaRevision, title, episodeNumber: 2 },
        ...(automaticMatch ? { automaticMatch } : {}),
      } });
    };
    window.addEventListener('fixture-match-state', event => state((event as CustomEvent<AutomaticMatchStatus>).detail));
    window.addEventListener('fixture-next-media', () => { mediaRevision++; title = '下一部作品'; state(); });
    window.addEventListener('fixture-stop-playback', () => { mediaRevision++; title = ''; state(); });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (command: { id: string; type: string }) => {
        const appearance = { skin, reduceTransparency: false, revision: 0, backdrop: 'solid', notice: '' };
        const data = command.type === 'hello' ? { protocolVersion: 1,
          capabilities: ['media-context-v1', 'automatic-match-v1', 'desktop-lifecycle-v1', 'tray-flyout-v1', 'appearance-v1'],
          apiBase, settings, settingsRevision: 0, stateSequence: sequence, appearance } :
          command.type === 'get-settings' ? { settings, settingsRevision: 0 } :
          command.type === 'get-startup' ? { enabled: false } :
          command.type === 'get-appearance' ? appearance : { animes: [] };
        setTimeout(() => {
          emit({ type: 'response', id: command.id, ok: true, data });
          if (command.type === 'hello') state({ state: 'failed', message: failure, sourceTitle: '' });
          if (command.type === 'search') state({ state: 'manual', message: '正在手动处理弹幕来源。', sourceTitle: '' });
        }, 10);
      },
    } } });
  }, { settings: defaultSettings, empty: emptyPlayer, skin, apiBase, message: manualMessage });
}

async function matchState(page: Page, state: AutomaticMatchStatus['state']) {
  await page.evaluate(({ state, message }) => window.dispatchEvent(new CustomEvent('fixture-match-state', {
    detail: { state, message, sourceTitle: '' },
  })), { state, message: manualMessage });
}

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
  await expect(page.getByRole('heading', { name: '爆米花弹幕面板' })).toHaveCount(1);
  await expect(page.locator('.source-view')).toBeHidden();
  await expect(page.locator('.manual-source-link')).not.toHaveClass(/needs-attention/);
  await page.getByLabel('API 地址', { exact: true }).fill('http://fixture.invalid/token');
  await page.getByRole('button', { name: '保存地址', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-last-command', 'save-api');
  await expect(page.locator('.mini-footer')).toContainText('23 条弹幕已准备');
  await expect(page.locator('.match-status')).toContainText('已自动匹配');
  await expect(page.locator('.match-status')).toContainText('来源：当前作品(2025)源A');
  await page.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
  await expect(page.locator('.source-notice')).toContainText('已自动匹配');
  await expect(page.locator('.source-notice')).toContainText('源A');
  await expect(page.locator('.playback-strip')).not.toContainText('已自动同步');
});

for (const skin of ['fluent', 'glass', 'winui', 'studio']) {
  test(`${skin} makes a required manual search prominent without stealing focus or overflowing`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 380, height: 540 });
    await manualSearchFixture(page, skin);
    await page.goto('/');
    await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
    await expect(page.locator('.match-status')).toHaveText(manualMessage);
    const button = page.getByRole('button', { name: '手动搜索 / 换源', exact: true });
    await page.screenshot({ path: testInfo.outputPath('manual-search-required.png') });
    await expect(button).toHaveClass(/needs-attention/);
    await expect(button).toHaveAccessibleDescription(manualMessage);
    await expect(button.locator('.manual-source-prompt')).toHaveText('需处理');
    await expect(page.locator('.source-view')).toBeHidden();
    await expect(button).not.toBeFocused();
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    expect(axe.violations).toEqual([]);
    await page.setViewportSize({ width: 320, height: 540 });
    await expect(button).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('manual-search-required-narrow.png') });
    await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
    await expect(button.locator('.manual-source-prompt')).toBeVisible();
    await button.focus();
    await button.press('Enter');
    await expect(page.getByRole('heading', { name: '手动搜索', exact: true })).toBeFocused();
    await expect(button).toHaveClass(/needs-attention/);
    await page.getByRole('button', { name: '返回设置', exact: true }).click();
    await expect(button).toHaveClass(/needs-attention/);
  });
}

test('manual attention clears for progress, success, actual manual work and a new media context, but not just opening the page', async ({ page }) => {
  await manualSearchFixture(page);
  await page.goto('/');
  const button = page.getByRole('button', { name: '手动搜索 / 换源', exact: true });
  await expect(button).toHaveClass(/needs-attention/);
  for (const status of ['waiting', 'matching', 'loading', 'loaded', 'manual'] as const) {
    await matchState(page, status);
    await expect(button).not.toHaveClass(/needs-attention/);
    await expect(button).not.toHaveAttribute('aria-describedby');
    await expect(button.locator('.manual-source-prompt')).toHaveCount(0);
    await matchState(page, 'failed');
    await expect(button).toHaveClass(/needs-attention/);
  }
  await button.click();
  await page.getByRole('button', { name: '搜索弹幕来源', exact: true }).click();
  await expect(button).not.toHaveClass(/needs-attention/);
  await matchState(page, 'failed');
  await expect(button).toHaveClass(/needs-attention/);
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-next-media')));
  await expect(page.locator('.playback-title')).toContainText('下一部作品');
  await expect(button).not.toHaveClass(/needs-attention/);
  await matchState(page, 'failed');
  await expect(button).toHaveClass(/needs-attention/);
  await page.evaluate(() => window.dispatchEvent(new Event('fixture-stop-playback')));
  await expect(page.locator('.playback-title')).toContainText('等待爆米花开始播放');
  await expect(button).not.toHaveClass(/needs-attention/);
});

test('an unconfigured service does not misleadingly promote manual source search', async ({ page }) => {
  await manualSearchFixture(page, 'fluent', '');
  await page.goto('/');
  await expect(page.locator('.match-status')).toHaveText(manualMessage);
  await expect(page.locator('.manual-source-link')).not.toHaveClass(/needs-attention/);
});

test('required manual search keeps a large touch target', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  try {
    const page = await context.newPage();
    await manualSearchFixture(page);
    await page.goto('/');
    const button = page.getByRole('button', { name: '手动搜索 / 换源', exact: true });
    await expect(button).toHaveClass(/needs-attention/);
    expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  } finally { await context.close(); }
});

test('all isolated preview source types use rolling entry without fixed rows', async ({ page, previewUrl }) => {
  await page.goto(previewUrl);
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
