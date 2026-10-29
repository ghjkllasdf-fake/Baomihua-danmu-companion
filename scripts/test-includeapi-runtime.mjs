import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFile, spawn } from 'node:child_process';
import { mkdtemp, mkdir, readFile, realpath, rm, stat, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

// Run only with BaoMiHua closed: its automatic matcher otherwise makes real provider requests.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(join(root, 'ui', 'package.json'));
const { chromium, expect } = require('@playwright/test');
const execFileAsync = promisify(execFile);
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const hash = data => createHash('sha256').update(data).digest('hex');
let appDir, evidence;
for (let i = 2; i < process.argv.length; i++) {
  const arg = process.argv[i];
  if ((arg !== '--app' && arg !== '--evidence') || !process.argv[i + 1]) throw new Error('Usage: node scripts/test-includeapi-runtime.mjs --app ABSOLUTE_PAYLOAD_DIR --evidence NEW_ABSOLUTE_DIR');
  const value = process.argv[++i];
  if (!isAbsolute(value)) throw new Error('Both paths must be absolute.');
  if (arg === '--app' && !appDir) appDir = resolve(value);
  else if (arg === '--evidence' && !evidence) evidence = resolve(value);
  else throw new Error('Repeated argument.');
}
if (!appDir || !evidence || process.platform !== 'win32') throw new Error('Windows, an application directory and a new evidence directory are required.');
await stat(join(appDir, 'DanmuCompanion.exe'));
await stat(join(appDir, 'api', 'node.exe'));
await mkdir(evidence); // Exclusive: never overwrite prior evidence.
const tempParent = await realpath(tmpdir());
const dataRoot = await mkdtemp(join(tempParent, 'includeapi-runtime-qa-'));
const profile = join(dataRoot, 'isolated');
await mkdir(profile);
const sentinelPath = join(profile, 'api.dat');
const sentinel = Buffer.from('includeapi-isolated-old-api-sentinel\n');
await writeFile(sentinelPath, sentinel, { flag: 'wx' });
const observations = { scenario: 'includeAPI actual native runtime, isolated profile', checks: {}, limitations: [
  'Process snapshots are bounded observations, not an event-complete trace.',
  'BaoMiHua must remain closed throughout the run to prevent automatic provider searches.',
] };
let stage = 'preflight', app, browser, page, exitPromise, seen = new Map(), cleanExit = true;
const mark = key => { observations.checks[key] = true; };

// Query only explicitly named processes or descendants of this test's app; never read command lines.
async function ps(script) {
  const encoded = Buffer.from("[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; " + script, 'utf16le').toString('base64');
  const { stdout } = await execFileAsync('powershell.exe', ['-NoProfile', '-NonInteractive', '-EncodedCommand', encoded], { windowsHide: true, timeout: 20000, maxBuffer: 1024 * 1024, encoding: 'utf8' });
  return JSON.parse(stdout.trim());
}
async function playerAbsent() {
  const count = await ps("@(Get-CimInstance Win32_Process -Filter \"Name='BaoMiHua.exe'\" -ErrorAction Stop).Count | ConvertTo-Json -Compress");
  if (count !== 0) throw new Error('Playback precondition failed.');
}
async function descendants(pid) {
  assert.ok(Number.isInteger(pid) && pid > 0);
  const code = `$front=@(${pid}); $all=@(); for($level=0;$level -lt 8 -and $front.Count -gt 0;$level++){ $next=@(); foreach($p in $front){ $children=@(Get-CimInstance Win32_Process -Filter ("ParentProcessId = " + $p)); foreach($c in $children){ $all+=@{id=[int]$c.ProcessId;parent=[int]$c.ParentProcessId;name=$c.Name;image=$c.ExecutablePath;started=$c.CreationDate.ToUniversalTime().Ticks.ToString()}; $next+=([int]$c.ProcessId); if($all.Count -gt 100){throw 'Too many children'} } }; $front=$next }; ConvertTo-Json -InputObject @($all) -Compress`;
  const found = await ps(code);
  for (const item of found) seen.set(`${item.id}:${item.started}`, item);
  return found;
}
async function listeners(pid) {
  assert.ok(Number.isInteger(pid) && pid > 0);
  return ps(`$a=@(Get-NetTCPConnection -OwningProcess ${pid} -State Listen -ErrorAction SilentlyContinue | ForEach-Object {$_.LocalAddress}); ConvertTo-Json -InputObject @($a) -Compress`);
}
async function cdpOwner(port, children) {
  const rows = await ps(`$a=@(Get-NetTCPConnection -LocalPort ${port} -State Listen -ErrorAction SilentlyContinue | ForEach-Object {@{id=[int]$_.OwningProcess;address=$_.LocalAddress}}); ConvertTo-Json -InputObject @($a) -Compress`);
  return rows.some(row => row.address === '127.0.0.1' && children.some(c => c.id === row.id && c.name.toLowerCase() === 'msedgewebview2.exe'));
}
async function stillAlive(items) {
  if (!items.length) return [];
  const filter = items.map(item => `ProcessId = ${item.id}`).join(' OR ');
  const processes = await ps(`$a=@(Get-CimInstance Win32_Process -Filter '${filter}' | ForEach-Object {@{id=[int]$_.ProcessId;started=$_.CreationDate.ToUniversalTime().Ticks.ToString()}}); ConvertTo-Json -InputObject @($a) -Compress`);
  return items.filter(item => processes.some(p => p.id === item.id && p.started === item.started));
}
async function waitFor(fn, timeout = 30000) {
  const end = Date.now() + timeout;
  do { const value = await fn(); if (value) return value; await sleep(350); } while (Date.now() < end);
  throw new Error('Observation timed out.');
}
async function freePort() {
  const server = createServer();
  await new Promise((ok, fail) => { server.once('error', fail); server.listen(0, '127.0.0.1', ok); });
  const port = server.address().port;
  await new Promise((ok, fail) => server.close(error => error ? fail(error) : ok()));
  return port;
}
async function ownedNode() {
  const nodes = (await descendants(app.pid)).filter(item => item.name.toLowerCase() === 'node.exe');
  const expected = join(appDir, 'api', 'node.exe').toLowerCase();
  if (nodes.length !== 1 || !nodes[0].image || resolve(nodes[0].image).toLowerCase() !== expected) return null;
  const addresses = await listeners(nodes[0].id);
  if (addresses.length !== 1 || addresses[0] !== '127.0.0.1') return null;
  return nodes[0];
}
async function safeView() {
  const body = await page.locator('body').innerText();
  const values = await page.locator('input').evaluateAll(inputs => inputs.map(input => input.value));
  if (/127\.0\.0\.1|localhost|[a-f0-9]{64}/i.test(body + values.join(' '))) throw new Error('Unsafe display content.');
  await expect(page.getByLabel('API 地址', { exact: true })).toHaveCount(0);
}
async function selectedLabels() {
  return page.locator('.local-source-list input:checked').evaluateAll(inputs => inputs.map(input => input.getAttribute('aria-label')));
}
async function startApp() {
  await playerAbsent();
  const port = await freePort();
  app = spawn(join(appDir, 'DanmuCompanion.exe'), ['--open', '--profile', 'isolated', '--data-root', dataRoot, '--debug-port', String(port), '--smoke-seconds', '120'], {
    cwd: appDir, stdio: 'ignore', windowsHide: false,
  });
  exitPromise = new Promise((ok, fail) => { app.once('exit', (code, signal) => ok({ code, signal })); app.once('error', fail); });
  await waitFor(async () => {
    if (app.exitCode !== null || app.signalCode !== null) throw new Error('Application exited early.');
    const children = await descendants(app.pid);
    return await cdpOwner(port, children);
  }, 60000);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { timeout: 10000 });
  page = await waitFor(() => browser.contexts().flatMap(context => context.pages()).find(item => item.url().startsWith('https://danmu.local/')), 30000);
  await expect(page.getByText('桌面宿主已连接')).toBeVisible({ timeout: 30000 });
  await expect(page.getByText('本机弹幕服务已就绪，仅允许本机连接。')).toBeVisible({ timeout: 30000 });
  await playerAbsent();
  await safeView();
}
async function closeApp() {
  if (page && !page.isClosed()) await page.evaluate(() => window.chrome.webview.postMessage({ v: 1, id: 'qa-close', type: 'close', payload: {} }));
  const result = await Promise.race([exitPromise, sleep(15000).then(() => null)]);
  if (!result || result.code !== 0) throw new Error('Graceful exit failed.');
  if (browser) { await browser.close().catch(() => {}); browser = undefined; page = undefined; }
  app = undefined;
}
try {
  stage = 'initial launch'; await startApp(); mark('isolated_app_ready');
  const first = await waitFor(ownedNode); mark('one_owned_node_loopback_only');
  stage = 'source change';
  await page.getByText('选择弹幕来源', { exact: false }).click();
  await page.getByRole('checkbox', { name: '启用 腾讯视频' }).check();
  await page.getByRole('button', { name: '上移 腾讯视频' }).click();
  await playerAbsent();
  await page.getByRole('button', { name: '应用来源' }).click();
  await expect(page.getByRole('button', { name: '已应用' })).toBeDisabled({ timeout: 60000 });
  const second = await waitFor(ownedNode, 60000);
  assert.notEqual(first.id, second.id);
  assert.equal((await stillAlive([first])).length, 0);
  const saved = JSON.parse(await readFile(join(profile, 'embedded-api', 'sources.json'), 'utf8'));
  assert.deepEqual(saved.sources, ['douban', '360', 'renren', 'tencent', 'hanjutv']);
  assert.deepEqual(await selectedLabels(), ['启用 豆瓣', '启用 360', '启用 人人视频', '启用 腾讯视频', '启用 韩剧TV']);
  assert.equal(hash(await readFile(sentinelPath)), hash(sentinel));
  await safeView(); await playerAbsent();
  await page.screenshot({ path: join(evidence, 'local-sources.png'), fullPage: true });
  mark('ui_selection_order_saved'); mark('node_restarted_old_child_reaped'); mark('legacy_api_sentinel_unchanged');
  stage = 'close and relaunch'; await closeApp();
  await waitFor(async () => (await stillAlive([...seen.values()])).length === 0, 30000);
  seen = new Map();
  await startApp(); await waitFor(ownedNode);
  await page.getByText('选择弹幕来源', { exact: false }).click();
  await expect(page.getByRole('checkbox', { name: '启用 腾讯视频' })).toBeChecked();
  assert.deepEqual(await selectedLabels(), ['启用 豆瓣', '启用 360', '启用 人人视频', '启用 腾讯视频', '启用 韩剧TV']);
  assert.deepEqual(JSON.parse(await readFile(join(profile, 'embedded-api', 'sources.json'), 'utf8')).sources, saved.sources);
  assert.equal(hash(await readFile(sentinelPath)), hash(sentinel));
  await safeView(); await playerAbsent();
  mark('selection_persists_after_relaunch'); mark('legacy_api_sentinel_unchanged_after_relaunch'); mark('no_address_token_or_api_input_rendered');
  stage = 'final close'; await closeApp();
  await waitFor(async () => (await stillAlive([...seen.values()])).length === 0, 30000);
  mark('observed_owned_descendants_exited');
  observations.passed = true;
} catch {
  observations.passed = false; observations.failedStage = stage;
} finally {
  if (app) {
    await descendants(app.pid).catch(() => {});
    try { if (page && !page.isClosed()) await page.evaluate(() => window.chrome.webview.postMessage({ v: 1, id: 'qa-close-finally', type: 'close', payload: {} })); } catch {}
    let result = await Promise.race([exitPromise.catch(() => null), sleep(5000).then(() => null)]);
    if (!result) { app.kill(); result = await Promise.race([exitPromise.catch(() => null), sleep(5000).then(() => null)]); }
    cleanExit = Boolean(result);
  }
  if (browser) await browser.close().catch(() => {});
  if (cleanExit && seen.size) {
    try { await waitFor(async () => (await stillAlive([...seen.values()])).length === 0, 10000); }
    catch { cleanExit = false; }
  }
  // Only remove the fresh directory created by this invocation, and only after the parent exits.
  const rel = relative(tempParent, dataRoot);
  if (cleanExit && rel && !rel.startsWith('..' + sep) && !isAbsolute(rel) && dirname(dataRoot) === tempParent)
    await rm(dataRoot, { recursive: true, force: true, maxRetries: 5, retryDelay: 500 }).catch(() => { observations.cleanup = 'temporary profile could not be removed'; });
  else observations.cleanup = 'temporary profile retained because process exit was not confirmed';
  if (observations.cleanup) observations.passed = false;
  await writeFile(join(evidence, 'runtime-evidence.json'), JSON.stringify(observations, null, 2) + '\n', { flag: 'wx' });
}
console.log(observations.passed ? 'PASS: isolated includeAPI UI, persistence and observed process lifecycle.' : `FAIL: isolated includeAPI QA at ${observations.failedStage || 'cleanup'}.`);
if (!observations.passed || observations.cleanup) process.exitCode = 1;
