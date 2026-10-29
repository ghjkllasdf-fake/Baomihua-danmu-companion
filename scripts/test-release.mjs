import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdir, readFile, writeFile, rename, rm, rmdir, realpath, stat } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { join, resolve, sep } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileHash, inventory, releaseNames } from './release-lib.mjs';

const root = resolve(import.meta.dirname, '..');
let directory = join(root, 'artifacts', 'releases'), version = '0.1.0-beta.1';
let evidence = join(root, 'artifacts', 'native-beta', 'final-validation');
for (let i = 2; i < process.argv.length; i++) {
  if (process.argv[i] === '--directory') directory = resolve(process.argv[++i]);
  else if (process.argv[i] === '--evidence') evidence = resolve(process.argv[++i]);
  else if (process.argv[i] === '--version') version = process.argv[++i];
  else throw new Error('Unknown release test argument.');
}
const names = releaseNames(version), id = randomUUID();
const qaParent = join(root, 'artifacts', 'native-qa'), qa = join(qaParent, `beta-${id}`);
const install = join(qa, '\u5b89\u88c5\u4f4d\u7f6e Beta'), portable = join(qa, '\u79fb\u52a8\u540e\u7684 Portable \u4e2d\u6587');
// The installer hides the group page, so Inno intentionally ignores /GROUP.
const group = '\u7206\u7c73\u82b1\u5f39\u5e55\u4f34\u4fa3';
const menu = join(process.env.APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs', group);
const link = join(menu, '\u7206\u7c73\u82b1\u5f39\u5e55\u4f34\u4fa3.lnk');
const tools = join(root, 'scripts', 'NativeReleaseTools', 'bin', 'Release', 'net10.0', 'win-x64', 'NativeReleaseTools.exe');
const checks = [], artifacts = [];
let sourceCommit = null, payloadFiles = null;
function run(exe, args, expected = 0) {
  const result = spawnSync(exe, args, { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 240000, maxBuffer: 8 * 1024 * 1024 });
  if (result.error || (expected !== null && result.status !== expected)) throw new Error(`${exe}: ${result.error?.message ?? result.stderr ?? result.status}`);
  return result;
}
function registered() {
  return JSON.parse(run(tools, ['installation-location']).stdout).location;
}
function ownedInstallation() { return registered()?.toLowerCase() === install.toLowerCase(); }
function setupArgs(target, log) {
  return ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', `/DIR=${target}`, '/TASKS=', `/LOG=${join(evidence, log)}`];
}
async function uninstall() {
  if (!ownedInstallation()) throw new Error('Refusing to uninstall a location not owned by this test.');
  run(join(install, 'unins000.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', `/LOG=${join(evidence, 'uninstall.log')}`]);
  for (let i = 0; i < 50 && registered() !== null; i++) await delay(200);
  assert.equal(registered(), null, 'Uninstall registration must be removed.');
}

assert.equal(registered(), null, 'An existing user installation must not be changed by artifact QA.');
assert.equal(existsSync(menu), false, 'The actual default Start-menu group must be absent; never overwrite user shortcuts.');
assert.equal(existsSync(evidence), false, 'Use a new evidence directory, not stale QA results.');
await mkdir(evidence, { recursive: true });await mkdir(qa, { recursive: true });
let passed = false;
try {
  const sums = await readFile(join(directory, 'SHA256SUMS.txt'), 'utf8');
  for (const file of [names.portable, names.installer]) {
    const path = join(directory, file), sha256 = await fileHash(path);
    assert.ok(sums.includes(`${sha256}  ${file}`), 'Artifact checksum mismatch.');
    artifacts.push({ name: file, sha256, bytes: (await stat(path)).size });
  }
  checks.push('final-artifact-sha256');
  run(tools, ['verify', join(directory, names.portable)]);
  run(tools, ['extract', join(directory, names.portable), join(qa, 'unpacked')]);
  await rename(join(qa, 'unpacked'), portable);
  run(tools, ['verify-dir', portable]);
  const manifest = JSON.parse(await readFile(join(portable, 'manifest.json'), 'utf8'));
  assert.equal(manifest.version, version);assert.equal(manifest.sourceDirty, false, 'Only clean final artifacts may pass release QA.');
  sourceCommit = manifest.sourceCommit;payloadFiles = manifest.files.length;
  const records = await inventory(portable);
  assert.equal(records.length, manifest.files.length);
  const localPaths = [root, process.env.USERPROFILE].filter(Boolean);
  const prefixes = [...new Set(localPaths.flatMap(value => [value, value.toLowerCase(), value.replaceAll('\\', '/')]))];
  const privateBytes = prefixes.flatMap(value => [Buffer.from(value, 'utf8'), Buffer.from(value, 'utf16le')]);
  for (const file of [...records.map(record => record.path), 'manifest.json']) {
    const bytes = await readFile(join(portable, file));
    assert.ok(privateBytes.every(prefix => !bytes.includes(prefix)), `Local source/profile path leaked in ${file}`);
  }
  checks.push('payload-allowlist-and-known-local-path-privacy-scan');
  checks.push('portable-manifest-clean-source-and-relocation');
  run(tools, ['smoke', portable, join(qa, 'portable-data'), join(evidence, 'portable-process-tree.json')]);
  checks.push('portable-native-react-private-browser-probe-and-owned-process-exit');
  const exeBefore = await fileHash(join(portable, 'DanmuCompanion.exe'));
  const rejected = run(join(directory, names.installer), setupArgs(portable, 'reject-portable.log'), null);
  assert.notEqual(rejected.status, 0);assert.equal(registered(), null);
  assert.equal(await fileHash(join(portable, 'DanmuCompanion.exe')), exeBefore);
  assert.equal(existsSync(join(portable, 'unins000.exe')), false);
  const foreign = join(qa, 'unrelated');await mkdir(foreign);
  await writeFile(join(foreign, 'sentinel.txt'), 'foreign files must survive');
  assert.notEqual(run(join(directory, names.installer), setupArgs(foreign, 'reject-unrelated.log'), null).status, 0);
  assert.equal(await readFile(join(foreign, 'sentinel.txt'), 'utf8'), 'foreign files must survive');
  assert.equal(registered(), null);checks.push('actual-silent-installer-rejects-portable-and-foreign-nonempty-directories');
  run(join(directory, names.installer), setupArgs(install, 'install.log'));
  assert.ok(ownedInstallation());run(tools, ['verify-installed', install]);
  run(tools, ['check-shortcut', link, join(install, 'DanmuCompanion.exe')]);
  checks.push('custom-chinese-space-installation-hashes-registration-and-direct-exe-shortcut');
  run(tools, ['smoke', install, join(qa, 'installed-data'), join(evidence, 'installed-process-tree.json')]);
  checks.push('installed-native-react-private-browser-probe-and-owned-process-exit');
  const data = join(qa, 'installed-data', 'release-qa', 'preferences.json');
  await writeFile(data, '{"isolatedPreservationSentinel":true}\n');
  const dataBefore = await fileHash(data);
  await writeFile(join(install, 'user-note.txt'), 'foreign addition after installation');
  const ownership = join(install, 'install-files.txt'), collision = join(install, 'THIRD-PARTY-NOTICES.md');
  const originalList = await readFile(ownership), originalFile = await readFile(collision);
  await writeFile(ownership, originalList.toString('utf8').split(/\r?\n/).filter(file => file !== 'THIRD-PARTY-NOTICES.md').join('\r\n'));
  await writeFile(collision, 'foreign sentinel at an incoming new-version filename');
  assert.notEqual(run(join(directory, names.installer), setupArgs(install, 'reject-upgrade-collision.log'), null).status, 0);
  assert.equal(await readFile(collision, 'utf8'), 'foreign sentinel at an incoming new-version filename');
  await writeFile(ownership, originalList);await writeFile(collision, originalFile);
  checks.push('upgrade-rejects-new-payload-file-colliding-with-non-owned-file');
  run(join(directory, names.installer), setupArgs(install, 'reinstall.log'));
  assert.ok(ownedInstallation());run(tools, ['verify-installed', install]);
  assert.equal(await fileHash(data), dataBefore);
  assert.equal(await readFile(join(install, 'user-note.txt'), 'utf8'), 'foreign addition after installation');
  checks.push('registered-in-place-reinstall-preserves-isolated-data-and-foreign-addition');
  await uninstall();
  assert.equal(existsSync(join(install, 'DanmuCompanion.exe')), false);
  assert.equal(existsSync(link), false);
  assert.equal(await fileHash(data), dataBefore);
  assert.equal(await readFile(join(install, 'user-note.txt'), 'utf8'), 'foreign addition after installation');
  checks.push('uninstall-removes-owned-files-shortcut-registration-and-preserves-data-and-foreign-addition');
  passed = true;
} finally {
  if (ownedInstallation()) await uninstall();
  await writeFile(join(evidence, 'result.json'), JSON.stringify({ passed, version, sourceCommit, payloadFiles, artifacts, directory, checks, isolatedQaRoot: qa, installationAbsent: registered() === null }, null, 2));
  if (passed) {
    const parent = await realpath(qaParent), target = await realpath(qa);
    assert.ok(target.toLowerCase().startsWith((parent + sep).toLowerCase()) && target.endsWith(`beta-${id}`));
    await rm(target, { recursive: true, force: true });
    if (existsSync(menu)) {
      assert.equal((await realpath(menu)).toLowerCase(), resolve(menu).toLowerCase());
      await rmdir(menu);
    }
  }
}
console.log(JSON.stringify({ passed, checks, evidence }, null, 2));
