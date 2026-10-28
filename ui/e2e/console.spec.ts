import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings, emptyPlayer } from '../src/model';

test('honest browser preview, display controls, responsive layout and accessibility', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.goto('/');
  await expect(page.getByText('浏览器预览模式：', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: '搜索弹幕来源' })).toBeDisabled();
  await page.getByRole('button', { name: '1/4', exact: true }).click();
  await expect(page.getByRole('button', { name: '1/4', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByText('25% 显示区域')).toBeVisible();
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations.filter(v => v.impact === 'critical' || v.impact === 'serious');
  expect(violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => ({ target: n.target, detail: n.failureSummary })) }))).toEqual([]);
  for (const width of [320, 768, 1120, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (width === 320 || width === 1120) await page.screenshot({ path: `../artifacts/ui-${width}.png`, fullPage: true });
  }
  expect(errors).toEqual([]);
});

test('UI bridge fixture: real controls complete the source-selection flow', async ({ page }) => {
  await page.addInitScript(() => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    const initial = { areaFraction: .5, fontSize: 28, scaleWithWindow: true, speedMultiplier: 1, opacity: .85, outlineWidth: 1, density: 60, rolling: true, top: true, bottom: true, dedupe: true, keywords: '', offsetMs: 0, enabled: true };
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, cb: (event: { data: unknown }) => void) => callbacks.push(cb),
      postMessage: (msg: { id: string; type: string; payload: unknown }) => {
        const data = msg.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1'], apiBase: 'http://fixture.invalid/token', settings: initial, settingsRevision: 0, stateSequence: 0 }
          : msg.type === 'search' ? { animes: [{ animeId: 1, animeTitle: '测试作品 · 原版' }, { animeId: 2, animeTitle: '测试作品 · 其他版本' }] }
          : msg.type === 'episodes' ? { bangumi: { episodes: [{ episodeId: 10, episodeNumber: 1, episodeTitle: '开始' }] } }
          : msg.type === 'comments' ? { count: 1234 } : initial;
        setTimeout(() => callbacks.forEach(cb => cb({ data: { type: 'response', id: msg.id, ok: true, data } })), 10);
      },
    } } });
  });
  await page.goto('/');
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
  await page.getByLabel('正在看什么？').fill('测试作品');
  await page.getByRole('button', { name: '搜索弹幕来源' }).click();
  await page.getByRole('button', { name: '测试作品 · 原版' }).click();
  await page.getByRole('button', { name: '载入分集' }).click();
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.getByText('1,234 条弹幕已准备')).toBeVisible();
  await expect(page.getByText('已自动同步', { exact: true })).toHaveCount(0);
});

test('detected title chooses its own episode, not an unrelated manual search', async ({ page }) => {
  await page.addInitScript(({ initialPlayer, initialSettings }) => {
    const callbacks: ((event: { data: unknown }) => void)[] = [];
    let sequence = 0;
    window.addEventListener('fixture-player', event => {
      const state = { type: 'state', sequence: ++sequence, data: { player: { ...initialPlayer, ...(event as CustomEvent).detail,
        sequence, mediaRevision: sequence, sampleMonotonicMs: 1000, positionResolutionMs: 200, source: 'fixture' } } };
      callbacks.forEach(cb => cb({ data: state }));
    });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, cb: (event: { data: unknown }) => void) => callbacks.push(cb),
      postMessage: (msg: { id: string; type: string }) => {
        const data = msg.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1'], apiBase: 'http://fixture.invalid/token', settings: initialSettings, settingsRevision: 0, stateSequence: sequence }
          : msg.type === 'search' ? { animes: [{ animeId: 1, animeTitle: '匹配来源' }] }
          : msg.type === 'comments' ? { count: 23 }
          : { bangumi: { episodes: [{ episodeId: 10, episodeNumber: 1 }, { episodeId: 170, episodeNumber: 17 }, { episodeId: 180, episodeNumber: 18 }] } };
        setTimeout(() => {
          callbacks.forEach(cb => cb({ data: { type: 'response', id: msg.id, ok: true, data } }));
          if (msg.type === 'hello') {
            const state = { type: 'state', sequence: ++sequence, data: { player: { ...initialPlayer, sequence, mediaRevision: sequence,
              sampleMonotonicMs: 1000, positionResolutionMs: 200, source: 'fixture', title: '示例电视剧', episodeNumber: 17, syncMode: 'automatic', state: 'paused', positionMs: 300000 } } };
            callbacks.forEach(cb => cb({ data: state }));
          }
        }, 10);
      },
    } } });
  }, { initialPlayer: emptyPlayer, initialSettings: defaultSettings });
  await page.goto('/');
  await page.getByRole('button', { name: '使用播放器片名', exact: false }).click();
  await expect(page.getByLabel('当前分集')).toHaveValue('170');
  await page.getByRole('button', { name: '加载这一集的弹幕' }).click();
  await expect(page.getByText('23 条弹幕已准备')).toBeVisible();
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('fixture-player', { detail: { title: '示例电视剧', episodeNumber: 18, syncMode: 'automatic', state: 'playing', positionMs: 0 } })));
  await expect(page.getByLabel('当前分集')).toHaveValue('180');
  await expect(page.getByText('23 条弹幕已准备')).toBeVisible();
  await page.getByLabel('正在看什么？').fill('三体');
  await page.getByRole('button', { name: '搜索弹幕来源' }).click();
  await expect(page.getByLabel('当前分集')).toHaveValue('10');
});
