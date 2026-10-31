import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { cp, mkdir, writeFile, readFile, copyFile, rm, readdir, stat, open, rename } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { setTimeout as delay } from 'node:timers/promises';
import { fileHash, inventory, walkFiles, releaseNames } from './release-lib.mjs';
import { sourceState, requireSameSource } from './source-state.mjs';

const root = resolve(import.meta.dirname, '..'), parent = join(tmpdir(), 'updater-runtime-tests');
assert.ok(process.argv.slice(2).every(arg => arg === '--manual-only'), 'Unknown runtime fixture argument.');
const manualOnly = process.argv.includes('--manual-only');
const source = await sourceState(root);
const evidenceParent = join(root, 'artifacts', 'updater-runtime-tests');
const work = join(parent, randomUUID()); await mkdir(work, { recursive: true });
const dotnet = join(root, '.deps/dotnet/dotnet.exe');
const env = { ...process.env, DANMU_API_BASE: '', DOTNET_CLI_HOME: join(root, '.cache/dotnet-home'), DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false', DOTNET_NOLOGO: '1' };
function run(exe, args, timeout = 120000) {
  const result = spawnSync(exe, args, { cwd: root, env, windowsHide: true, encoding: 'utf8', timeout, maxBuffer: 4 * 1024 * 1024 });
  if (result.error) throw new Error(result.error.message + '\n' + result.stdout + result.stderr, { cause: result.error });
  assert.equal(result.status, 0, result.stdout + result.stderr);
}
async function runExit(exe, args) {
  const child = spawn(exe, args, { cwd: root, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
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
async function runParent(exe, instruction) { await runExit(exe, ['--update-parent-fixture', instruction]); }
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
async function manifest(directory, version, variant = 'standard', apiInventory = null) {
  const files = await inventory(directory, apiInventory);
  await writeFile(join(directory, 'manifest.json'), JSON.stringify({ schemaVersion: 1, version, variant, sourceCommit: 'a'.repeat(40), sourceDirty: false, files }));
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
  await publish('tests/NativeDistribution/NativeDistribution.csproj', old, ['-p:AssemblyName=DanmuCompanion', '-p:Version=0.1.1-beta.6', '-p:FileVersion=0.1.1.6']);
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
  for (const mode of manualOnly ? [] : ['worker-death', 'preflight-failure', 'commit', 'health-failure', 'health-failure-probe']) {
    console.log(`Checking updater runtime: ${mode}`);
    const scenario = join(work, mode), application = join(scenario, 'app'), dataRoot = join(scenario, 'data');applications.push(application);
    await cp(old, application, { recursive: true });
    await writeFile(join(application, 'user.txt'), 'foreign bytes must survive');
    await mkdir(join(dataRoot, 'update-runtime'), { recursive: true });
    const preferences = join(dataRoot, 'update-runtime', 'preferences.json');
    const personal = JSON.stringify({ schemaVersion: 1, settings: { shadowStrength: 0.37 } });
    await writeFile(preferences, personal);
    const id = randomUUID().replaceAll('-', ''), operation = join(dataRoot, 'update-runtime', 'updates', id);await mkdir(operation, { recursive: true });
    await writeFile(join(operation, 'owner.json'), JSON.stringify({ schemaVersion: 1, component: 'github-updater', id, purpose: 'download' }));
    const file = join(operation, releaseNames('0.1.1-beta.7').portable);await copyFile(mode === 'health-failure' ? badArchive : mode === 'health-failure-probe' ? badProbeArchive : archive, file);
    const checksum = await fileHash(file);
    const instruction = join(scenario, 'instruction.json'), result = join(scenario, 'parent.json');
    await writeFile(instruction, JSON.stringify({ mode, dataRoot, result, download: { file, checksum, size: (await size(file)).size, version: '0.1.1-beta.7', variant: 'standard', distribution: 'portable', operation, inventoryChecksum: '' } }));
    await runParent(join(application, 'DanmuCompanion.exe'), instruction);
    const parentResult = await waitJson(result, value => value.armed || value.survived);
    assert.equal(parentResult.noBackup, true, 'Customer prepare/commit must not create an old-program backup.');
    if (mode === 'worker-death') {
      assert.equal(parentResult.survived, true);assert.equal(JSON.parse(await readFile(join(application, 'manifest.json'))).version, '0.1.1-beta.6');
      await assert.rejects(() => stat(join(parentResult.transaction, 'backup')), { code: 'ENOENT' });
      checks.push('dead worker refuses commit without stopping the old parent');
    } else {
      const resultFile = join(dataRoot, 'update-runtime', 'updates', 'last-result.json');
      const outcome = await waitJson(resultFile, () => true);
      assert.equal(outcome.status, mode.startsWith('health-failure') ? 'repair-required' : mode === 'preflight-failure' ? 'not-applied' : 'complete');
      assert.equal(outcome.version, mode === 'preflight-failure' ? '0.1.1-beta.6' : '0.1.1-beta.7');
      if (mode.startsWith('health-failure')) assert.match(outcome.detail, /安装器.*修复/);
      else assert.equal(outcome.detail, '', 'Zero-write failure must actually reverify the unmodified old app.');
      assert.equal(await readFile(join(application, 'user.txt'), 'utf8'), 'foreign bytes must survive');
      assert.equal(await readFile(preferences, 'utf8'), personal, 'Actual saved display settings must survive customer update and cleanup.');
      checks.push(mode === 'health-failure-probe' ? 'UI readiness without valid probe requires repair, ignores forged started.json and never restores old bytes' : mode === 'health-failure' ? 'startup failure requires repair without restoring old payload; forged started.json ignored' : mode === 'preflight-failure' ? 'post-exit preflight failure relaunches unmodified old app with real UI/probe health' : 'immediate parent exit, portable apply, real NativeHost UI/probe health handshake');
      run(driver, ['--update-fixture-stop', application]);
      if (mode === 'commit') {
        await assert.rejects(() => stat(parentResult.transaction), { code: 'ENOENT' }, 'Healthy new-host commit must silently reclaim the owned incoming stage metadata.');
        assert.equal(JSON.parse(await readFile(join(application, 'manifest.json'))).version, '0.1.1-beta.7');
        assert.equal(await readFile(join(application, 'user.txt'), 'utf8'), 'foreign bytes must survive');
        assert.equal(await readFile(preferences, 'utf8'), personal);
        checks.push('actual new-host health commit reclaims only incoming stage and metadata; personal/foreign files survive');
      } else {
        await assert.rejects(() => stat(join(parentResult.transaction, 'backup')), { code: 'ENOENT' });
        if (mode.startsWith('health-failure')) {
          assert.equal(JSON.parse(await readFile(join(application, 'manifest.json'))).version, '0.1.1-beta.7', 'Failed applied update must not restore the old manifest.');
          for (const path of await walkFiles(next)) await copyFile(join(next, path), join(application, path));
          const repaired = join(scenario, 'repair-smoke.json');
          run(join(application, 'DanmuCompanion.exe'), ['--smoke-seconds', '3', '--profile', 'update-runtime', '--data-root', dataRoot, '--result', repaired]);
          assert.equal(JSON.parse(await readFile(repaired)).Ready, true, 'Complete incoming package extraction must actually repair UI/probe health.');
          assert.equal(await readFile(preferences, 'utf8'), personal);
          assert.equal(await readFile(join(application, 'user.txt'), 'utf8'), 'foreign bytes must survive');
          checks.push('real failed-health payload repaired by complete incoming extraction at original root, without old bytes or settings loss');
        }
      }
    }
  }
  const manual = join(work, 'manual-launch'), installed = join(manual, 'app'), manualData = join(manual, 'data');applications.push(installed);
  await cp(next, installed, { recursive: true });await mkdir(join(manualData, 'update-runtime'), { recursive: true });
  const manualPersonal = JSON.stringify({ schemaVersion: 1, settings: { shadowStrength: 0.37 } });
  await writeFile(join(manualData, 'update-runtime/preferences.json'), manualPersonal);await writeFile(join(installed, 'user.txt'), 'foreign manual sentinel');
  await runExit(join(next, 'DanmuCompanion.Updater.exe'), ['--launch-installed', installed, '--profile', 'update-runtime', '--data-root', manualData]);
  assert.equal(await readFile(join(manualData, 'update-runtime/preferences.json'), 'utf8'), manualPersonal);
  assert.equal(await readFile(join(installed, 'user.txt'), 'utf8'), 'foreign manual sentinel');
  run(driver, ['--update-fixture-stop', installed]);
  checks.push('manual installer launch holds exact full payload through actual UI/probe readiness and then exits; saved settings and foreign bytes survive');
  const includeStage = join(work, 'include-stage'), includeInstalled = join(work, 'include-manual/app'), includeData = join(work, 'include-manual/data');applications.push(includeInstalled);
  const apiInput = join(root, '.deps/includeapi'), apiReceipt = JSON.parse(await readFile(join(apiInput, 'payload-inventory.json'), 'utf8'));
  await cp(next, includeStage, { recursive: true });await cp(join(apiInput, 'payload/api'), join(includeStage, 'api'), { recursive: true });
  await manifest(includeStage, '0.1.1-beta.7', 'includeAPI', apiReceipt.files);await cp(includeStage, includeInstalled, { recursive: true });
  await mkdir(join(includeData, 'update-runtime'), { recursive: true });await writeFile(join(includeData, 'update-runtime/preferences.json'), manualPersonal);
  await writeFile(join(includeInstalled, 'user.txt'), 'foreign includeAPI sentinel');
  for (const path of ['api/node.exe', 'api/entry.mjs']) { const writable = await open(join(includeInstalled, path), 'r+');await writable.close(); }
  const launch = spawn(join(includeStage, 'DanmuCompanion.Updater.exe'), ['--launch-installed', includeInstalled, '--profile', 'update-runtime', '--data-root', includeData],
    { cwd: root, env, windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  let launchDiagnostic = '';launch.stderr.on('data', bytes => { launchDiagnostic += bytes.toString('utf8').slice(0, Math.max(0, 8192 - launchDiagnostic.length)); });
  const launchExit = new Promise((resolveExit, reject) => { launch.once('error', reject);launch.once('exit', code => resolveExit(code)); });
  launchExit.catch(() => {});
  try {
    const node = join(includeInstalled, 'api/node.exe'), entry = join(includeInstalled, 'api/entry.mjs');let locked = false;
    for (let i = 0; i < 600 && launch.exitCode == null; i++) {
      try { const probe = await open(node, 'r+');await probe.close(); }
      catch (error) { if (['EACCES', 'EPERM', 'EBUSY'].includes(error.code)) { locked = true;break; }throw error; }
      await delay(100);
    }
    assert.equal(locked, true, 'Actual includeAPI startup must acquire a write-denying lock before execution.');
    await assert.rejects(() => rename(node, node + '.outside'), error => ['EACCES', 'EPERM', 'EBUSY'].includes(error.code));
    await assert.rejects(() => writeFile(entry, 'throw new Error("untrusted marker must never execute")'), error => ['EACCES', 'EPERM', 'EBUSY'].includes(error.code));
    let timer;
    const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Actual includeAPI installer-start readiness timed out.')), 120000); });
    let exitCode;try { exitCode = await Promise.race([launchExit, timeout]); } finally { clearTimeout(timer); }
    assert.equal(exitCode, 0, launchDiagnostic);
    assert.equal(await fileHash(entry), await fileHash(join(includeStage, 'api/entry.mjs')));
    assert.equal(await fileHash(node), await fileHash(join(includeStage, 'api/node.exe')));
    assert.equal(await readFile(join(includeData, 'update-runtime/preferences.json'), 'utf8'), manualPersonal);
    assert.equal(await readFile(join(includeInstalled, 'user.txt'), 'utf8'), 'foreign includeAPI sentinel');
    checks.push('actual pinned includeAPI Node and JavaScript: locked replacement attempts denied; installer-start completed real UI/probe/API readiness with personal/foreign bytes intact');
  } finally { if (launch.exitCode == null) launch.kill();run(driver, ['--update-fixture-stop', includeInstalled]); }
  await mkdir(evidenceParent, { recursive: true });
  requireSameSource(source, await sourceState(root));
  const evidence = join(evidenceParent, `verification-${randomUUID()}.json`);await writeFile(evidence, JSON.stringify({ passed: true, checks, sourceCommit: source.head, sourceFingerprint: source.digest, sourceDirty: Boolean(source.status), productionRelease: false }, null, 2));console.log(JSON.stringify({ passed: true, checks, evidence }, null, 2));
} catch (error) {
  await mkdir(evidenceParent, { recursive: true });
  await writeFile(join(evidenceParent, `failure-${randomUUID()}.json`), JSON.stringify({ passed: false, productionRelease: false, sourceCommit: source.head,
    sourceFingerprint: source.digest, checks, error: String(error.message).slice(0, 8192) }, null, 2));
  throw error;
} finally {
  if (driver) for (const application of applications) try { run(driver, ['--update-fixture-stop', application]); } catch { /* Keep the primary failure. */ }
  assert.ok(resolve(work).startsWith(resolve(parent) + sep));await rm(work, { recursive: true, force: true });
}
