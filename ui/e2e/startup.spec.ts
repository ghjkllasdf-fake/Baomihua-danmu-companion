import { test, expect, type Page } from '@playwright/test';
import { defaultSettings } from '../src/model';

async function desktop(page: Page, failWrite = false) {
  await page.addInitScript(({ settings, fail }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let enabled = false, writes = 0;
    const fixture = { writes: () => writes };
    Object.assign(window, { startupFixture: fixture });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string; payload: { enabled?: boolean } }) => {
        let response: object;
        if (message.type === 'hello') response = { ok: true, data: { apiBase: '', settings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1, capabilities: ['media-context-v1', 'desktop-lifecycle-v1'] } };
        else if (message.type === 'get-startup') response = { ok: true, data: { enabled } };
        else if (message.type === 'set-startup') {
          writes++;
          if (fail) response = { ok: false, error: 'controlled startup write failure' };
          else { enabled = message.payload.enabled === true; response = { ok: true, data: { enabled } }; }
        } else response = { ok: false, error: 'unsupported fixture command' };
        setTimeout(() => callbacks.forEach(callback => callback({ data: { type: 'response', id: message.id, ...response } })), 20);
      },
    } } });
  }, { settings: defaultSettings, fail: failWrite });
  await page.goto('/');
  await expect(page.getByRole('switch', { name: '开机自动启动', exact: true })).toBeEnabled();
}

test('fresh preview shows requested display defaults and never enables Windows startup', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByLabel('弹幕字号', { exact: true })).toHaveValue('22');
  await expect(page.getByLabel('文字描边', { exact: true })).toHaveValue('0');
  await expect(page.locator('.preview-bullet').first()).toHaveCSS('text-shadow', 'none');
  await expect(page.getByLabel('滚动速度', { exact: true })).toHaveValue('0.8');
  await expect(page.getByRole('switch', { name: '随视频窗口等比缩放', exact: false })).not.toBeChecked();
  await expect(page.getByRole('switch', { name: '开机自动启动', exact: true })).toBeDisabled();
});

test('startup is opt-in and only changes after confirmed user actions', async ({ page }) => {
  await desktop(page);
  const toggle = page.getByRole('switch', { name: '开机自动启动', exact: true });
  await expect(toggle).not.toBeChecked();
  expect(await page.evaluate(() => (window as unknown as { startupFixture: { writes(): number } }).startupFixture.writes())).toBe(0);
  await toggle.click();
  await expect(toggle).toBeEnabled();
  await expect(toggle).toBeChecked();
  await toggle.click();
  await expect(toggle).toBeEnabled();
  await expect(toggle).not.toBeChecked();
  expect(await page.evaluate(() => (window as unknown as { startupFixture: { writes(): number } }).startupFixture.writes())).toBe(2);
});

test('failed startup writes remain unconfirmed until a read recovers actual state', async ({ page }) => {
  await desktop(page, true);
  const toggle = page.getByRole('switch', { name: '开机自动启动', exact: true });
  await toggle.click();
  await expect(page.getByRole('alert')).toContainText('开机启动结果未确认');
  await expect(toggle).not.toBeChecked();
  await expect(toggle).toBeDisabled();
  await page.getByRole('button', { name: '重新读取状态', exact: true }).click();
  await expect(toggle).toBeEnabled();
  await expect(toggle).not.toBeChecked();
  await expect(page.getByRole('alert')).toHaveCount(0);
});
