import { createRequire } from 'node:module';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
const root = resolve(import.meta.dirname, '..');
const require = createRequire(resolve(root, 'ui/package.json'));
const { chromium, expect } = require('@playwright/test');
const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--out' || !args[1])) throw new Error('Usage: node scripts/Capture-PlaybackScreenshot.mjs [--out DIRECTORY]');
const output = args.length ? resolve(args[1]) : resolve(root, 'docs/assets'), demo = resolve(root, '.cache/readme-demo');
await mkdir(output, { recursive: true }); await mkdir(demo, { recursive: true });
const overlay = (await readFile(resolve(demo, 'overlay.png'))).toString('base64');
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
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
  console.log('Captured the labelled original-video and native-renderer demonstration; no player, API or user profile accessed.');
} finally { await browser.close(); }
