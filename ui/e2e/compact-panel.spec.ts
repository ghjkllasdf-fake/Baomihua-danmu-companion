import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';

test('compact panel has rows and header toggles, no product preview, and local navigation preserves edits', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 560 });
  await page.goto('/');
  await expect(page.locator('.mini-app')).toBeVisible({ timeout: 5000 });
  await expect(page.locator('.preview')).toHaveCount(0);
  await expect(page.getByRole('switch', { name: '显示弹幕', exact: true })).toBeVisible();
  await expect(page.getByRole('switch', { name: '开机自动启动', exact: true })).toBeVisible();
  await expect(page.getByLabel('API 地址', { exact: true })).toBeVisible();
  const rows = await page.getByRole('slider').evaluateAll(elements => elements.map(element => element.getBoundingClientRect().top));
  expect(rows.length).toBe(4);
  expect(new Set(rows).size).toBe(4);
  await expect(page.getByRole('button', { name: '搜索弹幕来源', exact: true })).toBeHidden();
  await page.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
  await page.getByLabel('正在看什么？').fill('保留的手动搜索');
  await page.getByRole('button', { name: '返回设置', exact: true }).click();
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(page.getByRole('heading', { name: '更多设置', exact: true })).toBeFocused({ timeout: 3000 });
  await expect(page.getByRole('checkbox', { name: '滚动', exact: true })).toBeVisible();
  await page.locator('.advanced summary').click();
  expect((await page.locator('.advanced .range-control label').boundingBox())!.height).toBeLessThan(25);
  await page.getByRole('button', { name: '返回设置', exact: true }).click();
  await page.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
  await expect(page.getByLabel('正在看什么？')).toHaveValue('保留的手动搜索');
  await page.getByRole('button', { name: '返回设置', exact: true }).click();
  for (const width of [320, 380, 768]) {
    await page.setViewportSize({ width, height: 560 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
  expect(violations.filter(value => value.impact === 'critical' || value.impact === 'serious')).toEqual([]);
});

test('Escape from a focused closed select requests hide, never process exit', async ({ page }) => {
  await page.addInitScript(settings => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string }) => {
        document.documentElement.dataset.lastCommand = message.type;
        const data = message.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1', 'tray-flyout-v1'],
          apiBase: '', settings, settingsRevision: 0, stateSequence: 0 } : { hidden: true };
        setTimeout(() => callbacks.forEach(callback => callback({ data: { type: 'response', id: message.id, ok: true, data } })), 10);
      },
    } } });
  }, defaultSettings);
  await page.goto('/');
  await expect(page.getByRole('button', { name: '收起面板' })).toBeEnabled();
  await page.getByRole('combobox', { name: '弹幕密度' }).focus();
  await page.keyboard.press('Escape');
  await expect(page.locator('html')).toHaveAttribute('data-last-command', 'hide', { timeout: 3000 });
});
