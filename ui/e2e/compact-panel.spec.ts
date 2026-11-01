import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';

for (const label of ['A · 原生清爽', 'B · 流光玻璃', 'C · WinUI', 'D · 石墨工作室']) {
  test(`${label} reuses the footer button to return from more settings without a duplicate header`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 380, height: 540 });
    await page.goto('/');
    await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
    await page.getByRole('button', { name: label, exact: true }).click();
    await page.getByRole('button', { name: '返回设置', exact: true }).click();
    const toggle = page.locator('.footer-actions .more-link');
    const before = (await toggle.boundingBox())!;
    await toggle.focus();
    await toggle.press('Enter');
    await expect(toggle).toHaveAccessibleName('返回');
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await expect(page.getByRole('button', { name: '返回设置', exact: true })).toHaveCount(0);
    await expect(page.getByRole('navigation', { name: '面板导航' })).toHaveCount(0);
    const heading = page.getByRole('heading', { name: '更多设置', exact: true });
    await expect(heading).toHaveClass('sr-only');
    await expect(heading).toBeFocused();
    const types = page.locator('.source-types');
    await expect(types).not.toHaveAttribute('open', '');
    await expect(page.getByRole('checkbox', { name: '滚动', exact: true })).toBeHidden();
    await types.locator('summary').focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('checkbox', { name: '滚动', exact: true })).toBeVisible();
    await page.getByRole('checkbox', { name: '顶部', exact: true }).uncheck();
    await expect(page.getByRole('checkbox', { name: '顶部', exact: true })).not.toBeChecked();
    await types.locator('summary').focus();
    await page.keyboard.press('Enter');
    await expect(types).not.toHaveAttribute('open', '');
    expect(await page.locator('.display-settings[data-view=advanced]').evaluate(el => getComputedStyle(el).backgroundColor)).toBe('rgba(0, 0, 0, 0)');
    const after = (await toggle.boundingBox())!;
    expect(after.x).toBe(before.x);
    expect(after.y).toBe(before.y);
    await page.locator('.advanced summary').click();
    await page.getByRole('textbox', { name: '屏蔽关键词', exact: true }).fill('保留的关键词');
    await page.screenshot({ path: testInfo.outputPath('more-footer-return.png') });
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    expect(axe.violations).toEqual([]);
    await toggle.focus();
    await toggle.press('Enter');
    await expect(toggle).toHaveAccessibleName('更多设置');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByRole('heading', { name: '爆米花弹幕面板', exact: true })).toBeFocused();
    await expect(page.getByRole('slider', { name: '弹幕字号', exact: true })).toBeVisible();
    await toggle.click();
    await expect(page.locator('.advanced')).toHaveAttribute('open', '');
    await expect(page.getByRole('textbox', { name: '屏蔽关键词', exact: true })).toHaveValue('保留的关键词');
    await page.setViewportSize({ width: 320, height: 340 });
    await page.locator('.manual-sync summary').click();
    const scroll = await page.locator('.panel-main').evaluate(node => {
      node.scrollTop = node.scrollHeight;
      return { top: node.scrollTop, maximum: node.scrollHeight - node.clientHeight };
    });
    expect(scroll.maximum).toBeGreaterThan(0);
    expect(scroll.top).toBe(scroll.maximum);
    await expect(toggle).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('more-footer-return-narrow-scrolled.png') });
    await toggle.click();
    await expect.poll(() => page.locator('.panel-main').evaluate(node => node.scrollTop)).toBe(0);
    await expect(page.getByRole('slider', { name: '弹幕字号', exact: true })).toBeVisible();
  });
}

test('compact panel has rows and header toggles, no product preview, and local navigation preserves edits', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 560 });
  await page.goto('/');
  await expect(page.locator('.mini-app')).toBeVisible({ timeout: 5000 });
  await expect(page.locator('.preview')).toHaveCount(0);
  await expect(page.getByRole('switch', { name: '显示弹幕', exact: true })).toBeVisible();
  await expect(page.getByRole('switch', { name: '开机自动启动', exact: true })).toBeVisible();
  await expect(page.getByLabel('API 地址', { exact: true })).toBeVisible();
  const rows = await page.getByRole('slider').evaluateAll(elements => elements.map(element => element.getBoundingClientRect().top));
  expect(rows.length).toBe(5);
  expect(new Set(rows).size).toBe(5);
  await expect(page.getByRole('slider', { name: '阴影强度', exact: true })).toHaveValue(String(defaultSettings.shadowStrength));
  await expect(page.getByRole('button', { name: '搜索弹幕来源', exact: true })).toBeHidden();
  await page.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
  await page.getByLabel('正在看什么？').fill('保留的手动搜索');
  await page.getByRole('button', { name: '返回设置', exact: true }).click();
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(page.getByRole('heading', { name: '更多设置', exact: true })).toBeFocused({ timeout: 3000 });
  await page.locator('.source-types summary').click();
  await expect(page.getByRole('checkbox', { name: '滚动', exact: true })).toBeVisible();
  await page.locator('.advanced summary').click();
  expect((await page.locator('.advanced .range-control label').boundingBox())!.height).toBeLessThan(25);
  await page.getByRole('button', { name: '返回', exact: true }).click();
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

for (const supported of [true, false]) {
  test(`view size signals respect host capability: ${supported}`, async ({ page }) => {
    await page.addInitScript(({ settings, supported }) => {
      const callbacks: ((event: { data: unknown }) => void)[] = [], views: string[] = [];
      Object.assign(window, { observedTrayViews: views });
      Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
        addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
        postMessage: (message: { id: string; type: string; payload: { view?: string } }) => {
          if (message.type === 'set-tray-view') views.push(message.payload.view!);
          const data = message.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1', ...(supported ? ['tray-view-size-v1'] : [])],
            apiBase: '', settings, settingsRevision: 0, stateSequence: 0 } : message.type === 'set-tray-view' ?
            { view: message.payload.view, width: 380, height: message.payload.view === 'home' ? 510 : 540 } : {};
          setTimeout(() => callbacks.forEach(callback => callback({ data: { type: 'response', id: message.id, ok: true, data } })), 10);
        },
      } } });
    }, { settings: defaultSettings, supported });
    await page.goto('/');
    await expect(page.getByText('桌面宿主已连接')).toBeVisible();
    if (supported) await expect.poll(() => page.evaluate(() => (window as unknown as { observedTrayViews: string[] }).observedTrayViews)).toEqual(['home']);
    await page.getByRole('button', { name: '更多设置', exact: true }).click();
    if (supported) await expect.poll(() => page.evaluate(() => (window as unknown as { observedTrayViews: string[] }).observedTrayViews)).toEqual(['home', 'settings']);
    await page.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
    if (supported) await expect.poll(() => page.evaluate(() => (window as unknown as { observedTrayViews: string[] }).observedTrayViews)).toEqual(['home', 'settings', 'search']);
    await page.getByRole('button', { name: '返回设置', exact: true }).click();
    if (supported) await expect.poll(() => page.evaluate(() => (window as unknown as { observedTrayViews: string[] }).observedTrayViews)).toEqual(['home', 'settings', 'search', 'home']);
    else expect(await page.evaluate(() => (window as unknown as { observedTrayViews: string[] }).observedTrayViews)).toEqual([]);
  });
}
