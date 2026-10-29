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
    const page = await browser.newPage({ viewport: { width: 512, height: 512 }, deviceScaleFactor: 1 });
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
  console.log('Built application/tray ICO files at 9 Windows sizes, PNG previews and the web favicon.');
} finally {
  await browser.close();
}
