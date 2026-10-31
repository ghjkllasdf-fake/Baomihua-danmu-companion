import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { cp, mkdir, writeFile, readFile, rm, readdir } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { randomUUID } from 'node:crypto';
import { inventory, fileHash } from './release-lib.mjs';

const root = resolve(import.meta.dirname, '..'), parent = join(root, 'artifacts', 'updater-sized-tests');
const fixture = join(parent, randomUUID());await mkdir(fixture, { recursive: true });
try {
  const input = join(root, '.deps/includeapi/payload');
  const receipt = JSON.parse(await readFile(join(root, '.deps/includeapi/payload-inventory.json'), 'utf8'));
  assert.ok(receipt.fileCount >= 4000);
  const application = join(fixture, 'app'), stage = join(fixture, 'stage');
  for (const [directory, version] of [[application, '0.1.1-beta.6'], [stage, '0.1.1-beta.7']]) {
    await cp(input, directory, { recursive: true });
    await writeFile(join(directory, 'DanmuCompanion.exe'), `Controlled sized-fixture main ${version}`);
    await writeFile(join(directory, 'manifest.json'), JSON.stringify({ schemaVersion: 1, version, variant: 'includeAPI', sourceCommit: 'a'.repeat(40), sourceDirty: false, files: await inventory(directory, receipt.files) }));
  }
  await writeFile(join(application, 'foreign.txt'), 'foreign sentinel');
  const output = join(fixture, 'measurement.json');
  const native = join(root, 'tests/NativeDistribution/bin/Release/net10.0-windows/win-x64/NativeDistribution.Tests.exe');
  const result = spawnSync(native, ['--update-sized-fixture', application, stage, output], { cwd: root, windowsHide: true, stdio: 'inherit', timeout: 180000 });
  if (result.error) throw result.error;
  assert.equal(result.status, 0, 'Actual sized fixture did not complete.');
  const measured = JSON.parse(await readFile(output));
  assert.equal(measured.fileCount, receipt.fileCount + 2);
  assert.equal(measured.backupCreated, false);
  assert.equal(measured.incrementalIdentityRecords, measured.fileCount);
  assert.equal(await readFile(join(application, 'foreign.txt'), 'utf8'), 'foreign sentinel');
  assert.equal((await readdir(measured.metadataScope)).includes('backup'), false);
  const evidence = join(parent, `verification-${randomUUID()}.json`);
  await writeFile(evidence, JSON.stringify({ ...measured, passed: true, inputInventorySha256: await fileHash(join(root, '.deps/includeapi/payload-inventory.json')), actualApiInputBytes: receipt.totalBytes,
    fixtureNote: 'Actual pinned includeAPI files and bytes; controlled main executable, not a product/runtime or real-player test.' }, null, 2));
  console.log(JSON.stringify({ ...measured, evidence }, null, 2));
} finally {
  assert.ok(resolve(fixture).startsWith(resolve(parent) + sep));await rm(fixture, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
}
