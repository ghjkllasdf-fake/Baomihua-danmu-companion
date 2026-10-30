import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';
import { defaultAppearanceSnapshot, type AppearanceSnapshot } from '../src/appearance';

async function desktop(page: Page, failure: 'none' | 'reject' | 'lost-reply' = 'none') {
  let appearance: AppearanceSnapshot = { ...defaultAppearanceSnapshot }, writes = 0;
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
