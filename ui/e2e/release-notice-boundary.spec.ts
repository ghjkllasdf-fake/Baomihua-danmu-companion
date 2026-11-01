import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { releasePageRoot } from '../src/updates';
import { commands, connect, emit, fixture, openUpdates, requests } from './update-fixture';

const retiredActions = ['download-update', 'install-update', 'prepare-portable', 'close', 'cancel', 'manual-clock'];
for (const distribution of ['portable', 'installer'] as const) test(`${distribution}: notification offers only project Release and honest manual instructions`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 540 });
  await connect(page, 'glass', { ...fixture, distribution, releasePage: `${releasePageRoot}/tag/v${fixture.availableVersion}` });
  await page.getByRole('button', { name: /^更多设置/ }).click(); await openUpdates(page);
  const panel = page.locator('.update-panel');
  await expect(panel).toContainText('程序仅检查版本；请自行下载并安装或解压。');
  await expect(panel).toContainText('更新前请退出弹幕伴侣，个人设置无需清除。');
  await expect(panel.getByRole('progressbar')).toHaveCount(0);
  await expect(panel.getByRole('group', { name: '确认更新安装' })).toHaveCount(0);
  await expect(panel.getByRole('switch', { name: '自动下载更新' })).toHaveCount(0);
  await expect(panel.getByRole('button', { name: /下载更新|重启|准备免安装|恢复/ })).toHaveCount(0);
  for (const width of [380, 320]) {
    await page.setViewportSize({ width, height: 540 });
    await panel.locator('.update-actions').scrollIntoViewIfNeeded();
    await expect(panel.getByRole('button', { name: '查看 Release', exact: true })).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`notice-${width}-${distribution}.png`) });
  }
  const before = page.url();
  await panel.getByRole('button', { name: '查看 Release', exact: true }).click();
  await expect.poll(async () => (await requests(page)).filter(value => value.type === 'open-update-page').length).toBe(1);
  expect((await requests(page)).find(value => value.type === 'open-update-page')!.payload).toEqual({});
  expect(page.url()).toBe(before);
  await panel.getByText('更新内容', { exact: true }).click();
  await expect(panel.locator('.update-notes pre')).toContainText('<script>not executed</script>');
  await expect(panel.locator('.update-notes a, .update-notes script')).toHaveCount(0);
  expect((await commands(page)).some(type => retiredActions.includes(type))).toBe(false);
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations).toEqual([]);
});

for (const distribution of ['portable', 'installer'] as const) test(`${distribution}: network failure means retrying the check, not repairing a working program`, async ({ page }, testInfo) => {
  const message = '版本检查失败：网络连接不可用，请重试。';
  await page.setViewportSize({ width: 380, height: 540 });
  await connect(page, 'fluent', { ...fixture, distribution, state: 'failed', availableVersion: '', notes: '', message }, false, { checkFailure: message });
  await page.getByRole('button', { name: '更多设置', exact: true }).click(); await openUpdates(page);
  const panel = page.locator('.update-panel');
  await expect(panel.getByRole('status')).toHaveText('版本检查未完成，请重试');
  await expect(panel.getByRole('alert')).toHaveText(message);
  await expect(panel).not.toContainText('修复'); await expect(panel).not.toContainText('回滚'); await expect(panel).not.toContainText('准备免安装');
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  for (const width of [380, 320]) {
    await page.setViewportSize({ width, height: 540 }); await panel.locator('.update-actions').scrollIntoViewIfNeeded();
    await expect(panel.getByRole('alert')).toBeInViewport({ ratio: 1 });
    await expect(panel.getByRole('button', { name: '检查更新', exact: true })).toBeInViewport({ ratio: 1 });
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`notice-failed-${width}-${distribution}.png`) });
  }
  await panel.getByRole('button', { name: '检查更新', exact: true }).click();
  await expect.poll(async () => (await commands(page)).filter(type => type === 'check-update').length).toBe(1);
  await expect(panel.getByRole('button', { name: '查看 Release', exact: true })).toBeEnabled();
  expect((await commands(page)).some(type => retiredActions.includes(type))).toBe(false);
});

test('a slow check can be cancelled while the independent Release action remains available', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 540 }); await connect(page, 'winui', fixture, false, { holdCheck: true });
  await page.getByRole('button', { name: /^更多设置/ }).click(); await openUpdates(page);
  await page.getByRole('button', { name: '检查更新', exact: true }).click();
  await expect(page.getByRole('button', { name: '检查更新', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: '取消检查', exact: true })).toBeVisible();
  const release = page.getByRole('button', { name: '查看 Release', exact: true });
  await expect(release).toBeEnabled(); await release.click();
  await expect.poll(async () => (await commands(page)).filter(type => type === 'open-update-page').length).toBe(1);
  await page.getByRole('button', { name: '取消检查', exact: true }).click();
  await expect(page.getByText('已取消版本检查。', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '检查更新', exact: true })).toBeEnabled();
  await expect(page.getByRole('button', { name: '取消检查', exact: true })).toHaveCount(0);
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath('notice-cancelled-380.png') });
  expect((await commands(page)).some(type => retiredActions.includes(type))).toBe(false);
});

for (const capability of ['github-update-v1', 'portable-setup-v1']) test(`${capability}: legacy capability cannot enable the new notification UI`, async ({ page }) => {
  await connect(page, 'fluent', fixture, false, { capability });
  await page.getByRole('button', { name: '更多设置', exact: true }).click(); await openUpdates(page);
  await expect(page.getByRole('button', { name: '查看 Release', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '检查更新', exact: true })).toHaveCount(0);
  await expect(page.locator('.update-panel')).toContainText('宿主不支持版本通知');
  expect((await commands(page)).some(type => retiredActions.includes(type) || type === 'open-update-page' || type === 'check-update')).toBe(false);
});

test('malicious metadata and reply URLs never navigate or override the fixed project Release', async ({ page }) => {
  await connect(page, 'fluent', fixture, false, { badOpenReply: true });
  await page.getByRole('button', { name: /^更多设置/ }).click(); await openUpdates(page);
  const before = page.url();
  await page.getByRole('button', { name: '查看 Release', exact: true }).click();
  await expect(page.locator('.update-panel').getByRole('alert')).toContainText('宿主响应数据无效');
  expect(page.url()).toBe(before);
  await emit(page, fixture);
  await expect(page.locator('.update-panel').getByRole('alert')).toContainText('宿主响应数据无效');
  await emit(page, { ...fixture, releasePage: 'https://evil.example', revision: 3 });
  await expect(page.locator('.update-panel').getByRole('alert')).toContainText('状态数据无效');
  await expect(page.getByRole('button', { name: '查看 Release', exact: true })).toHaveCount(0);
  await emit(page, { ...fixture, revision: 4 });
  await expect(page.getByRole('button', { name: '查看 Release', exact: true })).toBeEnabled();
  await emit(page, { ...fixture, state: 'current', availableVersion: '', revision: 2 });
  await expect(page.locator('.update-title .update-indicator')).toBeVisible();
  expect((await requests(page)).filter(value => value.type === 'open-update-page').map(value => value.payload)).toEqual([{}]);
  expect(page.url()).toBe(before);
});

for (const retiredField of ['autoDownload', 'portableSetupState', 'canInstall']) test(`${retiredField}: old snapshot fields cannot restore retired controls`, async ({ page }) => {
  await connect(page); await page.getByRole('button', { name: /^更多设置/ }).click(); await openUpdates(page);
  await emit(page, { ...fixture, [retiredField]: true, revision: 9 });
  await expect(page.locator('.update-panel').getByRole('alert')).toContainText('状态数据无效');
  await expect(page.locator('.update-panel').getByRole('button')).toHaveCount(0);
  expect((await commands(page)).some(type => retiredActions.includes(type))).toBe(false);
});
