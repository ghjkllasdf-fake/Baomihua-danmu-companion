import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { cp, mkdir, writeFile, readFile, copyFile, rm, readdir } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { setTimeout as delay } from 'node:timers/promises';
import { fileHash, inventory, walkFiles, releaseNames } from './release-lib.mjs';

const root = resolve(import.meta.dirname, '..'), parent = join(tmpdir(), 'updater-runtime-tests');
const evidenceParent = join(root, 'artifacts', 'updater-runtime-tests');
const work = join(parent, randomUUID()); await mkdir(work, { recursive: true });
const dotnet = join(root, '.deps/dotnet/dotnet.exe');
const env = { ...process.env, DANMU_API_BASE: '', DOTNET_CLI_HOME: join(root, '.cache/dotnet-home'), DOTNET_NOLOGO: '1' };
function run(exe, args, timeout = 120000) {
  const result = spawnSync(exe, args, { cwd: root, env, windowsHide: true, encoding: 'utf8', timeout, maxBuffer: 4 * 1024 * 1024 });
  if (result.error) throw new Error(result.error.message + '\n' + result.stdout + result.stderr, { cause: result.error });
  assert.equal(result.status, 0, result.stdout + result.stderr);
}
async function runParent(exe, instruction) {
  const child = spawn(exe, ['--update-parent-fixture', instruction], { cwd: root, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = '';
  for (const stream of [child.stdout, child.stderr]) stream.on('data', bytes => { if (output.length < 8192) output += bytes.toString('utf8').slice(0, 8192 - output.length); });
  // Exit observes the retained parent, unlike close/spawnSync which also waits for inherited pipe handles.
  await new Promise((resolveExit, reject) => {
    const timer = setTimeout(() => { child.kill(); reject(new Error('Controlled updater parent did not exit.\n' + output)); }, 240000);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', code => {
      clearTimeout(timer); child.stdout.destroy(); child.stderr.destroy();
      if (code === 0) resolveExit(); else reject(new Error('Controlled updater parent failed.\n' + output));
    });
  });
}
async function publish(project, output, overrides = []) {
  console.log(`Publishing controlled fixture: ${project}`);
  run(dotnet, ['restore', project, '--locked-mode', '--source', '.deps/nuget']);
  run(dotnet, ['publish', project, '--no-restore', '-c', 'Release', '-o', output, '-p:DebugType=none', ...overrides]);
}
async function merge(source, target) {
  for (const path of await walkFiles(source)) {
    if (/\.(pdb|xml)$/.test(path)) continue;
    await mkdir(resolve(target, path, '..'), { recursive: true });
    await copyFile(join(source, path), join(target, path));
  }
}
async function manifest(directory, version) {
  const files = await inventory(directory);
  await writeFile(join(directory, 'manifest.json'), JSON.stringify({ schemaVersion: 1, version, variant: 'standard', sourceCommit: 'a'.repeat(40), sourceDirty: false, files }));
}
async function waitJson(path, predicate, seconds = 75) {
  for (let i = 0; i < seconds * 5; i++) {
    try { const value = JSON.parse(await readFile(path, 'utf8')); if (predicate(value)) return value; } catch (error) { if (error.code !== 'ENOENT' && !(error instanceof SyntaxError)) throw error; }
    await delay(200);
  }
  throw new Error(`Timed out waiting for owned fixture result: ${path}`);
}
const checks = [], applications = [];
let driver;
try {
  const old = join(work, 'old'), next = join(work, 'next'), updater = join(work, 'updater'), probe = join(work, 'probe');
  await publish('tests/NativeDistribution/NativeDistribution.csproj', old, ['-p:AssemblyName=DanmuCompanion']);
  await publish('desktop/NativeHost/NativeHost.csproj', next, ['-p:Version=0.1.1-beta.7', '-p:FileVersion=0.1.1.7']);
  await publish('desktop/NativeUpdater/NativeUpdater.csproj', updater);
  await publish('desktop/NativeProbe/NativeProbe.csproj', probe);
  for (const target of [old, next]) {
    await merge(updater, target); await merge(probe, target);
    await cp(join(root, 'ui/dist'), join(target, 'ui/dist'), { recursive: true });
    await cp(join(root, 'assets'), join(target, 'assets'), { recursive: true, filter: path => !/\.(svg|png)$/.test(path) });
    await copyFile(join(root, '.deps/webview2/runtimes/win-x64/native/WebView2Loader.dll'), join(target, 'WebView2Loader.dll'));
  }
  // Only the application ICOs belong to the controlled payload.
  for (const target of [old, next]) {
    for (const path of await walkFiles(target)) if (/\.(pdb|xml)$/.test(path)) await rm(join(target, path));
    for (const name of await readdir(join(target, 'assets'))) if (!['app.ico', 'tray.ico'].includes(name)) await rm(join(target, 'assets', name), { recursive: true });
  }
  await manifest(old, '0.1.1-beta.6'); await manifest(next, '0.1.1-beta.7');
  driver = join(old, 'DanmuCompanion.exe');
  run(dotnet, ['restore', 'scripts/NativeReleaseTools/NativeReleaseTools.csproj', '--locked-mode', '--source', '.deps/nuget']);
  run(dotnet, ['build', 'scripts/NativeReleaseTools/NativeReleaseTools.csproj', '--no-restore', '-c', 'Release']);
  const tools = join(root, 'scripts/NativeReleaseTools/bin/Release/net10.0/win-x64/NativeReleaseTools.exe');
  const archive = join(work, releaseNames('0.1.1-beta.7').portable);run(tools, ['pack', next, archive]);
  const bad = join(work, 'bad'); await cp(next, bad, { recursive: true });
  await writeFile(join(bad, 'DanmuCompanion.exe'), 'controlled invalid PE'); await rm(join(bad, 'manifest.json')); await manifest(bad, '0.1.1-beta.7');
  const badArchive = join(work, 'bad.zip'); run(tools, ['pack', bad, badArchive]);
  const badProbe = join(work, 'bad-probe'); await cp(next, badProbe, { recursive: true });
  await writeFile(join(badProbe, 'DanmuCompanion.Probe.exe'), 'controlled invalid probe PE'); await rm(join(badProbe, 'manifest.json')); await manifest(badProbe, '0.1.1-beta.7');
  const badProbeArchive = join(work, 'bad-probe.zip'); run(tools, ['pack', badProbe, badProbeArchive]);
  const size = (await import('node:fs/promises')).stat;
  for (const mode of ['worker-death', 'preflight-failure', 'commit', 'rollback', 'rollback-probe']) {
    console.log(`Checking updater runtime: ${mode}`);
    const scenario = join(work, mode), application = join(scenario, 'app'), dataRoot = join(scenario, 'data');applications.push(application);
    await cp(old, application, { recursive: true });
    await writeFile(join(application, 'user.txt'), 'foreign bytes must survive');
    const id = randomUUID().replaceAll('-', ''), operation = join(dataRoot, 'update-runtime', 'updates', id);await mkdir(operation, { recursive: true });
    await writeFile(join(operation, 'owner.json'), JSON.stringify({ schemaVersion: 1, component: 'github-updater', id, purpose: 'download' }));
    const file = join(operation, releaseNames('0.1.1-beta.7').portable);await copyFile(mode === 'rollback' ? badArchive : mode === 'rollback-probe' ? badProbeArchive : archive, file);
    const checksum = await fileHash(file);
    const instruction = join(scenario, 'instruction.json'), result = join(scenario, 'parent.json');
    await writeFile(instruction, JSON.stringify({ mode, dataRoot, result, download: { file, checksum, size: (await size(file)).size, version: '0.1.1-beta.7', variant: 'standard', distribution: 'portable', operation, inventoryChecksum: '' } }));
    await runParent(join(application, 'DanmuCompanion.exe'), instruction);
    const parentResult = await waitJson(result, value => value.armed || value.survived);
    if (mode === 'worker-death') {
      assert.equal(parentResult.survived, true);assert.equal(JSON.parse(await readFile(join(application, 'manifest.json'))).version, '0.1.1-beta.6');
      checks.push('dead worker refuses commit without stopping the old parent');
    } else {
      const resultFile = join(dataRoot, 'update-runtime', 'updates', 'last-result.json');
      const outcome = await waitJson(resultFile, () => true);
      assert.equal(outcome.status, mode.startsWith('rollback') ? 'rolledback' : mode === 'preflight-failure' ? 'not-applied' : 'complete');
      assert.equal(outcome.version, mode === 'commit' ? '0.1.1-beta.7' : '0.1.1-beta.6');
      assert.equal(outcome.detail, '', 'A no-mutation failure must actually reverify the restarted old app, not silently leave it stopped.');
      assert.equal(await readFile(join(application, 'user.txt'), 'utf8'), 'foreign bytes must survive');
      checks.push(mode === 'rollback-probe' ? 'UI readiness without a valid probe cannot commit; rollback verifies old UI/probe health and ignores forged started.json' : mode === 'rollback' ? 'startup failure restores old payload and verifies real UI/probe health; forged started.json ignored' : mode === 'preflight-failure' ? 'post-exit preflight failure relaunches unmodified old app with real UI/probe health' : 'immediate parent exit, portable apply, real NativeHost UI/probe health handshake');
      run(driver, ['--update-fixture-stop', application]);
    }
  }
  await mkdir(evidenceParent, { recursive: true });
  const evidence = join(evidenceParent, `verification-${randomUUID()}.json`);await writeFile(evidence, JSON.stringify({ passed: true, checks, productionRelease: false }, null, 2));console.log(JSON.stringify({ passed: true, checks, evidence }, null, 2));
} finally {
  if (driver) for (const application of applications) try { run(driver, ['--update-fixture-stop', application]); } catch { /* Keep the primary failure. */ }
  assert.ok(resolve(work).startsWith(resolve(parent) + sep));await rm(work, { recursive: true, force: true });
}
