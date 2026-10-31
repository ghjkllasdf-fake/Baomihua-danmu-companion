import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

test('actual Inno snapshot captures only old-owned retired resources and preserves Unicode scope', async () => {
  const root = resolve(import.meta.dirname, '..'), fixture = await mkdtemp(join(tmpdir(), 'DanmuResourceInno-'));
  try {
    const application = join(fixture, '\u539f\u5b89\u88c5\u4f4d\u7f6e'), snapshot = join(fixture, 'snapshot'), report = join(fixture, 'report.txt');
    await mkdir(join(application, 'de'), { recursive: true }); await mkdir(join(application, 'zh-Hans'));
    await writeFile(join(application, 'de/System.Windows.Forms.resources.dll'), 'old owned resource');
    await writeFile(join(application, 'de/personal.txt'), 'foreign sentinel');
    await writeFile(join(application, 'zh-Hans/System.Windows.Forms.resources.dll'), 'retained Chinese');
    await writeFile(join(application, 'manifest.json'), '{}');
    const compiled = spawnSync(join(root, '.deps/inno/ISCC.exe'), ['--quiet', `/DReleaseDir=${fixture}`, 'tests/inno/ResourceRetirement.Tests.iss'], { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 30000 });
    assert.equal(compiled.status, 0, compiled.stdout + compiled.stderr);
    const ran = spawnSync(join(fixture, 'ResourceRetirement.Tests.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', `/ROOT=${application}`, `/SNAPSHOT=${snapshot}`, `/REPORT=${report}`], { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 30000 });
    assert.equal(ran.status, 1, ran.stdout + ran.stderr);
    assert.equal(await readFile(report, 'utf8'), 'pass');
    const lines = (await readFile(join(snapshot, 'scope.txt'), 'utf8')).trim().split(/\r?\n/);
    assert.equal(lines[0], `retired-resources-v1|${application}`);
    assert.equal(lines.length, 2);
    assert.match(lines[1], /^de\\System\.Windows\.Forms\.resources\.dll\|[0-9A-F]{48}$/);
    assert.equal(await readFile(join(application, 'de/personal.txt'), 'utf8'), 'foreign sentinel');
  } finally { await rm(fixture, { recursive: true, force: true }); }
});
