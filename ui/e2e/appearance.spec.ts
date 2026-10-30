import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';
import { defaultAppearanceSnapshot, type AppearanceSnapshot } from '../src/appearance';

async function desktop(page: Page, failure: 'none' | 'reject' | 'lost-reply' = 'none', backdrop: AppearanceSnapshot['backdrop'] = 'solid') {
  let appearance: AppearanceSnapshot = { ...defaultAppearanceSnapshot, backdrop }, writes = 0;
  await page.exposeFunction('appearanceTransport', (message: { type: string; id: string; payload: Record<string, unknown> }) => {
    let data: unknown, ok = true, code = '', error = '';
    if (message.type === 'hello') data = { settings: defaultSettings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1,
      apiBase: '', capabilities: ['media-context-v1', 'desktop-lifecycle-v1', 'tray-flyout-v1', 'appearance-v1'], appearance };
    else if (message.type === 'get-settings') data = { settings: defaultSettings, settingsRevision: 0 };
    else if (message.type === 'get-startup') data = { enabled: false };
    else if (message.type === 'get-appearance') data = appearance;
    else if (message.type === 'set-appearance') {
      writes++;
      if (failure === 'reject') { ok = false; code = 'appearance-not-applied'; error = '外观保存失败，本次设置未应用。'; }
      else {
        appearance = { ...appearance, ...message.payload, revision: appearance.revision + 1 } as AppearanceSnapshot;
        data = appearance;
        if (failure === 'lost-reply') { ok = false; code = 'appearance-outcome-unknown'; error = '保存结果未确认。'; }
      }
    } else data = { hidden: true };
    return { type: 'response', id: message.id, ok, data, code, error };
  });
  await page.addInitScript(() => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    const transport = (window as unknown as { appearanceTransport(message: unknown): Promise<unknown> }).appearanceTransport;
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: unknown) => { void transport(message).then(data => callbacks.forEach(callback => callback({ data }))); },
    } } });
  });
  return { writes: () => writes, snapshot: () => appearance };
}

test('four real skins preview offline without storage, keep the compact jobs, and remain accessible', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 540 });
  await page.goto('/');
  await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
  for (const [skin, label] of [['fluent', 'A · 原生清爽'], ['glass', 'B · 流光玻璃'], ['winui', 'C · WinUI'], ['studio', 'D · 石墨工作室']]) {
    await page.getByRole('button', { name: label, exact: true }).click();
    await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
    await expect(page.getByRole('button', { name: label, exact: true })).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.appearance-status')).toContainText('预览');
    await page.getByRole('button', { name: '返回设置', exact: true }).click();
    await expect(page.getByRole('slider')).toHaveCount(4);
    await expect(page.locator('.preview')).toHaveCount(0);
    const axe = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    expect(axe.violations).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
  }
  expect(await page.evaluate(() => localStorage.length + sessionStorage.length)).toBe(0);
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-skin', 'fluent');
});

test('confirmed appearance survives a new document and stays separate from display settings', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 540 });
  const fixture = await desktop(page);
  await page.goto('/');
  await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
  await page.getByRole('button', { name: 'B · 流光玻璃', exact: true }).click();
  await expect(page.locator('.appearance-status')).toContainText('已保存');
  await expect(page.getByRole('switch', { name: '减少透明效果', exact: true })).toBeInViewport({ ratio: 1 });
  await page.getByRole('switch', { name: '减少透明效果', exact: true }).click();
  await expect(page.locator('.appearance-status')).toContainText('已保存');
  expect(fixture.writes()).toBe(2);
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-skin', 'glass');
  await expect(page.locator('html')).toHaveAttribute('data-transparency', 'reduced');
  await expect(page.getByRole('slider', { name: '弹幕字号', exact: true })).toHaveValue('22');
  await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
  await expect(page.getByRole('button', { name: 'B · 流光玻璃', exact: true })).toHaveAttribute('aria-pressed', 'true');
});

test('rejected saves roll back to A, while ambiguous outcomes read the confirmed host state', async ({ page, context }) => {
  await desktop(page, 'reject');
  await page.goto('/');
  await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
  await page.getByRole('button', { name: 'B · 流光玻璃', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-skin', 'fluent');
  await expect(page.getByRole('alert')).toContainText('保存失败');
  await expect(page.getByRole('button', { name: 'B · 流光玻璃', exact: true })).toBeEnabled();
  const other = await context.newPage();
  await desktop(other, 'lost-reply');
  await other.goto('/');
  await other.getByRole('button', { name: '切换皮肤', exact: true }).click();
  await other.getByRole('button', { name: 'C · WinUI', exact: true }).click();
  await expect(other.locator('html')).toHaveAttribute('data-skin', 'winui');
  await expect(other.getByRole('alert')).toContainText('重新确认');
  await expect(other.getByRole('button', { name: 'C · WinUI', exact: true })).toBeEnabled();
  await other.close();
});

test('touch skin controls preserve the existing large input targets and forced-colour fallback', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const page = await context.newPage();
  try {
    await page.goto('/');
    expect(await page.evaluate(() => matchMedia('(pointer: coarse)').matches)).toBe(true);
    const slider = page.getByRole('slider', { name: '弹幕字号', exact: true });
    expect((await slider.boundingBox())!.height).toBeGreaterThanOrEqual(40);
    await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
    await page.getByRole('button', { name: 'B · 流光玻璃', exact: true }).click();
    await page.getByRole('button', { name: '返回设置', exact: true }).click();
    expect((await slider.boundingBox())!.height).toBeGreaterThanOrEqual(40);
    await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
    expect(await page.locator('.mini-toolbar').evaluate(node => getComputedStyle(node).backdropFilter)).toBe('none');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  } finally { await context.close(); }
});

test('glass exposes the native material instead of repainting an opaque sheet, with explicit solid fallback', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 540 });
  await desktop(page, 'none', 'acrylic');
  await page.goto('/');
  await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
  await page.getByRole('button', { name: 'B · 流光玻璃', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-backdrop', 'acrylic');
  const coating = await page.locator('.mini-app').evaluate(node => {
    const style = getComputedStyle(node);
    return { colour: style.backgroundColor, sheen: style.backgroundImage };
  });
  const alpha = Number(coating.colour.match(/[\d.]+/g)?.[3] ?? 1);
  expect(alpha).toBeGreaterThan(0);
  expect(alpha).toBeLessThanOrEqual(0.25);
  expect(coating.sheen).toContain('linear-gradient');
  expect(await page.locator('body').evaluate(node => getComputedStyle(node).backgroundColor)).toBe('rgba(0, 0, 0, 0)');
  await page.getByRole('switch', { name: '减少透明效果', exact: true }).check();
  await expect(page.locator('html')).toHaveAttribute('data-transparency', 'reduced');
  expect(await page.locator('.mini-app').evaluate(node => getComputedStyle(node).backgroundColor)).toBe('rgb(237, 243, 247)');
  expect(await page.locator('.mini-app').evaluate(node => getComputedStyle(node).backgroundImage)).toBe('none');
  expect(await page.locator('.mini-toolbar').evaluate(node => getComputedStyle(node).backdropFilter)).toBe('none');
});

for (const label of ['B · 流光玻璃', 'C · WinUI']) {
  for (const backdrop of ['solid', 'acrylic'] as const) {
    test(`${label} keeps secondary navigation on the same ${backdrop} material without scrolling content underneath`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width: 380, height: 540 });
      await desktop(page, 'none', backdrop);
      await page.goto('/');
      await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
      await page.getByRole('button', { name: label, exact: true }).click();
      await expect(page.getByRole('button', { name: label, exact: true })).toBeEnabled();
      await expect(page.locator('.appearance-status')).toContainText('已保存');
      const navigation = page.getByRole('navigation', { name: '面板导航' });
      await page.screenshot({ path: testInfo.outputPath('appearance.png') });
      await expect(navigation).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
      await expect(navigation).toHaveCSS('backdrop-filter', 'none');
      await expect(page.getByRole('heading', { name: '外观与皮肤', exact: true })).toBeVisible();
      const firstChoice = page.getByRole('button', { name: 'A · 原生清爽', exact: true });
      await firstChoice.focus();
      await page.keyboard.press('Shift+Tab');
      await page.keyboard.press('Tab');
      await expect(firstChoice).toBeFocused();
      await expect(firstChoice).toHaveCSS('outline-width', '2px');
      const viewport = (await page.locator('.panel-main > div:visible').boundingBox())!;
      const first = (await firstChoice.boundingBox())!;
      const second = (await page.getByRole('button', { name: 'B · 流光玻璃', exact: true }).boundingBox())!;
      expect(first.x - viewport.x).toBeGreaterThanOrEqual(4);
      expect(viewport.x + viewport.width - second.x - second.width).toBeGreaterThanOrEqual(4);
      await page.screenshot({ path: testInfo.outputPath('appearance-keyboard.png') });
      await page.getByRole('button', { name: '返回设置', exact: true }).click();
      await page.getByRole('button', { name: '更多设置', exact: true }).click();
      await page.screenshot({ path: testInfo.outputPath('more.png') });
      await expect(navigation).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
      await expect(navigation).toHaveCSS('backdrop-filter', 'none');

      await page.setViewportSize({ width: 320, height: 340 });
      await page.locator('.manual-sync summary').click();
      const content = page.locator('.more-view');
      const before = (await navigation.boundingBox())!;
      const scroll = await content.evaluate(node => {
        node.scrollTop = node.scrollHeight;
        return { top: node.scrollTop, maximum: node.scrollHeight - node.clientHeight };
      });
      expect(scroll.maximum).toBeGreaterThan(0);
      expect(scroll.top).toBe(scroll.maximum);
      const after = (await navigation.boundingBox())!;
      expect(after.y).toBe(before.y);
      expect((await content.boundingBox())!.y).toBeGreaterThanOrEqual(after.y + after.height);
      await expect(page.getByRole('button', { name: '返回设置', exact: true })).toBeInViewport({ ratio: 1 });
      await page.screenshot({ path: testInfo.outputPath('more-scrolled-narrow.png') });
      await page.getByRole('button', { name: '返回设置', exact: true }).click();
      await expect(page.getByRole('slider', { name: '弹幕字号', exact: true })).toBeVisible();
    });
  }
}

test('A and D keep their existing navigation surfaces and the clothing shortcut remains keyboard accessible', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 540 });
  await desktop(page);
  await page.goto('/');
  const shortcut = page.getByRole('button', { name: '切换皮肤', exact: true });
  await shortcut.focus();
  await shortcut.press('Enter');
  await expect(shortcut).toHaveAttribute('aria-pressed', 'true');
  await expect(shortcut.locator('svg')).toHaveAttribute('aria-hidden', 'true');
  for (const [label, colour] of [['A · 原生清爽', 'rgb(239, 244, 249)'], ['D · 石墨工作室', 'rgb(32, 34, 37)']]) {
    await page.getByRole('button', { name: label, exact: true }).click();
    const navigation = page.getByRole('navigation', { name: '面板导航' });
    await expect(navigation).toHaveCSS('background-color', colour);
    await expect(navigation).toHaveCSS('position', 'sticky');
  }
  await shortcut.focus();
  await shortcut.press('Enter');
  await expect(shortcut).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('heading', { name: '爆米花弹幕面板', exact: true })).toBeFocused();
});
