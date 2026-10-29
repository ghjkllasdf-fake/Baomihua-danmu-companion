import { test, expect } from '@playwright/test';
import { defaultSettings } from '../src/model';

test('API save is explicit, disables after success, restores on reopen and preserves edits after failure', async ({ page }) => {
  await page.addInitScript(settings => {
    const listeners: ((event: { data: unknown }) => void)[] = [];
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => listeners.push(callback),
      postMessage: (message: { id: string; type: string; payload: { apiBase?: string } }) => {
        const address = sessionStorage.getItem('fixture-saved-api') || '';
        let data: unknown = { configured: true }, ok = true;
        if (message.type === 'hello') data = { protocolVersion: 1, capabilities: ['media-context-v1', 'api-persistence-v1'],
          settings, settingsRevision: 0, stateSequence: 0, apiBase: address, apiSaved: Boolean(address), apiNotice: '' };
        if (message.type === 'save-api') {
          if (message.payload.apiBase?.includes('FAIL_EXAMPLE')) ok = false;
          else {
            sessionStorage.setItem('fixture-saved-api', message.payload.apiBase || '');
            data = { apiBase: message.payload.apiBase, saved: Boolean(message.payload.apiBase) };
          }
        }
        document.documentElement.dataset.lastApiCommand = message.type;
        setTimeout(() => listeners.forEach(callback => callback({ data: { type: 'response', id: message.id, ok, data, error: 'API 地址保存失败，请重试。' } })), 20);
      },
    } } });
  }, defaultSettings);
  await page.goto('/');
  const field = page.getByLabel('API 地址', { exact: true });
  const save = page.getByRole('button', { name: '保存地址', exact: true });
  await expect(field).toBeVisible();
  await expect(save).toBeDisabled();
  expect(await field.evaluate(e => e.getBoundingClientRect().top)).toBeLessThan(430);
  await field.fill('https://danmu.example.com/EXAMPLE_ONLY');
  await expect(save).toBeEnabled();
  await page.waitForTimeout(700);
  await expect(page.locator('html')).toHaveAttribute('data-last-api-command', 'hello');
  await save.click();
  await expect(page.getByRole('button', { name: '已保存', exact: true })).toBeDisabled();
  await page.reload();
  await expect(field).toHaveValue('https://danmu.example.com/EXAMPLE_ONLY');
  await expect(page.getByRole('button', { name: '已保存', exact: true })).toBeDisabled();
  await field.fill('https://danmu.example.com/FAIL_EXAMPLE');
  await page.getByRole('button', { name: '保存地址', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('保存失败');
  await expect(field).toHaveValue('https://danmu.example.com/FAIL_EXAMPLE');
  await expect(page.getByRole('button', { name: '保存地址', exact: true })).toBeEnabled();
  await field.fill('https://danmu.example.com/EXAMPLE_ONLY');
  await expect(page.getByRole('button', { name: '已保存', exact: true })).toBeDisabled();
  await field.fill('');
  await page.getByRole('button', { name: '清除保存', exact: true }).click();
  await page.reload();
  await expect(field).toBeEmpty();
});
