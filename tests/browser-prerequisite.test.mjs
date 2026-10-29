import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';

test('actual Inno browser prerequisite handles absent/old runtime, bootstrap failures and legacy layouts without installing anything', async () => {
  const root = resolve(import.meta.dirname, '..'), folder = await mkdtemp(join(tmpdir(), 'DanmuBrowserPrereq-'));
  const report = join(folder, 'result.txt');
  try {
    const compile = spawnSync(join(root, '.deps', 'inno', 'ISCC.exe'), ['--quiet', `/DReleaseDir=${folder}`, 'tests/inno/WebView2Prerequisite.Tests.iss'],
      { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 30000 });
    assert.equal(compile.status, 0, compile.stdout + compile.stderr);
    const run = spawnSync(join(folder, 'WebView2Prerequisite.Tests.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', `/ROOT=${folder}`, `/REPORT=${report}`],
      { windowsHide: true, timeout: 30000 });
    assert.equal(run.status, 1);
    const result = await readFile(report, 'utf8');
    await mkdir(join(root, 'artifacts', 'shared-beta'), { recursive: true });
    await writeFile(join(root, 'artifacts', 'shared-beta', 'browser-prerequisite-tests.log'), result);
    assert.equal(result.trim().split(/\r?\n/).length, 17);
    assert.ok(result.trim().split(/\r?\n/).every(line => line.endsWith('=pass')), result);
  } finally { await rm(folder, { recursive: true, force: true }); }
});
