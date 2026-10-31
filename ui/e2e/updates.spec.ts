import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';
import { skinIds } from '../src/appearance';
import type { UpdateSnapshot } from '../src/updates';

const fixture: UpdateSnapshot = { state: 'available', currentVersion: '0.1.1-beta.6', availableVersion: '0.1.1-beta.10', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, autoDownload: false, checkIntervalHours: 24, checkedAt: '2026-10-31T04:00:00Z', downloadedBytes: 0, totalBytes: 100,
  notes: '保留设置，改进更新恢复。\n<script>not executed</script>', message: '', revision: 1, supported: true, canInstall: false, lastResult: '' };

async function connect(page: Page, skin = 'fluent', state = fixture, failPreferences = false) {
  await page.addInitScript(({ settings, skin, state, failPreferences }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let snapshot = state, sequence = 0;
    const commands: string[] = [];
    const preferenceWrites: Record<string, unknown>[] = [];
    function emit(data: unknown) { callbacks.forEach(callback => callback({ data })); }
    Object.defineProperty(window, 'updateFixture', { value: { commands, preferenceWrites, emit: (update: unknown) => emit({ type: 'state', sequence: ++sequence, data: { update } }) } });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string; payload?: Record<string, unknown> }) => {
        commands.push(message.type);
        if (message.type === 'set-update-preferences' && failPreferences) {
          preferenceWrites.push({ ...message.payload });
          setTimeout(() => emit({ type: 'response', id: message.id, ok: false, error: '更新偏好保存失败' }), 10);
          return;
        }
        let data: unknown = snapshot;
        if (message.type === 'hello') data = { protocolVersion: 1, capabilities: ['media-context-v1', 'tray-flyout-v1', 'appearance-v1', 'github-update-v1'],
          apiBase: '', settings, settingsRevision: 0, stateSequence: 0, appearance: { skin, reduceTransparency: false, revision: 0, backdrop: 'solid', notice: '' }, update: snapshot };
        else if (message.type === 'set-update-preferences') {
          preferenceWrites.push({ ...message.payload });
          const channelChanged = message.payload?.channel !== snapshot.channel;
          snapshot = { ...snapshot, ...message.payload, ...(channelChanged ? { state: 'idle', availableVersion: '', downloadedBytes: 0, totalBytes: 0, notes: '', canInstall: false } : {}), revision: snapshot.revision + 1 };
          data = snapshot;
        }
        else if (message.type === 'download-update') { snapshot = { ...snapshot, state: 'ready', downloadedBytes: 100, canInstall: true, revision: snapshot.revision + 1 }; data = snapshot; }
        else if (message.type === 'install-update') data = { accepted: true };
        else if (message.type === 'open-update-page') data = { opened: true };
        setTimeout(() => emit({ type: 'response', id: message.id, ok: true, data }), 10);
      },
    } } });
  }, { settings: defaultSettings, skin, state, failPreferences });
  await page.goto('/');
  await expect(page.locator('.mini-app')).toBeVisible();
  if (state.availableVersion) await expect(page.locator('.footer-actions .update-indicator')).toBeVisible();
  else await expect(page.locator('.footer-actions .update-indicator')).toHaveCount(0);
}
async function commands(page: Page) { return page.evaluate(() => (window as unknown as { updateFixture: { commands: string[] } }).updateFixture.commands); }
async function emit(page: Page, value: unknown) { await page.evaluate(value => (window as unknown as { updateFixture: { emit: (value: unknown) => void } }).updateFixture.emit(value), value); }
async function openUpdates(page: Page) {
  const panel = page.locator('.update-panel');
  if (await panel.getAttribute('open') === null) await panel.locator(':scope > summary').click();
  await expect(panel).toHaveAttribute('open', '');
}

for (const skin of skinIds) for (const distribution of ['portable', 'installer'] as const) {
  test(`${skin}/${distribution}: ready and failed repair guidance matches the distribution`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 380, height: 540 });
    await connect(page, skin, { ...fixture, distribution, state: 'ready', downloadedBytes: 100, canInstall: true, notes: '' });
    await page.getByRole('button', { name: /^更多设置/ }).click();
    await openUpdates(page);
    const repairText = distribution === 'installer'
      ? '安装版：仅在覆盖失败或新版未正常启动时，才需用同版本完整安装器原位修复。'
      : '便携版：仅在覆盖失败或新版未正常启动时，才需用同版本完整 ZIP 解压到新空目录运行；不要混合覆盖外来文件。';
    for (const width of [380, 320]) {
      await page.setViewportSize({ width, height: 540 });
      await page.getByRole('button', { name: '重启并更新', exact: true }).click();
      const confirmation = page.getByRole('group', { name: '确认更新安装' });
      await expect(confirmation).toContainText('原位覆盖，不备份旧版本；个人设置保留。');
      const guidance = confirmation.getByText(repairText, { exact: true });
      await expect(guidance).toBeInViewport({ ratio: 1 });
      await expect(page.getByRole('button', { name: '确认重启并更新', exact: true })).toBeInViewport({ ratio: 1 });
      await expect(confirmation).not.toContainText(distribution === 'portable' ? '安装器' : 'ZIP');
      await expect(confirmation).not.toContainText('若有外来文件冲突');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`repair-ready-${width}-${skin}-${distribution}.png`) });
      await page.getByRole('button', { name: '暂不更新', exact: true }).click();
    }
    expect((await commands(page)).includes('install-update')).toBe(false);
    await emit(page, { ...fixture, distribution, state: 'failed', downloadedBytes: 100, canInstall: false,
      message: '新版健康检查未通过，更新未完成。', notes: '', revision: 2 });
    await expect(page.getByRole('group', { name: '确认更新安装' })).toHaveCount(0);
    for (const width of [380, 320]) {
      await page.setViewportSize({ width, height: 540 });
      const guidance = page.locator('.update-panel').getByText(repairText, { exact: true });
      await guidance.scrollIntoViewIfNeeded();
      await expect(guidance).toBeInViewport({ ratio: 1 });
      await expect(page.getByRole('alert')).toContainText('新版健康检查未通过');
      await expect(page.locator('.update-panel')).not.toContainText('已回滚');
      await expect(page.locator('.update-panel')).not.toContainText('Recover-Danmu');
      await expect(page.getByRole('button', { name: '重启并更新', exact: true })).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`repair-failed-${width}-${skin}-${distribution}.png`) });
    }
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
    expect(violations).toEqual([]);
  });
}

for (const distribution of ['portable', 'installer'] as const) for (const failure of ['check', 'download'] as const) {
  test(`${distribution}/${failure}: pre-write network failure retains retry without requiring repair`, async ({ page }, testInfo) => {
    const message = failure === 'check' ? '检查更新失败：网络连接不可用，请重试。' : '下载更新失败：连接中断，请重新下载。';
    await page.setViewportSize({ width: 380, height: 540 });
    await connect(page, 'fluent', { ...fixture, distribution, state: 'failed', message, notes: '',
      availableVersion: failure === 'check' ? '' : fixture.availableVersion,
      downloadedBytes: failure === 'check' ? 0 : 12, totalBytes: failure === 'check' ? 0 : 100, canInstall: false });
    await page.getByRole('button', { name: /^更多设置/ }).click();
    await openUpdates(page);
    await expect(page.getByRole('alert')).toHaveText(message);
    await expect(page.getByRole('status').filter({ hasText: '更新未完成，请重试' })).toBeVisible();
    const guidance = page.locator('.update-panel .update-muted').filter({ hasText: /同版本完整/ });
    await expect(guidance).toContainText('仅在覆盖失败或新版未正常启动时，才需');
    await expect(guidance).not.toContainText('如更新失败，请');
    await expect(guidance).not.toContainText(distribution === 'portable' ? '安装器' : 'ZIP');
    const retry = page.getByRole('button', { name: failure === 'check' ? '检查更新' : '下载更新', exact: true });
    for (const width of [380, 320]) {
      await page.setViewportSize({ width, height: 540 });
      await page.locator('.update-panel > .update-body > .update-actions').scrollIntoViewIfNeeded();
      await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`network-${failure}-${width}-${distribution}.png`) });
      await expect(guidance).toBeInViewport({ ratio: 1 });
      await expect(page.getByRole('alert')).toBeInViewport({ ratio: 1 });
      await expect(retry).toBeEnabled();
      await expect(retry).toBeInViewport({ ratio: 1 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    }
    await expect(page.getByRole('group', { name: '确认更新安装' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: '重启并更新', exact: true })).toHaveCount(0);
    expect((await commands(page)).some(type => ['download-update', 'install-update'].includes(type))).toBe(false);
    await retry.click();
    await expect.poll(async () => (await commands(page)).filter(type => type === (failure === 'check' ? 'check-update' : 'download-update')).length).toBe(1);
    expect((await commands(page)).includes('install-update')).toBe(false);
  });
}

for (const skin of skinIds) test(`${skin}: update module is last, independently collapsed and echoes the home corner badge`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 540 });
  await connect(page, skin);
  const homeBadge = await page.locator('.footer-actions .update-indicator').evaluate(element => {
    const style = getComputedStyle(element);
    return { color: style.color, background: style.backgroundColor, text: element.textContent };
  });
  await page.getByRole('button', { name: /^更多设置/ }).click();
  const panel = page.locator('details.update-panel'), summary = panel.locator(':scope > summary');
  await expect(panel).toHaveCount(1);
  expect(await panel.getAttribute('open')).toBeNull();
  await expect(page.locator('#more-settings > :last-child')).toHaveClass('update-panel');
  await expect(summary).toHaveAccessibleName('软件更新，有新版本 0.1.1-beta.10');
  await expect(summary).toBeInViewport({ ratio: 1 });
  expect((await summary.boundingBox())!.height).toBeLessThanOrEqual(40);
  expect((await panel.boundingBox())!.y).toBeGreaterThan((await page.locator('.manual-sync').boundingBox())!.y);
  await expect(page.getByRole('switch', { name: '自动检查更新', includeHidden: true })).toBeHidden();
  await expect(page.locator('.update-title .update-indicator')).toHaveText('新');
  expect(await page.locator('.update-title .update-indicator').evaluate(element => {
    const style = getComputedStyle(element);
    return { color: style.color, background: style.backgroundColor, text: element.textContent };
  })).toEqual(homeBadge);
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`module-collapsed-${skin}.png`) });
  await summary.focus();
  await summary.press('Enter');
  await expect(panel).toHaveAttribute('open', '');
  await expect(page.getByRole('switch', { name: '自动检查更新' })).toBeVisible();
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`module-expanded-${skin}.png`) });
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations).toEqual([]);
  await summary.press('Space');
  expect(await panel.getAttribute('open')).toBeNull();
  await expect(summary).toBeFocused();
  await expect(panel.locator('.update-indicator')).toBeVisible();
  await page.getByRole('button', { name: '返回', exact: true }).click();
  await page.getByRole('button', { name: /^更多设置/ }).click();
  expect(await panel.getAttribute('open')).toBeNull();
  await page.setViewportSize({ width: 320, height: 340 });
  await summary.scrollIntoViewIfNeeded();
  await expect(summary).toBeInViewport({ ratio: 1 });
  await expect(page.getByRole('button', { name: '返回', exact: true })).toBeInViewport({ ratio: 1 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await emit(page, { ...fixture, state: 'current', availableVersion: '', revision: 2 });
  await expect(summary).toHaveAccessibleName('软件更新');
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  expect((await commands(page)).filter(type => ['download-update', 'install-update'].includes(type))).toEqual([]);
});

for (const skin of skinIds) test(`${skin}: compact More Settings uses a legible New corner badge without changing footer height`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 560 });
  await connect(page, skin);
  for (const width of [320, 380, 768]) {
    await page.setViewportSize({ width, height: 560 });
    await expect(page.locator('.more-label')).toHaveCSS('font-size', '12px');
    await expect(page.locator('.more-label')).toHaveCSS('font-weight', '400');
    const indicator = page.locator('.footer-actions .update-indicator');
    const label = (await page.locator('.more-label').boundingBox())!, badge = (await indicator.boundingBox())!;
    expect(badge.y).toBeLessThan(label.y);
    expect(badge.x).toBeGreaterThan(label.x);
    expect(badge.x + badge.width).toBeLessThanOrEqual(label.x + label.width);
    expect(badge.width).toBeLessThanOrEqual(16);
    expect(badge.height).toBeLessThanOrEqual(16);
    await expect(indicator).toHaveCSS('background-color', skin === 'studio' ? 'rgb(255, 105, 113)' : 'rgb(196, 36, 42)');
    await expect(indicator).toHaveCSS('color', skin === 'studio' ? 'rgb(32, 30, 30)' : 'rgb(255, 255, 255)');
    await expect(indicator).toHaveCSS('font-size', '11px');
    await expect(indicator).toHaveText('新');
    await expect(indicator.locator('svg')).toHaveCount(0);
    await expect(indicator).toHaveAttribute('aria-hidden', 'true');
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
  await expect(page.locator('.footer-actions .update-indicator')).toBeVisible();
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
  await expect(page.locator('.update-title .update-indicator')).toBeVisible();
  await page.getByRole('button', { name: '返回', exact: true }).click();
  await expect(page.locator('.footer-actions .update-indicator')).toBeVisible();
  await page.getByRole('button', { name: /^更多设置/ }).click();
  await openUpdates(page);
  expect((await commands(page)).filter(type => ['download-update', 'install-update'].includes(type))).toEqual([]);
  await expect(page.getByRole('switch', { name: '自动下载更新' })).not.toBeChecked();
  await page.getByRole('switch', { name: '自动检查更新' }).click();
  await expect(page.getByRole('switch', { name: '自动检查更新' })).not.toBeChecked();
  await page.getByRole('button', { name: '下载更新', exact: true }).click();
  await page.getByRole('button', { name: '重启并更新', exact: true }).click();
  expect((await commands(page)).includes('install-update')).toBe(false);
  await expect(page.getByRole('group', { name: '确认更新安装' })).toContainText('播放器不受控制');
  await expect(page.getByRole('group', { name: '确认更新安装' })).toContainText('不备份旧版本');
  await expect(page.getByRole('group', { name: '确认更新安装' })).toContainText('不要混合覆盖外来文件');
  await expect(page.getByRole('group', { name: '确认更新安装' })).not.toContainText('Recover-Danmu');
  await expect(page.getByRole('button', { name: '确认重启并更新', exact: true })).toBeInViewport({ ratio: 1 });
  await page.getByRole('button', { name: '返回', exact: true }).click();
  await page.getByRole('button', { name: /^更多设置/ }).click();
  await openUpdates(page);
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
  await openUpdates(page);
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

test('an unavailable updater stays in a collapsed last module without falsely lighting a badge', async ({ page }) => {
  await connect(page, 'fluent', { ...fixture, state: 'unavailable', availableVersion: '', supported: false, totalBytes: 0 });
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  const panel = page.locator('.update-panel');
  expect(await panel.getAttribute('open')).toBeNull();
  await expect(page.locator('#more-settings > :last-child')).toHaveClass('update-panel');
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  await openUpdates(page);
  await expect(page.getByRole('switch', { name: '自动检查更新' })).toBeDisabled();
  await expect(page.getByRole('button', { name: '检查更新', exact: true })).toBeDisabled();
  await expect(panel).toContainText('不支持自动更新');
});

test('the collapsed updater remains a large touch target and its shared badge survives forced colours', async ({ browser }, testInfo) => {
  const context = await browser.newContext({ viewport: { width: 320, height: 540 }, isMobile: true, hasTouch: true });
  try {
    const page = await context.newPage();
    await connect(page, 'studio');
    expect((await page.locator('.more-link').boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await page.getByRole('button', { name: /^更多设置/ }).click();
    const summary = page.locator('.update-panel > summary');
    expect((await summary.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await summary.scrollIntoViewIfNeeded();
    await expect(summary).toBeInViewport({ ratio: 1 });
    await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
    await expect(summary.locator('.update-indicator')).toBeVisible();
    await expect(summary.locator('.update-indicator')).toHaveText('新');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath('module-touch-forced-colors.png') });
  } finally { await context.close(); }
});

test('legacy updater snapshots keep update actions working but cannot pretend to set a check frequency', async ({ page }) => {
  const legacy = { ...fixture };
  delete legacy.checkIntervalHours;
  await connect(page, 'fluent', legacy);
  await page.getByRole('button', { name: /^更多设置/ }).click();
  await openUpdates(page);
  const frequency = page.getByRole('combobox', { name: '检查频率', exact: true });
  await expect(frequency).toHaveValue('24');
  await expect(frequency).toBeDisabled();
  await expect(page.locator('#update-frequency-notice')).toContainText('宿主不支持设置频率');
  await page.getByRole('switch', { name: '自动下载更新' }).click();
  await expect(page.getByRole('switch', { name: '自动下载更新' })).toBeChecked();
  expect(await page.evaluate(() => (window as unknown as { updateFixture: { preferenceWrites: unknown[] } }).updateFixture.preferenceWrites)).toEqual([
    { autoCheck: true, autoDownload: true, channel: 'beta' },
  ]);
  expect((await commands(page)).filter(type => ['download-update', 'install-update'].includes(type))).toEqual([]);
});

test('supported check frequencies round-trip inside the updater without losing other preferences or downloading', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 540 });
  await connect(page, 'glass', { ...fixture, checkIntervalHours: 24 });
  await page.getByRole('button', { name: /^更多设置/ }).click();
  await openUpdates(page);
  const frequency = page.getByRole('combobox', { name: '检查频率', exact: true });
  await expect(frequency).toBeEnabled();
  await expect(frequency.locator('option')).toHaveText(['每天', '每周', '每月']);
  for (const interval of [24, 168, 720]) {
    await frequency.selectOption(String(interval));
    await expect(frequency).toHaveValue(String(interval));
    await expect(frequency).toBeEnabled();
  }
  await expect(page.getByText('每月按 30 天检查一次。')).toBeVisible();
  await page.getByRole('switch', { name: '自动检查更新' }).click();
  await expect(page.getByRole('switch', { name: '自动检查更新' })).not.toBeChecked();
  await expect(frequency).toBeDisabled();
  await expect(frequency).toHaveValue('720');
  await page.getByRole('switch', { name: '自动检查更新' }).click();
  await expect(frequency).toBeEnabled();
  await page.getByRole('switch', { name: '自动下载更新' }).click();
  await expect(page.getByRole('switch', { name: '自动下载更新' })).toBeChecked();
  await page.getByRole('combobox', { name: '更新通道' }).selectOption('stable');
  await expect(page.getByRole('combobox', { name: '更新通道' })).toHaveValue('stable');
  await expect(frequency).toBeEnabled();
  expect(await page.evaluate(() => (window as unknown as { updateFixture: { preferenceWrites: unknown[] } }).updateFixture.preferenceWrites.at(-1))).toEqual(
    { autoCheck: true, autoDownload: true, channel: 'stable', checkIntervalHours: 720 },
  );
  await page.locator('.update-panel > summary').click();
  await openUpdates(page);
  await expect(frequency).toHaveValue('720');
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath('module-frequency-glass.png') });
  expect((await commands(page)).filter(type => ['download-update', 'install-update'].includes(type))).toEqual([]);
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations).toEqual([]);
});

test('a rejected frequency preference restores the confirmed selection and keeps an explicit error', async ({ page }) => {
  await connect(page, 'fluent', { ...fixture, checkIntervalHours: 24 }, true);
  await page.getByRole('button', { name: /^更多设置/ }).click();
  await openUpdates(page);
  const frequency = page.getByRole('combobox', { name: '检查频率', exact: true });
  await frequency.selectOption('168');
  await expect(page.getByRole('alert')).toHaveText('更新偏好保存失败');
  await expect(frequency).toHaveValue('24');
  await expect(frequency).toBeEnabled();
});
