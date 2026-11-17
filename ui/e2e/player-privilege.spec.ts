import { test, expect } from '@playwright/test';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { defaultSettings, emptyPlayer, type Player } from '../src/model';

const note = '（检测到网易爆米花正以管理员权限运行，弹幕无法显示）';

test('waiting label shows only confirmed privilege mismatch and clears on recovery', async ({ page }) => {
  await page.setViewportSize({ width: 380, height: 510 });
  await page.addInitScript(settings => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let sequence = 0;
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    window.addEventListener('fixture-player', event => emit({ type: 'state', sequence: ++sequence,
      data: { player: { ...(event as CustomEvent).detail, sequence }, commentCount: 0 } }));
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string }) => {
        const data = message.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1'],
          apiBase: '', settings, settingsRevision: 0, stateSequence: sequence }
          : message.type === 'get-settings' ? { settings, settingsRevision: 0 } : {};
        setTimeout(() => emit({ type: 'response', id: message.id, ok: true, data }), 10);
      },
    } } });
  }, defaultSettings);
  await page.goto('/');
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
  const strip = page.getByRole('region', { name: '播放器连接状态' });
  const publish = (player: Player) => page.evaluate(value => window.dispatchEvent(new CustomEvent('fixture-player', { detail: value })), player);
  await expect(strip).toContainText('等待爆米花开始播放');
  await expect(strip).not.toContainText(note);
  await publish({ ...emptyPlayer, source: 'unavailable', detail: 'permission-denied' });
  await expect(strip).not.toContainText(note);
  await publish({ ...emptyPlayer, source: 'unavailable', detail: 'player-elevated' });
  await expect(strip).toContainText(note);
  const evidence = resolve('../artifacts/elevated-player-ui');
  await mkdir(evidence, { recursive: true });
  for (const width of [380, 320]) {
    await page.setViewportSize({ width, height: 510 });
    await expect(strip.getByText(note, { exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const clipped = await strip.getByText(note, { exact: true }).evaluate(element => element.scrollWidth > element.clientWidth);
    expect(clipped).toBe(false);
    await expect(page.getByRole('button', { name: '更多设置', exact: true })).toBeInViewport();
    await page.screenshot({ path: resolve(evidence, `warning-${width}.png`) });
  }
  await publish({ ...emptyPlayer, source: 'unavailable' });
  await expect(strip).not.toContainText(note);
  await expect(strip).toContainText('等待爆米花开始播放');
  await publish({ ...emptyPlayer, title: '测试影片', source: 'player-memory', detail: 'player-elevated',
    syncMode: 'automatic', state: 'playing', positionMs: 1000, sampleMonotonicMs: 1000, positionResolutionMs: 200 });
  await expect(strip).toContainText('测试影片');
  await expect(strip).not.toContainText(note);
});
