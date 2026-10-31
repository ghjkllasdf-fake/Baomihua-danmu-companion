import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';
import { skinIds } from '../src/appearance';

const fixture = { state: 'available', currentVersion: '0.1.1-beta.6', availableVersion: '0.1.1-beta.10', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, autoDownload: false, checkedAt: '2026-10-31T04:00:00Z', downloadedBytes: 0, totalBytes: 100,
  notes: '保留设置，改进更新恢复。\n<script>not executed</script>', message: '', revision: 1, supported: true, canInstall: false, lastResult: '' };

async function connect(page: Page, skin = 'fluent', state = fixture) {
  await page.addInitScript(({ settings, skin, state }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let snapshot = state, sequence = 0;
    const commands: string[] = [];
    function emit(data: unknown) { callbacks.forEach(callback => callback({ data })); }
    Object.defineProperty(window, 'updateFixture', { value: { commands, emit: (update: unknown) => emit({ type: 'state', sequence: ++sequence, data: { update } }) } });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string; payload?: Record<string, unknown> }) => {
        commands.push(message.type);
        let data: unknown = snapshot;
        if (message.type === 'hello') data = { protocolVersion: 1, capabilities: ['media-context-v1', 'tray-flyout-v1', 'appearance-v1', 'github-update-v1'],
          apiBase: '', settings, settingsRevision: 0, stateSequence: 0, appearance: { skin, reduceTransparency: false, revision: 0, backdrop: 'solid', notice: '' }, update: snapshot };
        else if (message.type === 'set-update-preferences') { snapshot = { ...snapshot, ...message.payload, revision: snapshot.revision + 1 }; data = snapshot; }
        else if (message.type === 'download-update') { snapshot = { ...snapshot, state: 'ready', downloadedBytes: 100, canInstall: true, revision: snapshot.revision + 1 }; data = snapshot; }
        else if (message.type === 'install-update') data = { accepted: true };
        else if (message.type === 'open-update-page') data = { opened: true };
        setTimeout(() => emit({ type: 'response', id: message.id, ok: true, data }), 10);
      },
    } } });
  }, { settings: defaultSettings, skin, state });
  await page.goto('/');
  await expect(page.locator('.update-indicator')).toBeVisible();
}
async function commands(page: Page) { return page.evaluate(() => (window as unknown as { updateFixture: { commands: string[] } }).updateFixture.commands); }
async function emit(page: Page, value: unknown) { await page.evaluate(value => (window as unknown as { updateFixture: { emit: (value: unknown) => void } }).updateFixture.emit(value), value); }

for (const skin of skinIds) test(`${skin}: compact More Settings uses an aligned SVG update badge without changing footer height`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 560 });
  await connect(page, skin);
  for (const width of [320, 380, 768]) {
    await page.setViewportSize({ width, height: 560 });
    await expect(page.locator('.more-label')).toHaveCSS('font-size', '12px');
    await expect(page.locator('.more-label')).toHaveCSS('font-weight', '400');
    const label = (await page.locator('.more-label').boundingBox())!, badge = (await page.locator('.update-indicator').boundingBox())!;
    expect(Math.abs(badge.y + badge.height / 2 - label.y - label.height / 2)).toBeLessThanOrEqual(1);
    expect(badge.x).toBeGreaterThan(label.x);
    expect(badge.x + badge.width).toBeLessThanOrEqual(label.x + label.width);
    expect(badge.width).toBeLessThanOrEqual(16);
    expect(badge.height).toBeLessThanOrEqual(16);
    await expect(page.locator('.update-indicator')).toHaveCSS('color', skin === 'studio' ? 'rgb(255, 105, 113)' : 'rgb(196, 36, 42)');
    await expect(page.locator('.update-indicator svg')).toHaveCount(1);
    await expect(page.locator('.update-indicator')).toHaveAttribute('aria-hidden', 'true');
    await expect(page.locator('.more-chevron')).toHaveCount(1);
    expect((await page.locator('.more-chevron').boundingBox())!.width).toBeLessThanOrEqual(12);
    await expect(page.getByRole('button', { name: '更多设置，有新版本 0.1.1-beta.10', exact: true })).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`home-${width}-${skin}.png`) });
  }
  await page.setViewportSize({ width: 380, height: 560 });
  const height = (await page.locator('.mini-app').boundingBox())!.height;
  const footerHeight = (await page.locator('.mini-footer').boundingBox())!.height;
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
  expect(violations).toEqual([]);
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  await expect(page.locator('.update-indicator svg')).toBeVisible();
  await expect(page.locator('.more-chevron')).toBeVisible();
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`forced-colors-${skin}.png`) });
  await page.emulateMedia({ forcedColors: 'none', reducedMotion: 'no-preference' });
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`home-${skin}.png`) });
  await page.getByRole('button', { name: /^更多设置/ }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('button', { name: '返回', exact: true })).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('.more-label')).toHaveCSS('font-size', '12px');
  await expect(page.locator('.more-chevron')).toHaveAttribute('data-direction', 'back');
  await expect(page.locator('.more-chevron')).toHaveCSS('transform', 'matrix(-1, 0, 0, -1, 0, 0)');
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`return-${skin}.png`) });
  await page.getByRole('button', { name: '返回', exact: true }).click();
  expect((await commands(page)).filter(type => ['download-update', 'install-update'].includes(type))).toEqual([]);
  await emit(page, { ...fixture, state: 'current', availableVersion: '', revision: 2 });
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  expect((await page.locator('.mini-app').boundingBox())!.height).toBe(height);
  expect((await page.locator('.mini-footer').boundingBox())!.height).toBe(footerHeight);
  await expect(page.getByRole('button', { name: '更多设置', exact: true })).toBeVisible();
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`no-update-${skin}.png`) });
});

test('opening settings never downloads; opt-in settings and two-step install remain explicit', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 560 }); await connect(page);
  await page.getByRole('button', { name: /^更多设置/ }).click();
  await expect(page.getByRole('heading', { name: '更多设置', exact: true })).toBeFocused();
  await expect(page.getByRole('button', { name: '返回', exact: true })).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  await page.getByRole('button', { name: '返回', exact: true }).click();
  await expect(page.locator('.update-indicator')).toHaveCount(1);
  await page.getByRole('button', { name: /^更多设置/ }).click();
  expect((await commands(page)).filter(type => ['download-update', 'install-update'].includes(type))).toEqual([]);
  await expect(page.getByRole('switch', { name: '自动下载更新' })).not.toBeChecked();
  await page.getByRole('switch', { name: '自动检查更新' }).click();
  await expect(page.getByRole('switch', { name: '自动检查更新' })).not.toBeChecked();
  await page.getByRole('button', { name: '下载更新', exact: true }).click();
  await page.getByRole('button', { name: '重启并更新', exact: true }).click();
  expect((await commands(page)).includes('install-update')).toBe(false);
  await expect(page.getByRole('group', { name: '确认更新安装' })).toContainText('播放器不受控制');
  await expect(page.getByRole('button', { name: '确认重启并更新', exact: true })).toBeInViewport({ ratio: 1 });
  await page.getByRole('button', { name: '返回', exact: true }).click();
  await page.getByRole('button', { name: /^更多设置/ }).click();
  await page.getByRole('button', { name: '重启并更新', exact: true }).click();
  await expect(page.getByRole('button', { name: '确认重启并更新', exact: true })).toBeInViewport({ ratio: 1 });
  expect((await commands(page)).includes('install-update')).toBe(false);
  await page.screenshot({ path: testInfo.outputPath('update-confirm.png'), fullPage: true });
  await page.getByRole('button', { name: '确认重启并更新', exact: true }).click();
  await expect.poll(async () => (await commands(page)).filter(type => type === 'install-update').length).toBe(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
  expect(violations.filter(value => value.impact === 'critical' || value.impact === 'serious')).toEqual([]);
});

test('stale and forged snapshots cannot revoke newer readiness or create an install action', async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: /^更多设置/ }).click();
  await emit(page, { ...fixture, state: 'ready', downloadedBytes: 100, canInstall: true, revision: 5 });
  await expect(page.getByRole('button', { name: '重启并更新', exact: true })).toBeEnabled();
  await emit(page, { ...fixture, state: 'current', availableVersion: '', revision: 2 });
  await expect(page.getByRole('button', { name: '重启并更新', exact: true })).toBeEnabled();
  await emit(page, { ...fixture, state: 'ready', canInstall: true, revision: 6 });
  await expect(page.getByRole('alert')).toContainText('更新状态数据无效');
  await emit(page, { ...fixture, state: 'downloading', downloadedBytes: 50, revision: 7 });
  await expect(page.getByRole('progressbar', { name: '更新下载进度' })).toHaveAttribute('value', '50');
  await expect(page.getByRole('button', { name: '重启并更新', exact: true })).toHaveCount(0);
});
