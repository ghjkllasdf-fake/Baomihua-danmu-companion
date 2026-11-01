import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { skinIds } from '../src/appearance';
import { connect, commands, emit, fixture, openUpdates } from './update-fixture';

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

test('an unavailable updater stays in a collapsed last module without falsely lighting a badge', async ({ page }) => {
  await connect(page, 'fluent', { ...fixture, state: 'unavailable', availableVersion: '', supported: false });
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  const panel = page.locator('.update-panel');
  expect(await panel.getAttribute('open')).toBeNull();
  await expect(page.locator('#more-settings > :last-child')).toHaveClass('update-panel');
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  await openUpdates(page);
  await expect(page.getByRole('switch', { name: '自动检查更新' })).toBeDisabled();
  await expect(page.getByRole('button', { name: '检查更新', exact: true })).toBeDisabled();
  await expect(panel).toContainText('不支持版本检查');
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

test('supported check frequencies round-trip inside the updater without losing preferences or invoking retired actions', async ({ page }, testInfo) => {
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
  await page.getByRole('combobox', { name: '更新通道' }).selectOption('stable');
  await expect(page.getByRole('combobox', { name: '更新通道' })).toHaveValue('stable');
  await expect(frequency).toBeEnabled();
  expect(await page.evaluate(() => (window as unknown as { updateFixture: { preferenceWrites: unknown[] } }).updateFixture.preferenceWrites.at(-1))).toEqual(
    { autoCheck: true, channel: 'stable', checkIntervalHours: 720 },
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
