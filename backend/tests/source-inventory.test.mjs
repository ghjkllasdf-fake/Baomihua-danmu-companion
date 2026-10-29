import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
const root = new URL('../', import.meta.url);

test('every production package has pinned actual source and npm archive metadata', async () => {
  const lock=JSON.parse(await readFile(new URL('package-lock.json',root),'utf8'));
  const source=JSON.parse(await readFile(new URL('dependency-sources.json',root),'utf8'));
  assert.equal(source.schemaVersion,1);
  const packages=Object.entries(lock.packages).filter(([path])=>path.startsWith('node_modules/'));
  assert.equal(source.packages.length,packages.length);
  const names=new Set();
  for(const item of source.packages){
    const path=`node_modules/${item.name}`;
    assert.ok(!names.has(item.name));names.add(item.name);
    assert.equal(item.version,lock.packages[path]?.version);assert.equal(item.integrity,lock.packages[path]?.integrity);
    assert.match(item.sourceCommit,/^[a-f0-9]{40}$/);assert.match(item.sourceSha256,/^[a-f0-9]{64}$/);
    assert.match(item.tarballSha256,/^[a-f0-9]{64}$/);
    assert.ok(item.sourceFile.endsWith('.zip'));assert.ok(item.tarballFile.endsWith('.tgz'));
  }
});
