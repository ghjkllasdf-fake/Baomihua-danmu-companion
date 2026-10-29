import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile, copyFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(resolve(root, 'ui/package.json'));
const { chromium } = require('@playwright/test');
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

function ico(frames) {
  const header = Buffer.alloc(6 + frames.length * 16);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);
  let offset = header.length;
  frames.forEach(({ size, png }, index) => {
    const entry = 6 + index * 16;
    header[entry] = header[entry + 1] = size === 256 ? 0 : size;
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(png.length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...frames.map(frame => frame.png)]);
}

await mkdir(resolve(root, 'docs/assets'), { recursive: true });
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  for (const name of ['app', 'tray']) {
    const svg = await readFile(resolve(root, `assets/${name}.svg`), 'utf8');
    const previewSize = name === 'app' ? 1024 : 512;
    const page = await browser.newPage({ viewport: { width: previewSize, height: previewSize }, deviceScaleFactor: 1 });
    await page.setContent(`<style>html,body{margin:0;background:transparent}svg{display:block;width:100vw;height:100vh}</style>${svg}`);
    const preview = await page.screenshot({ omitBackground: true });
    await writeFile(resolve(root, `docs/assets/${name}-icon.png`), preview);
    const frames = [];
    for (const size of sizes) {
      await page.setViewportSize({ width: size, height: size });
      frames.push({ size, png: await page.screenshot({ omitBackground: true }) });
    }
    await writeFile(resolve(root, `assets/${name}.ico`), ico(frames));
    await page.close();
  }
  await copyFile(resolve(root, 'assets/app.svg'), resolve(root, 'ui/public/favicon.svg'));
  const app = (await readFile(resolve(root, 'docs/assets/app-icon.png'))).toString('base64');
  const tray = (await readFile(resolve(root, 'docs/assets/tray-icon.png'))).toString('base64');
  const samples = [16, 20, 24, 32].map(size => `<div><img src="data:image/png;base64,${tray}" width="${size}" height="${size}"><span>${size}px</span></div>`).join('');
  const sheet = await browser.newPage({ viewport: { width: 700, height: 340 }, deviceScaleFactor: 1 });
  await sheet.setContent(`<style>
    *{box-sizing:border-box}body{margin:0;padding:24px;display:flex;gap:24px;background:#f5f3eb;font:14px "Segoe UI","Microsoft YaHei",sans-serif;color:#2d2438}
    .main{width:218px;text-align:center}.main>img{display:block;width:208px;height:208px;margin:0 auto 12px}
    .tray{flex:1}.row{display:flex;align-items:center;justify-content:space-around;gap:16px;padding:14px 18px;border-radius:12px;background:#fff;margin:8px 0 14px}
    .row.dark{background:#24232b;color:#fff9e8}.row>div{height:64px;display:flex;flex-direction:column;align-items:center;justify-content:space-between}
    .row span{font-size:11px;opacity:.7}p{margin:0 0 4px}small{color:#686371}
  </style><div class="main"><img src="data:image/png;base64,${app}">应用 / 任务栏图标</div>
  <div class="tray"><p>托盘 · 浅色背景</p><div class="row">${samples}</div><p>托盘 · 深色背景</p><div class="row dark">${samples}</div><small>亮黄 × 珊瑚红 · 电视、爆米花与弹幕点线</small></div>`);
  await sheet.screenshot({ path: resolve(root, 'docs/assets/icon-preview.png') });
  await sheet.close();
  const appSvg = await readFile(resolve(root, 'assets/app.svg'), 'utf8');
  const review = await browser.newPage({ viewport: { width: 980, height: 370 }, deviceScaleFactor: 1 });
  await review.setContent(`<style>
    *{box-sizing:border-box}body{margin:0;padding:24px;background:#f5f3eb;font:14px "Segoe UI","Microsoft YaHei",sans-serif;color:#2d2438}
    h1{font-size:18px;margin:0 0 20px;font-weight:600}.samples{display:flex;align-items:flex-start;justify-content:space-between;gap:20px}
    figure{margin:0;text-align:center;width:180px}.stage{height:200px;display:flex;align-items:center;justify-content:center}
    img,svg{display:block}figcaption{font-size:12px;color:#686371}.gray{filter:grayscale(1)}
    .silhouette svg{width:180px;height:180px}.silhouette #background,.silhouette #contact-shadow{display:none}
    .silhouette [fill]:not([fill="none"]){fill:#2d2438}.silhouette [stroke]{stroke:#2d2438}.silhouette [opacity]{opacity:1}
    p{margin:24px 0 0;color:#686371;font-size:12px}
  </style><h1>尺寸、灰阶与主体轮廓检查</h1><div class="samples">
    <figure><div class="stage"><img src="data:image/png;base64,${app}" width="180" height="180"></div><figcaption>180px · 应用入口</figcaption></figure>
    <figure><div class="stage"><img src="data:image/png;base64,${app}" width="60" height="60"></div><figcaption>60px · 中等尺寸</figcaption></figure>
    <figure><div class="stage"><img src="data:image/png;base64,${app}" width="32" height="32"></div><figcaption>32px · 任务栏</figcaption></figure>
    <figure><div class="stage gray"><img src="data:image/png;base64,${app}" width="180" height="180"></div><figcaption>灰阶 · 明暗分离</figcaption></figure>
    <figure><div class="stage silhouette">${appSvg}</div><figcaption>主体剪影 · 无天线／脚</figcaption></figure>
  </div><p>1024px 原稿另存 app-icon.png；Windows 托盘另有独立的 16–32px 简化图形。</p>`);
  await review.screenshot({ path: resolve(root, 'docs/assets/icon-size-review.png') });
  await review.close();
  console.log('Built application/tray ICO files at 9 Windows sizes, 1024px application artwork, scale/grayscale reviews and the web favicon.');
} finally {
  await browser.close();
}
