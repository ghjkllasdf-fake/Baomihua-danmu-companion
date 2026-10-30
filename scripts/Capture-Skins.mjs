import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { defaultSettings } from '../ui/src/model.ts';

const root = resolve(import.meta.dirname, '..');
const require = createRequire(resolve(root, 'ui/package.json'));
const { chromium, expect } = require('@playwright/test');
const output = resolve(root, 'artifacts/skin-preview');
await mkdir(output, { recursive: true });
const port = 5187;
const server = spawn(process.execPath, [resolve(root, 'ui/node_modules/vite/bin/vite.js'), 'preview', '--host', '127.0.0.1', '--port', String(port), '--strictPort'], { cwd: resolve(root, 'ui'), windowsHide: true, stdio: 'pipe' });
let browser;
const evidence = { demoOnly: true, desktopMaterialInBrowser: false, errors: [], layouts: [], externalRequests: [] };
try {
  for (let attempt = 0; ; attempt++) {
    try { if ((await fetch(`http://127.0.0.1:${port}`)).ok) break; } catch { /* Await only this owned local server. */ }
    if (attempt > 100 || server.exitCode !== null) throw new Error('The owned skin capture server failed to start.');
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  const shots = [];
  const labels = ['A · 原生清爽', 'B · 流光玻璃', 'C · WinUI', 'D · 石墨工作室'];
  for (const skin of ['fluent', 'glass', 'winui', 'studio']) {
    const context = await browser.newContext({ viewport: { width: 380, height: 540 } });
    const page = await context.newPage();
    page.on('pageerror', error => evidence.errors.push(error.message));
    page.on('request', request => { if (!request.url().startsWith(`http://127.0.0.1:${port}/`)) evidence.externalRequests.push(request.url()); });
    await page.addInitScript(({ settings: initial, skin }) => {
      const callbacks = []; let settings = initial, settingsRevision = 0, appearance = { skin, reduceTransparency: false, revision: 0, backdrop: 'solid', notice: '' };
      Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
        addEventListener: (_, callback) => callbacks.push(callback),
        postMessage: message => {
          let data;
          if (message.type === 'hello') data = { settings, settingsRevision, stateSequence: 0, protocolVersion: 1, apiBase: 'https://danmu.example.invalid/demo', apiSaved: true, apiNotice: '设计演示 · 不读取或保存真实配置', appearance,
            capabilities: ['media-context-v1', 'automatic-match-v1', 'desktop-lifecycle-v1', 'tray-flyout-v1', 'api-persistence-v1', 'appearance-v1'] };
          else if (message.type === 'get-settings') data = { settings, settingsRevision };
          else if (message.type === 'update-settings') { settings = message.payload.settings; settingsRevision++; data = settings; }
          else if (message.type === 'get-startup') data = { enabled: false };
          else if (message.type === 'get-appearance') data = appearance;
          else if (message.type === 'set-appearance') { appearance = { ...appearance, ...message.payload, revision: appearance.revision + 1 }; data = appearance; }
          else throw new Error('Unexpected isolated skin capture command.');
          setTimeout(() => callbacks.forEach(callback => callback({ data: { type: 'response', id: message.id, ok: true, data, settingsRevision } })), 10);
        },
      } } });
    }, { settings: defaultSettings, skin });
    await page.goto(`http://127.0.0.1:${port}/`);
    await expect(page.getByText('桌面宿主已连接')).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-skin', skin);
    for (const width of [380, 320, 390, 768]) {
      await page.setViewportSize({ width, height: 540 });
      const layout = await page.evaluate(() => { const main = document.querySelector('.panel-main'); return { width: innerWidth, overflow: document.documentElement.scrollWidth - innerWidth, clipped: main.scrollHeight - main.clientHeight }; });
      evidence.layouts.push({ skin, ...layout });
      if (layout.overflow > 1 || layout.clipped > 1) throw new Error(`${skin} does not fit its compact desktop-pointer viewport: ${JSON.stringify(layout)}`);
    }
    await page.setViewportSize({ width: 380, height: 540 });
    const image = await page.screenshot({ path: resolve(output, `${skin}.png`) });
    shots.push(image.toString('base64'));
    await page.getByRole('button', { name: '切换皮肤', exact: true }).click();
    await page.screenshot({ path: resolve(output, `${skin}-picker.png`) });
    await context.close();
  }
  const board = await browser.newPage({ viewport: { width: 1680, height: 700 } });
  await board.setContent('<canvas width="1680" height="700"></canvas>');
  const png = await board.evaluate(async ({ shots, labels }) => {
    const canvas = document.querySelector('canvas'), ctx = canvas.getContext('2d');
    ctx.fillStyle = '#edf1f3'; ctx.fillRect(0, 0, 1680, 700);
    ctx.fillStyle = '#23333e'; ctx.font = '600 23px "Microsoft YaHei UI"'; ctx.fillText('爆米花弹幕 · 皮肤系统', 44, 35);
    ctx.fillStyle = '#536575'; ctx.font = '13px "Microsoft YaHei UI"'; ctx.fillText('保留 A，其余三款重做控件形态、材质与层次；仍是同一个 380 × 540 托盘面板。', 44, 62);
    for (let i = 0; i < shots.length; i++) {
      const image = new Image(); image.src = `data:image/png;base64,${shots[i]}`; await image.decode();
      const x = 44 + i * 404; ctx.fillStyle = '#243644'; ctx.font = '600 18px "Microsoft YaHei UI"'; ctx.fillText(labels[i], x, 96); ctx.drawImage(image, x, 111);
    }
    ctx.fillStyle = '#536575'; ctx.font = '12px "Microsoft YaHei UI"'; ctx.fillText('隔离演示数据 · 浏览器中不展示桌面透色；原生系统材质的实际验证结果另记，不以此图冒充。', 44, 682);
    return canvas.toDataURL('image/png').split(',')[1];
  }, { shots, labels });
  await writeFile(resolve(output, 'comparison.png'), Buffer.from(png, 'base64'));
  if (evidence.errors.length || evidence.externalRequests.length) throw new Error('The isolated skin preview reported browser errors or external traffic.');
  evidence.result = 'PASS';
} catch (error) { evidence.result = 'FAIL'; evidence.failure = error.stack; process.exitCode = 1; }
finally {
  await writeFile(resolve(output, 'verification.json'), JSON.stringify(evidence, null, 2));
  console.log(JSON.stringify(evidence, null, 2));
  await browser?.close();
  if (server.exitCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
}
