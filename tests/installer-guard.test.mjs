import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, writeFile, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';

test('actual Inno destination guard protects foreign/portable files and handles local paths', async () => {
  const root = resolve(import.meta.dirname, '..');
  const folder = await mkdtemp(join(tmpdir(), 'DanmuInstallerGuard-'));
  const report = join(folder, 'result.txt');
  const evidence = join(root, 'artifacts', 'native-beta');
  try {
    await mkdir(evidence, { recursive: true });
    await mkdir(join(folder, 'outside'));
    await writeFile(join(folder, 'outside', 'sentinel.txt'), 'outside junction sentinel');
    await mkdir(join(folder, 'registered'));
    await symlink(join(folder, 'outside'), join(folder, 'registered', 'linked-assets'), 'junction');
    await symlink(join(folder, 'outside'), join(folder, 'alias'), 'junction');
    const compile = spawnSync(join(root, '.deps', 'inno', 'ISCC.exe'), ['--quiet', `/DReleaseDir=${folder}`, 'tests/inno/DestinationGuard.Tests.iss'],
      { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 30000 });
    assert.equal(compile.status, 0, compile.stdout + compile.stderr);
    const run = spawnSync(join(folder, 'DestinationGuard.Tests.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', `/ROOT=${folder}`, `/REPORT=${report}`],
      { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 30000 });
    assert.equal(run.status, 1, 'InitializeSetup exits before installation; its report is the test result.');
    const result = await readFile(report, 'utf8');
    await writeFile(join(evidence, 'installer-guards.log'), result);
    const cases = result.trim().split(/\r?\n/);
    assert.equal(cases.length, 21);
    assert.ok(cases.every(line => line.endsWith('=pass')), result);
    assert.equal(await readFile(join(folder, 'occupied', 'sentinel.txt'), 'utf8'), 'unrelated sentinel');
    assert.equal(await readFile(join(folder, 'portable', 'DanmuCompanion.exe'), 'utf8'), 'portable sentinel');
    assert.equal(await readFile(join(folder, 'registered', 'user-note.txt'), 'utf8'), 'must survive');
    assert.equal(await readFile(join(folder, 'outside', 'sentinel.txt'), 'utf8'), 'outside junction sentinel');
  } finally { await rm(folder, { recursive: true, force: true }); }
});
