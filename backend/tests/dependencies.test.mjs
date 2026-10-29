import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const backend = new URL('../', import.meta.url);
test('modified runtime profile is identified as AGPL while original upstream metadata stays separate', async () => {
  const manifest = JSON.parse(await readFile(new URL('package.json', backend), 'utf8'));
  const lock = JSON.parse(await readFile(new URL('package-lock.json', backend), 'utf8'));
  assert.equal(manifest.license, 'AGPL-3.0-only');
  assert.equal(lock.packages[''].license, 'AGPL-3.0-only');
});

test('locked production graph does not distribute brotli or depend on it', async () => {
  const manifest = JSON.parse(await readFile(new URL('package.json', backend), 'utf8'));
  const lock = JSON.parse(await readFile(new URL('package-lock.json', backend), 'utf8'));
  assert.equal(manifest.dependencies.brotli, undefined);
  for (const [path, entry] of Object.entries(lock.packages)) {
    assert.equal(/(?:^|\/)node_modules\/brotli$/.test(path), false, path);
    for (const type of ['dependencies', 'optionalDependencies']) assert.equal(entry[type]?.brotli, undefined, `${path} ${type}`);
  }
});

test('pure worker profile excludes build tools and unused database backends', async () => {
  const manifest = JSON.parse(await readFile(new URL('package.json', backend), 'utf8'));
  const lock = JSON.parse(await readFile(new URL('package-lock.json', backend), 'utf8'));
  assert.equal(manifest.dependencies.esbuild, undefined);
  const excluded = ['esbuild', '@electric-sql/pglite', '@electric-sql/pglite-tools', 'drizzle-orm'];
  for (const [path, entry] of Object.entries(lock.packages)) {
    assert.ok(!path.startsWith('node_modules/@esbuild/'), path);
    for (const name of excluded) {
      assert.notEqual(path, `node_modules/${name}`);
      for (const type of ['dependencies', 'optionalDependencies']) assert.equal(entry[type]?.[name], undefined, `${path} ${name}`);
    }
  }
});
