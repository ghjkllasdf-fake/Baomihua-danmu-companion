import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { defaultSettings } from '../ui/src/model.ts';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(resolve(root, 'ui/package.json'));
const { chromium, expect } = require('@playwright/test');
const output = resolve(root, 'docs/assets');
const demo = resolve(root, '.cache/readme-demo');
await mkdir(output, { recursive: true });
await mkdir(demo, { recursive: true });
const overlay = (await readFile(resolve(demo, 'overlay.png'))).toString('base64');
const server = spawn(process.execPath, [resolve(root, 'ui/node_modules/vite/bin/vite.js'), 'preview', '--host', '127.0.0.1', '--port', '5177', '--strictPort'], { cwd: resolve(root, 'ui'), windowsHide: true, stdio: 'pipe' });
let browser;
try {
  for (let attempt = 0; ; attempt++) {
    try { if ((await fetch('http://127.0.0.1:5177')).ok) break; } catch { /* Wait for this owned preview server. */ }
    if (attempt > 80 || server.exitCode !== null) throw new Error('The isolated README preview server failed to start.');
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  browser = await chromium.launch({ channel: 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1200, height: 1000 }, deviceScaleFactor: 1 });
  await page.addInitScript(initial => {
    const callbacks = [];
    let settings = initial, settingsRevision = 0, apiBase = '', apiSaved = false;
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_, callback) => callbacks.push(callback),
      postMessage: message => {
        let data;
        if (message.type === 'hello') data = { apiBase, apiSaved, apiNotice: '', settings, settingsRevision, stateSequence: 0, protocolVersion: 1,
          capabilities: ['media-context-v1', 'automatic-match-v1', 'desktop-lifecycle-v1', 'api-persistence-v1'] };
        else if (message.type === 'save-api') { apiBase = message.payload.apiBase; apiSaved = Boolean(apiBase); data = { apiBase, saved: apiSaved }; }
        else if (message.type === 'get-settings') data = { settings, settingsRevision };
        else if (message.type === 'update-settings') { settings = message.payload.settings; settingsRevision++; data = settings; }
        else if (message.type === 'get-startup') data = { enabled: false };
        else throw new Error('Unexpected README fixture command.');
        setTimeout(() => callbacks.forEach(callback => callback({ data: { type: 'response', id: message.id, ok: true, data, settingsRevision } })), 10);
      },
    } } });
  }, defaultSettings);
  await page.goto('http://127.0.0.1:5177');
  await page.getByLabel('API 地址', { exact: true }).fill('https://danmu.example.com/EXAMPLE_ONLY');
  await page.getByRole('button', { name: '保存地址', exact: true }).click();
  await expect(page.getByRole('button', { name: '已保存', exact: true })).toBeDisabled();
  await page.evaluate(() => {
    const banner = document.createElement('div');
    banner.textContent = '界面演示 · 示例地址与数据 · 不含真实服务或观看记录';
    banner.style.cssText = 'background:#ffe56b;color:#2d2438;padding:9px 32px;font:12px "Microsoft YaHei",sans-serif';
    document.body.prepend(banner);
  });
  await page.screenshot({ path: resolve(output, 'preview-workbench.png') });
  await page.locator('.advanced summary').click();
  await page.locator('.main-controls').screenshot({ path: resolve(output, 'preview-settings.png') });

  // Record an original animated scene into a real video, then play it for the effect screenshot.
  const film = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  await film.setContent('<canvas width="1280" height="720"></canvas>');
  const videoBase64 = await film.evaluate(async () => {
    const canvas = document.querySelector('canvas'), ctx = canvas.getContext('2d');
    const stream = canvas.captureStream(24), chunks = [];
    const recorder = new MediaRecorder(stream, { mimeType: 'video/webm;codecs=vp9', videoBitsPerSecond: 2200000 });
    const finished = new Promise(resolve => recorder.onstop = resolve);
    recorder.ondataavailable = event => chunks.push(event.data);
    recorder.start();
    const start = performance.now();
    function draw(now) {
      const t = (now - start) / 1000;
      const sky = ctx.createLinearGradient(0, 0, 0, 430);
      sky.addColorStop(0, '#253b5b'); sky.addColorStop(.55, '#b47780'); sky.addColorStop(1, '#f4ba88');
      ctx.fillStyle = sky; ctx.fillRect(0, 0, 1280, 720);
      ctx.fillStyle = '#ffdf9a'; ctx.beginPath(); ctx.arc(850, 335, 65, 0, Math.PI * 2); ctx.fill();
      ctx.fillStyle = '#465873'; ctx.beginPath(); ctx.moveTo(0, 410); ctx.lineTo(130, 305); ctx.quadraticCurveTo(235, 265, 355, 410); ctx.closePath(); ctx.fill();
      ctx.fillStyle = '#344e65'; ctx.beginPath(); ctx.moveTo(0, 430); ctx.lineTo(0, 355); ctx.quadraticCurveTo(110, 340, 230, 430); ctx.closePath(); ctx.fill();
      const sea = ctx.createLinearGradient(0, 405, 0, 720);
      sea.addColorStop(0, '#6d8390'); sea.addColorStop(1, '#163b50');
      ctx.fillStyle = sea; ctx.fillRect(0, 405, 1280, 315);
      for (let i = 0; i < 32; i++) {
        const y = 411 + i * 10, spread = 14 + i * 3;
        ctx.fillStyle = `rgba(255,209,153,${.35 - i * .007})`;
        ctx.fillRect(850 - spread + Math.sin(i + t) * 9, y, spread * 2, 2 + i * .07);
      }
      ctx.strokeStyle = '#a4b8ba'; ctx.lineWidth = 1;
      for (let i = 0; i < 17; i++) {
        const y = 445 + i * 18, x = ((i * 193 + t * 10) % 1500) - 100;
        ctx.globalAlpha = .18; ctx.beginPath(); ctx.moveTo(x, y); ctx.quadraticCurveTo(x + 70, y - 3, x + 150, y); ctx.stroke();
      }
      ctx.globalAlpha = 1;
      const boat = 490 + Math.sin(t * .4) * 6;
      ctx.fillStyle = '#1f344b'; ctx.beginPath(); ctx.moveTo(boat - 50, 519); ctx.lineTo(boat + 53, 519); ctx.lineTo(boat + 30, 534); ctx.lineTo(boat - 30, 534); ctx.closePath(); ctx.fill();
      ctx.fillStyle = '#f9e3be'; ctx.beginPath(); ctx.moveTo(boat, 410); ctx.lineTo(boat, 510); ctx.lineTo(boat - 49, 510); ctx.closePath(); ctx.fill();
      ctx.fillStyle = '#cbb4ab'; ctx.beginPath(); ctx.moveTo(boat + 5, 435); ctx.lineTo(boat + 5, 510); ctx.lineTo(boat + 43, 510); ctx.closePath(); ctx.fill();
      if (t < 8) requestAnimationFrame(draw); else recorder.stop();
    }
    requestAnimationFrame(draw);
    await finished; stream.getTracks().forEach(track => track.stop());
    const bytes = new Uint8Array(await new Blob(chunks, { type: 'video/webm' }).arrayBuffer());
    let binary = ''; for (const byte of bytes) binary += String.fromCharCode(byte);
    return btoa(binary);
  });
  await writeFile(resolve(demo, 'sunset.webm'), Buffer.from(videoBase64, 'base64'));
  await film.setViewportSize({ width: 1344, height: 848 });
  await film.setContent(`<style>*{box-sizing:border-box}body{margin:0;padding:24px 32px;background:#171c28;color:#f5f3eb;font:13px "Microsoft YaHei",sans-serif}header{height:40px;display:flex;justify-content:space-between}small{color:#c5bfb2}.screen{position:relative;width:1280px;height:720px;overflow:hidden;border-radius:8px}video,.danmu{position:absolute;inset:0;width:100%;height:100%}.controls{height:40px;display:flex;gap:18px;align-items:center;color:#ddd5c5}.bar{height:3px;flex:1;background:#474c59}.bar:before{content:"";display:block;width:70%;height:100%;background:#ffe56b}</style>
    <header><span>海边日落 · 自制动画视频</span><small>效果演示 · 示例弹幕由本项目原生渲染器生成</small></header>
    <div class="screen"><video muted playsinline src="data:video/webm;base64,${videoBase64}"></video><img class="danmu" src="data:image/png;base64,${overlay}" alt="原生示例弹幕"></div>
    <div class="controls"><span>Ⅱ</span><span>00:07 / 00:08</span><div class="bar"></div><span>22px · 0px描边 · 0.8×</span></div>`);
  await film.locator('video').evaluate(async video => {
    if (video.readyState < 2) await new Promise(resolve => video.addEventListener('loadeddata', resolve, { once: true }));
    await video.play();
    await new Promise(resolve => { video.addEventListener('seeked', resolve, { once: true }); video.currentTime = 7.5; });
    video.pause();
  });
  await expect(film.locator('.danmu')).toHaveJSProperty('complete', true);
  await film.screenshot({ path: resolve(output, 'preview-playback.png') });
  console.log('Captured three labelled README previews: real React UI with fixture data, display settings, and original video plus actual native-renderer example comments.');
} finally {
  if (browser) await browser.close();
  if (server.exitCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
}
