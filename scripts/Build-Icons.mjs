import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(resolve(root, 'ui/package.json'));
const { chromium } = require('@playwright/test');
const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--out' || !args[1])) throw new Error('Usage: node scripts/Build-Icons.mjs [--out DIRECTORY]');
const output = args.length ? resolve(args[1]) : root;

function icoFrames(bytes) {
  if (bytes.length < 6 || bytes.readUInt16LE(0) !== 0 || bytes.readUInt16LE(2) !== 1) throw new Error('Invalid ICO header.');
  const count = bytes.readUInt16LE(4), directory = 6 + count * 16, frames = new Map();
  if (!count || directory > bytes.length) throw new Error('Invalid ICO directory.');
  for (let index = 0; index < count; index++) {
    const entry = 6 + index * 16, size = bytes[entry] || 256;
    const length = bytes.readUInt32LE(entry + 8), offset = bytes.readUInt32LE(entry + 12);
    if ((bytes[entry + 1] || 256) !== size || frames.has(size) || offset < directory || length < 24 || offset + length > bytes.length) throw new Error('Invalid ICO frame bounds.');
    const png = bytes.subarray(offset, offset + length);
    if (png.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a' || png.readUInt32BE(16) !== size || png.readUInt32BE(20) !== size) throw new Error('ICO frame must contain its declared PNG dimensions.');
    frames.set(size, `data:image/png;base64,${png.toString('base64')}`);
  }
  return frames;
}

// Small ICO frames are separately optimized artwork, not scaled copies of the large SVG.
const icons = {}, artwork = {}, frames = {};
for (const name of ['app', 'tray']) {
  icons[name] = await readFile(resolve(root, `assets/${name}.ico`));
  frames[name] = icoFrames(icons[name]);
  artwork[name] = await readFile(resolve(root, `docs/assets/${name}-icon.png`));
}
function frame(name, size) {
  const image = frames[name].get(size);
  if (!image) throw new Error(`Missing ${size}px ${name} ICO frame.`);
  return image;
}
await mkdir(resolve(output, 'docs/assets'), { recursive: true });
await mkdir(resolve(output, 'ui/public'), { recursive: true });
for (const name of ['app', 'tray']) await writeFile(resolve(output, `docs/assets/${name}-icon.png`), artwork[name]);
await writeFile(resolve(output, 'ui/public/favicon.ico'), icons.app);
await writeFile(resolve(output, 'ui/public/favicon.svg'), await readFile(resolve(root, 'assets/app.svg')));
const browser = await chromium.launch({ channel: 'msedge', headless: true });
try {
  const app = artwork.app.toString('base64');
  const samples = [16, 20, 24, 32].map(size => `<div><img src="${frame('tray', size)}" width="${size}" height="${size}"><span>${size}px</span></div>`).join('');
  const sheet = await browser.newPage({ viewport: { width: 700, height: 340 }, deviceScaleFactor: 1 });
  await sheet.setContent(`<style>
    *{box-sizing:border-box}body{margin:0;padding:24px;display:flex;gap:24px;background:#f5f3eb;font:14px "Segoe UI","Microsoft YaHei",sans-serif;color:#2d2438}
    .main{width:218px;text-align:center}.main>img{display:block;width:208px;height:208px;margin:0 auto 12px}
    .tray{flex:1}.row{display:flex;align-items:center;justify-content:space-around;gap:16px;padding:14px 18px;border-radius:12px;background:#fff;margin:8px 0 14px}
    .row.dark{background:#24232b;color:#fff9e8}.row>div{height:64px;display:flex;flex-direction:column;align-items:center;justify-content:space-between}
    .row span{font-size:11px;opacity:.7}p{margin:0 0 4px}small{color:#686371}
  </style><div class="main"><img src="data:image/png;base64,${app}">应用 / 任务栏图标</div>
  <div class="tray"><p>托盘 · 浅色背景</p><div class="row">${samples}</div><p>托盘 · 深色背景</p><div class="row dark">${samples}</div><small>爆米花桶与弹幕气泡 · 直接读取专用 ICO 帧</small></div>`);
  await sheet.locator('img').evaluateAll(images => Promise.all(images.map(image => image.decode())));
  await sheet.screenshot({ path: resolve(output, 'docs/assets/icon-preview.png') });
  await sheet.close();
  const review = await browser.newPage({ viewport: { width: 980, height: 370 }, deviceScaleFactor: 1 });
  await review.setContent(`<style>
    *{box-sizing:border-box}body{margin:0;padding:24px;background:#f5f3eb;font:14px "Segoe UI","Microsoft YaHei",sans-serif;color:#2d2438}
    h1{font-size:18px;margin:0 0 20px;font-weight:600}.samples{display:flex;align-items:flex-start;justify-content:space-between;gap:20px}
    figure{margin:0;text-align:center;width:180px}.stage{height:200px;display:flex;align-items:center;justify-content:center}
    img{display:block}figcaption{font-size:12px;color:#686371}.gray{filter:grayscale(1)}
    p{margin:24px 0 0;color:#686371;font-size:12px}
  </style><h1>原始图标帧与灰阶检查</h1><div class="samples">
    <figure><div class="stage"><img src="data:image/png;base64,${app}" width="180" height="180"></div><figcaption>180px · 应用入口</figcaption></figure>
    <figure><div class="stage"><img src="${frame('app', 64)}" width="64" height="64"></div><figcaption>64px · 原始 ICO 帧</figcaption></figure>
    <figure><div class="stage"><img src="${frame('app', 32)}" width="32" height="32"></div><figcaption>32px · 任务栏专用帧</figcaption></figure>
    <figure><div class="stage gray"><img src="data:image/png;base64,${app}" width="180" height="180"></div><figcaption>灰阶 · 明暗分离</figcaption></figure>
    <figure><div class="stage"><img src="${frame('tray', 16)}" width="16" height="16"></div><figcaption>16px · 托盘专用帧</figcaption></figure>
  </div><p>小尺寸直接采用提供的 ICO 对应帧，不从大图缩放生成；ICO 源文件保持原样。</p>`);
  await review.locator('img').evaluateAll(images => Promise.all(images.map(image => image.decode())));
  await review.screenshot({ path: resolve(output, 'docs/assets/icon-size-review.png') });
  await review.close();
  console.log(`Preserved ${frames.app.size} application and ${frames.tray.size} tray ICO frames; refreshed previews and browser icons.`);
} finally {
  await browser.close();
}
