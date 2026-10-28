import { chromium, expect } from '@playwright/test';
import assert from 'node:assert/strict';
import { readdir, readFile, writeFile } from 'node:fs/promises';
import { basename, join, resolve } from 'node:path';
import { assessSettingsLatency, assessLoadedSettingsLatency, assertLatencyOwner } from '../../scripts/SettingsLatency.mjs';

const directory = resolve(process.argv[2] ?? '');
const playbackLoad = process.argv.includes('--playback-load');
if (!/^settings-latency-\d{8}-\d{6}-[a-f0-9]{8}$/.test(basename(directory))) throw new Error('A dedicated latency run directory is required.');
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function proveEndpointOwnership(nonce) {
  const versionResponse = await fetch('http://127.0.0.1:9232/json/version', { signal: AbortSignal.timeout(3000) });
  if (!versionResponse.ok) throw new Error('The CDP version endpoint is unavailable.');
  const version = await versionResponse.json();
  const browserEndpoint = new URL(version.webSocketDebuggerUrl);
  if (browserEndpoint.protocol !== 'ws:' || browserEndpoint.hostname !== '127.0.0.1' || browserEndpoint.port !== '9232' ||
    browserEndpoint.username || browserEndpoint.password || browserEndpoint.search || browserEndpoint.hash || !browserEndpoint.pathname.startsWith('/devtools/browser/')) throw new Error('Unexpected browser CDP endpoint.');
  const response = await fetch('http://127.0.0.1:9232/json/list', { signal: AbortSignal.timeout(3000) });
  if (!response.ok) throw new Error('The CDP inventory is unavailable.');
  const targets = await response.json();
  if (!Array.isArray(targets)) throw new Error('Invalid CDP inventory.');
  const pages = targets.filter(target => target.type === 'page' && target.url === 'https://danmu.local/index.html');
  if (pages.length !== 1) throw new Error('Expected one owned CDP candidate.');
  const endpoint = new URL(pages[0].webSocketDebuggerUrl);
  if (endpoint.protocol !== 'ws:' || endpoint.hostname !== '127.0.0.1' || endpoint.port !== '9232' ||
    endpoint.username || endpoint.password || endpoint.search || endpoint.hash || !endpoint.pathname.startsWith('/devtools/page/')) throw new Error('Unexpected CDP endpoint.');
  await new Promise((resolve, reject) => {
    const socket = new WebSocket(endpoint);
    const timeout = setTimeout(() => { socket.close(); reject(new Error('Read-only CDP ownership probe timed out.')); }, 3000);
    socket.addEventListener('error', () => { clearTimeout(timeout); reject(new Error('Read-only CDP ownership probe failed.')); }, { once: true });
    socket.addEventListener('open', () => socket.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: {
      expression: "({url:location.href,nonce:Object.getOwnPropertyDescriptor(globalThis,'__settingsLatencyOwner')?.value})", returnByValue: true,
    } })), { once: true });
    socket.addEventListener('message', event => {
      try {
        const reply = JSON.parse(String(event.data));
        if (reply.id !== 1) return;
        clearTimeout(timeout); socket.close();
        if (reply.error || reply.result?.exceptionDetails) throw new Error('Read-only CDP identity evaluation failed.');
        assertLatencyOwner(nonce, reply.result?.result?.value); resolve();
      } catch (error) { clearTimeout(timeout); socket.close(); reject(error); }
    });
  });
  return browserEndpoint.href;
}
async function readNative() {
  const names = (await readdir(directory)).filter(name => /^native-\d{6}\.json$/.test(name)).sort();
  return names.length ? JSON.parse(await readFile(join(directory, names.at(-1)), 'utf8')) : null;
}
async function nativeUntil(predicate, timeoutMs = 10000) {
  const deadline = performance.now() + timeoutMs;
  while (performance.now() < deadline) {
    let data;
    try { data = await readNative(); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (data?.failure) throw new Error(data.failure);
    if (data && predicate(data)) return data;
    await wait(25);
  }
  throw new Error('Native latency evidence timed out.');
}

let browser, page;
const edits = [], errors = [];
let finalEvidence;
try {
  const initial = await nativeUntil(data => data.ready, playbackLoad ? 60000 : 10000);
  const wrongNonce = (initial.ownerNonce[0] === '0' ? '1' : '0') + initial.ownerNonce.slice(1);
  await assert.rejects(proveEndpointOwnership(wrongNonce), /does not belong to this latency run/);
  expect((await readNative()).deliveries).toHaveLength(0);
  const ownedEndpoint = await proveEndpointOwnership(initial.ownerNonce);
  browser = await chromium.connectOverCDP(ownedEndpoint, { noDefaults: true });
  const pages = browser.contexts().flatMap(context => context.pages()).filter(page => page.url().startsWith('https://danmu.local/'));
  if (pages.length !== 1) throw new Error('Expected exactly one owned local app page.');
  page = pages[0]; page.on('pageerror', error => errors.push(error.message));
  const proveOwnership = async () => assertLatencyOwner(initial.ownerNonce, await page.evaluate(() => ({ url: location.href, nonce: Object.getOwnPropertyDescriptor(window, '__settingsLatencyOwner')?.value })));
  await proveOwnership();
  await expect(page.getByText('桌面宿主已连接')).toBeVisible();
  await page.evaluate(() => {
    window.__latencyInputs = [];
    document.addEventListener('input', event => {
      if (event.target instanceof HTMLInputElement && event.target.type === 'range') {
        window.__latencyInputs.push({ browserMs: performance.now(), timeOriginMs: performance.timeOrigin, value: event.target.value, kind: 'font' });
      }
    }, true);
    document.addEventListener('click', event => {
      const button = event.target instanceof Element ? event.target.closest('button[aria-pressed]') : null;
      if (button && ['1/4', '1/2', '3/4', '全屏'].includes(button.textContent.trim())) {
        window.__latencyInputs.push({ browserMs: performance.now(), timeOriginMs: performance.timeOrigin, value: button.textContent.trim(), kind: 'area' });
      }
    }, true);
  });
  let settings = { ...initial.initialSettings };
  async function accept(id, kind, value) {
    const observed = await page.evaluate(() => window.__latencyInputs);
    expect(observed.length).toBe(edits.length + 1);
    expect(observed.at(-1).kind).toBe(kind); expect(observed.at(-1).value).toBe(String(value));
    edits.push({ id, browserMs: observed.at(-1).browserMs, timeOriginMs: observed.at(-1).timeOriginMs, expectedSettings: { ...settings } });
    const evidence = await nativeUntil(data => data.deliveries.length >= edits.length);
    expect(evidence.deliveries).toHaveLength(edits.length);
    expect(evidence.deliveries.at(-1).matchesCommitted).toBe(true);
    expect(evidence.deliveries.at(-1).settings).toEqual(settings);
    await expect(page.locator('footer')).toContainText('显示偏好已保存');
  }
  const font = page.getByRole('slider', { name: '弹幕字号' });
  expect(Number(await font.inputValue())).toBe(28);
  for (let i = 1; i <= 20; i++) {
    settings = { ...settings, fontSize: 28 + i };
    await proveOwnership();
    await font.press('ArrowRight');
    await accept(`font-${i}`, 'font', settings.fontSize);
  }
  for (const [label, fraction] of [['1/4', .25], ['3/4', .75], ['全屏', 1], ['1/2', .5]]) {
    settings = { ...settings, areaFraction: fraction };
    await proveOwnership();
    await page.getByRole('button', { name: label, exact: true }).click();
    await accept(`area-${fraction}`, 'area', label);
  }
  expect(edits).toHaveLength(24);
  await writeFile(join(directory, 'control.txt'), 'calibrate-after');
  const native = await nativeUntil(data => data.finished);
  finalEvidence = { ...native, edits, errors, ownershipNegativeRejected: true, scope: 'Twenty discrete keyboard font edits and four region clicks; document capture-listener timestamp to actual Configure completion. Optional playback load is verified at start, every configuration delivery and end; not physical input arrival, presentation, continuous dragging or all controls.' };
  finalEvidence.assessment = playbackLoad ? assessLoadedSettingsLatency(finalEvidence) : assessSettingsLatency(finalEvidence);
  expect(errors).toEqual([]);
  console.log(JSON.stringify(finalEvidence.assessment, null, 2));
  if (!finalEvidence.assessment.passed) throw new Error('The conservative 100ms setting-delivery budget was exceeded.');
} finally {
  if (!finalEvidence) {
    let native = {};
    try { native = await readNative() ?? {}; } catch { /* Preserve partial evidence if the owned host failed before output. */ }
    finalEvidence = { ...native, edits, errors, incomplete: true };
  }
  const output = resolve(directory, '..', '..', 'artifacts', `${basename(directory)}.json`);
  await writeFile(output, JSON.stringify(finalEvidence, null, 2));
  await writeFile(join(directory, 'control.txt'), 'close');
  if (browser) await browser.close();
  console.log(`Evidence: ${output}`);
}
