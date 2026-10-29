import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { inventoryPayload } from '../prepare-lib.mjs';

test('inventory lists exact bytes and rejects shell scripts', async t => {
  const root = await mkdtemp(join(tmpdir(), 'includeapi-inventory-'));
  t.after(() => rm(root, { force: true, recursive: true }));
  await mkdir(join(root, 'api'));
  await writeFile(join(root, 'api', 'entry.mjs'), 'hello');
  const result = await inventoryPayload(root);
  assert.deepEqual(result, { schemaVersion: 1, totalBytes: 5, fileCount: 1, files: [{ path: 'api/entry.mjs', length: 5, sha256: '2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824' }] });
  await writeFile(join(root, 'api', 'bad.PS1'), 'bad');
  await assert.rejects(inventoryPayload(root), /forbidden/i);
});
