import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawnSync } from 'node:child_process';
import { cp, copyFile, mkdir, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { join, resolve, sep } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { releaseNames, walkFiles } from '../scripts/release-lib.mjs';

const root = resolve(import.meta.dirname, '..');
const parent = join(root, 'artifacts', 'simple-installer', 'fixtures');
const results = join(root, 'artifacts', 'simple-installer', 'results');
const menuRoot = join(process.env.APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs');
const quote = value => "'" + value.replaceAll("'", "''") + "'";

function run(executable, args, expected = 0) {
  const result = spawnSync(executable, args, { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 60000 });
  if (result.error) throw result.error;
  if (expected === null) assert.notEqual(result.status, 0, 'Rejected installation must not succeed.');
  else assert.equal(result.status, expected, result.stdout + result.stderr);
  return result;
}
async function untilAbsent(path) {
  for (let i = 0; i < 100; i++) {
    try { await stat(path); } catch (error) { if (error.code === 'ENOENT') return; throw error; }
    await delay(100);
  }
  assert.fail('Logged program path remains after uninstall: ' + path);
}
function registration(id) {
  const key = `HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{${id}}_is1`;
  return run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    `[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); $v=(Get-ItemProperty -LiteralPath ${quote(key)} -ErrorAction SilentlyContinue).InstallLocation; if($v){[Console]::Write($v)}`]).stdout.trim().replace(/[\\/]+$/, '');
}
function shortcut(path, target) {
  run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    `$shell=New-Object -ComObject WScript.Shell; $link=$shell.CreateShortcut(${quote(path)}); $link.TargetPath=${quote(target)}; $link.Save()`]);
}
async function compile(fixture, version, id, label, includeApi = false) {
  const stage = join(fixture, version + (includeApi ? '-api' : ''));
  await mkdir(join(stage, 'assets'), { recursive: true });
  await copyFile(join(root, 'assets', 'app.ico'), join(stage, 'assets', 'app.ico'));
  for (const [name, data] of Object.entries({ 'DanmuCompanion.exe': 'controlled main ' + version, 'stable.dll': 'controlled library ' + version,
    'DISTRIBUTION-LICENSE.txt': 'Controlled installer test license.', 'MicrosoftEdgeWebview2Setup.exe': 'never executed in this fixture' })) await writeFile(join(stage, name), data);
  if (version.endsWith('.6')) await writeFile(join(stage, 'old-only.dll'), 'old logged program file');
  if (includeApi) await cp(join(root, '.deps', 'includeapi', 'payload', 'api'), join(stage, 'api'), { recursive: true });
  const files = await walkFiles(stage), list = join(fixture, version + '-files.iss');
  await writeFile(list, 'procedure AddIncomingPayloadFiles(Files: TStringList);\nbegin\n' + files.map(path => `  Files.Add('${path.replaceAll('/', '\\').replaceAll("'", "''")}');`).join('\n') + '\nend;\n');
  const startup = join(fixture, 'controlled-startup.lnk');
  const cleanup = (await readFile(join(root, 'installer', 'StartupCleanup.iss'), 'utf8')).replace("ExpandConstant('{userstartup}\\爆米花弹幕伴侣.lnk')", quote(startup));
  const cleanupPath = join(fixture, 'StartupCleanup.iss'); await writeFile(cleanupPath, cleanup);
  let script = await readFile(join(root, 'installer', 'DanmuCompanion.iss'), 'utf8');
  script = script.replace(/#define ProductId "[^"]+"/g, `#define ProductId "{{${id}}"`)
    .replace(/#define UninstallKey "[^"]+"/g, `#define UninstallKey "{${id}}_is1"`)
    .replace(/#define ProductName "[^"]+"/g, `#define ProductName "${label}"`)
    .replace(/^AppMutex=.*$/m, `AppMutex=Local\\${label}`)
    .replace(/^Compression=.*$/m, 'Compression=none')
    .replace("if (Result <> '') or HasSharedWebView2 then Exit", "Started := True; if (Result <> '') or Started then Exit");
  for (const include of ['DestinationGuard.iss', 'WebView2Prerequisite.iss']) script = script.replace(`#include "${include}"`, `#include "${join(root, 'installer', include)}"`);
  script = script.replace('#include "StartupCleanup.iss"', `#include "${cleanupPath}"`);
  const scriptPath = join(fixture, version + '-setup.iss'); await writeFile(scriptPath, script);
  run(join(root, '.deps', 'inno', 'ISCC.exe'), ['--quiet', `/DAppVersion=${version}`, `/DAppFileVersion=${version.replace('-beta.', '.')}`,
    `/DPayloadDir=${stage}`, `/DPayloadFileList=${list}`, `/DReleaseDir=${fixture}`, ...(includeApi ? ['/DIncludeApi=1'] : []), scriptPath]);
  return { stage, setup: join(fixture, releaseNames(version, includeApi).installer), files, startup };
}
const install = (setup, destination) => run(setup, ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DIR=' + destination]);
const uninstall = destination => run(join(destination, 'unins000.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART']);

test('ordinary installation, upgrade, missing-file reinstall and logged uninstall work without updater ledgers', { timeout: 240000 }, async () => {
  const id = randomUUID(), fixture = join(parent, id), label = 'DanmuLiteQA-' + id;
  await mkdir(fixture, { recursive: true }); await mkdir(results, { recursive: true });
  const app = join(fixture, '中文 安装位置'), menu = join(menuRoot, label), started = performance.now(); let passed = false;
  try {
    assert.equal(registration(id), '');
    const old = await compile(fixture, '0.1.1-beta.6', id, label), next = await compile(fixture, '0.1.1-beta.7', id, label);
    install(old.setup, app); assert.equal(registration(id).toLowerCase(), app.toLowerCase());
    await stat(join(menu, label + '.lnk'));
    const settings = join(fixture, 'personal-settings.json'); await writeFile(settings, 'settings outside the installation');
    await writeFile(join(app, 'stable.dll'), 'user replacement at program filename');
    install(next.setup, app); assert.equal(await readFile(join(app, 'stable.dll'), 'utf8'), 'controlled library 0.1.1-beta.7');
    await rm(join(app, 'DanmuCompanion.exe')); install(next.setup, app);
    assert.equal(await readFile(join(app, 'DanmuCompanion.exe'), 'utf8'), 'controlled main 0.1.1-beta.7');
    const alternate = join(fixture, 'unregistered relocation');
    run(next.setup, ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DIR=' + alternate], null);
    await assert.rejects(stat(alternate), { code: 'ENOENT' });
    shortcut(next.startup, join(app, 'DanmuCompanion.exe')); uninstall(app);
    await untilAbsent(join(app, 'unins000.exe')); await untilAbsent(app); await untilAbsent(menu); await untilAbsent(next.startup);
    assert.equal(registration(id), ''); assert.equal(await readFile(settings, 'utf8'), 'settings outside the installation');
    install(next.setup, app); shortcut(next.startup, join(process.env.SystemRoot, 'System32', 'notepad.exe')); uninstall(app);
    await untilAbsent(app); await untilAbsent(menu); await stat(next.startup);
    assert.equal(registration(id), ''); passed = true;
  } finally {
    await writeFile(join(results, id + '.json'), JSON.stringify({ passed, scope: 'controlled production-source Inno copy/upgrade/uninstall, not app/player acceptance', elapsedMs: Math.round(performance.now() - started) }, null, 2));
    assert.ok(resolve(fixture).startsWith(resolve(parent) + sep));
    if (passed) await rm(fixture, { recursive: true, force: true, maxRetries: 20, retryDelay: 200 });
    else console.error('Failed lightweight installer fixture retained: ' + fixture);
  }
});

test('includeAPI real payload installs and uninstalls through ordinary Inno within the unchanged limit', { timeout: 240000 }, async () => {
  const id = randomUUID(), fixture = join(parent, id), label = 'DanmuLiteApiQA-' + id;
  await mkdir(fixture, { recursive: true }); await mkdir(results, { recursive: true });
  const app = join(fixture, 'includeAPI installed'), started = performance.now(); let passed = false;
  try {
    const payload = await compile(fixture, '0.1.1-beta.7', id, label, true);
    assert.ok(payload.files.filter(path => path.startsWith('api/')).length >= 4000);
    const before = performance.now(); install(payload.setup, app); const installMs = Math.round(performance.now() - before);
    const apiFiles = await walkFiles(join(app, 'api')); assert.equal(apiFiles.length, payload.files.filter(path => path.startsWith('api/')).length);
    for (const path of ['api/node.exe', 'api/entry.mjs', 'stable.dll']) assert.equal((await stat(join(app, path))).size, (await stat(join(payload.stage, path))).size);
    assert.equal(registration(id).toLowerCase(), app.toLowerCase());
    assert.equal((await readdir(fixture)).includes('.DanmuCompanion-updates'), false);
    uninstall(app); await untilAbsent(app); await untilAbsent(join(menuRoot, label)); assert.equal(registration(id), '');
    await writeFile(join(results, id + '-installation.json'), JSON.stringify({ passed: true, installMs, apiFiles: apiFiles.length, actualPlayerTest: false }, null, 2)); passed = true;
  } finally {
    await writeFile(join(results, id + '.json'), JSON.stringify({ passed, scope: 'real pinned API bytes plus controlled inert main, ordinary Inno, prerequisites mocked available', elapsedMs: Math.round(performance.now() - started) }, null, 2));
    assert.ok(resolve(fixture).startsWith(resolve(parent) + sep));
    if (passed) await rm(fixture, { recursive: true, force: true, maxRetries: 20, retryDelay: 200 });
    else console.error('Failed includeAPI installer fixture retained: ' + fixture);
  }
});
