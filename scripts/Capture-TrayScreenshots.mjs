import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { defaultSettings, defaultLocalSources } from '../ui/src/model.ts';

const root = resolve(import.meta.dirname, '..');
const require = createRequire(resolve(root, 'ui/package.json'));
const { chromium, expect } = require('@playwright/test');
const output = resolve(root, 'docs/assets');
await mkdir(output, { recursive: true });
const server = spawn(process.execPath, [resolve(root, 'ui/node_modules/vite/bin/vite.js'), 'preview', '--host', '127.0.0.1', '--port', '5177', '--strictPort'],
  { cwd: resolve(root, 'ui'), windowsHide: true, stdio: 'pipe' });
let browser;
try {
  for (let attempt = 0; ; attempt++) {
    try { if ((await fetch('http://127.0.0.1:5177')).ok) break; } catch { /* Await only this owned server. */ }
    if (attempt > 80 || server.exitCode !== null) throw new Error('The isolated screenshot server did not start.');
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  async function panel(embedded) {
    const page = await browser.newPage({ viewport: { width: 380, height: 568 }, deviceScaleFactor: 1 });
    await page.addInitScript(({ initial, sources, embedded }) => {
      const callbacks = [];
      let settings = initial, settingsRevision = 0;
      const localApi = { enabled: true, state: 'ready', message: '本机弹幕服务已就绪。', sources,
        availableSources: [{ id: 'douban', label: '豆瓣' }, { id: '360', label: '360' }, { id: 'renren', label: '人人视频' }, { id: 'hanjutv', label: '韩剧TV' }] };
      Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
        addEventListener: (_, callback) => callbacks.push(callback),
        postMessage: message => {
          let data;
          if (message.type === 'hello') data = { apiBase: embedded ? 'http://127.0.0.1:12345/EXAMPLE_ONLY' : 'https://danmu.example.com/EXAMPLE_ONLY',
            apiSaved: !embedded, apiNotice: '', settings, settingsRevision, stateSequence: 0, protocolVersion: 1,
            capabilities: ['media-context-v1', 'automatic-match-v1', 'desktop-lifecycle-v1', 'tray-flyout-v1', ...(embedded ? ['embedded-api-v1'] : ['api-persistence-v1'])],
            ...(embedded ? { localApi } : {}) };
          else if (message.type === 'get-startup') data = { enabled: false };
          else if (message.type === 'get-settings') data = { settings, settingsRevision };
          else if (message.type === 'update-settings') { settings = message.payload.settings; settingsRevision++; data = settings; }
          else if (message.type === 'search') data = { animes: [{ animeId: 1, animeTitle: '海边日落 · 自制演示作品' }] };
          else if (message.type === 'episodes') data = { bangumi: { episodes: [{ episodeId: 10, episodeNumber: 1, episodeTitle: '自制演示片段' }] } };
          else throw new Error('Unexpected isolated screenshot command.');
          setTimeout(() => callbacks.forEach(callback => callback({ data: { type: 'response', id: message.id, ok: true, data, settingsRevision } })), 10);
        },
      } } });
    }, { initial: defaultSettings, sources: defaultLocalSources, embedded });
    await page.goto('http://127.0.0.1:5177');
    await expect(page.getByText('桌面宿主已连接')).toBeVisible();
    await page.evaluate(() => {
      const caption = document.createElement('p');
      caption.textContent = '界面演示 · 隔离示例数据 · 非真实播放器验收';
      caption.style.cssText = 'margin:0;padding:7px 10px;font-size:11px;text-align:center';
      document.body.append(caption);
    });
    return page;
  }
  const standard = await panel(false);
  await expect(standard.getByRole('button', { name: '已保存', exact: true })).toBeDisabled();
  await standard.screenshot({ path: resolve(output, 'tray-panel.png') });
  await standard.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(standard.getByRole('heading', { name: '更多设置', exact: true })).toBeFocused();
  await standard.locator('.advanced summary').click();
  await standard.screenshot({ path: resolve(output, 'tray-more.png') });
  await standard.getByRole('button', { name: '手动搜索 / 换源', exact: true }).click();
  await standard.getByLabel('正在看什么？').fill('海边日落');
  await standard.getByRole('button', { name: '搜索弹幕来源', exact: true }).click();
  await expect(standard.getByLabel('当前分集')).toHaveValue('10');
  await standard.getByRole('button', { name: '加载这一集的弹幕', exact: true }).scrollIntoViewIfNeeded();
  await standard.screenshot({ path: resolve(output, 'tray-source.png') });
  const embedded = await panel(true);
  await expect(embedded.getByLabel('API 地址', { exact: true })).toHaveCount(0);
  await embedded.screenshot({ path: resolve(output, 'tray-includeapi.png') });
  console.log('Captured four labelled compact React panels with isolated fixtures; no real service or player access.');
} finally {
  await browser?.close();
  if (server.exitCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
}
