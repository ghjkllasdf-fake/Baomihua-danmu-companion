import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, writeFile, readFile, rm, symlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { fileHash, inventory } from '../scripts/release-lib.mjs';

const tools = resolve('scripts/NativeReleaseTools/bin/Release/net10.0/win-x64/NativeReleaseTools.exe');
function run(...args) {
  return spawnSync(tools, args, { windowsHide: true, encoding: 'utf8', timeout: 15000 });
}
function pass(result) { assert.equal(result.status, 0, result.stderr ?? result.error?.message); }
function fail(result) { assert.equal(result.status, 1, 'Expected explicit verifier failure, not success or a crash.'); }

test('native ZIP tool verifies final bytes, rejects mutations and never overwrites', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuNativeArchive-'));
  const payload = join(root, 'payload');
  await mkdir(payload);
  try {
    await writeFile(join(payload, 'DanmuCompanion.exe'), 'controlled placeholder; never executed');
    const manifest = { schemaVersion: 1, version: '0.1.0-beta.1', sourceCommit: 'a'.repeat(40), files: await inventory(payload) };
    await writeFile(join(payload, 'manifest.json'), JSON.stringify(manifest));
    const first = join(root, 'first.zip'), second = join(root, 'second.zip');
    pass(run('pack', payload, first)); pass(run('verify', first)); pass(run('pack', payload, second));
    const original = await fileHash(first);
    assert.equal(await fileHash(second), original, 'Equivalent input must produce byte-identical ZIPs.');
    fail(run('pack', payload, first));
    assert.equal(await fileHash(first), original, 'Rejected overwrite must preserve the existing artifact.');
    const extracted = join(root, 'extracted');
    pass(run('extract', first, extracted)); pass(run('verify-dir', extracted));
    fail(run('extract', first, extracted));
    await writeFile(join(extracted, 'DanmuCompanion.exe'), 'modified after extraction');
    fail(run('verify-dir', extracted));
    const bytes = await readFile(first);
    assert.equal(bytes.readUInt32LE(0), 0x04034b50);
    const content = 30 + bytes.readUInt16LE(26) + bytes.readUInt16LE(28);
    const corrupted = Buffer.from(bytes); corrupted[content + 1] ^= 0x40;
    const corruptZip = join(root, 'corrupt.zip'); await writeFile(corruptZip, corrupted);
    fail(run('verify', corruptZip));
    const unsafe = Buffer.from(bytes), name = Buffer.from('DanmuCompanion.exe');
    const replacement = Buffer.from('../' + 'x'.repeat(name.length - 3));
    for (let at = unsafe.indexOf(name); at !== -1; at = unsafe.indexOf(name, at + name.length)) replacement.copy(unsafe, at);
    const unsafeZip = join(root, 'traversal.zip'); await writeFile(unsafeZip, unsafe);
    fail(run('verify', unsafeZip)); fail(run('extract', unsafeZip, join(root, 'must-not-extract')));
    await writeFile(join(payload, 'api.dat'), 'private sentinel');
    fail(run('pack', payload, join(root, 'unmanifested.zip')));
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('reparse input is rejected without following it', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuNativeReparse-'));
  const payload = join(root, 'payload'), outside = join(root, 'outside');
  await mkdir(payload); await mkdir(outside);
  try {
    await writeFile(join(payload, 'DanmuCompanion.exe'), 'controlled');
    await writeFile(join(payload, 'manifest.json'), JSON.stringify({ schemaVersion: 1, version: '0.1.0-beta.1',
      sourceCommit: 'a'.repeat(40), files: await inventory(payload) }));
    await writeFile(join(outside, 'sentinel.txt'), 'do not follow');
    await symlink(outside, join(payload, 'junction'), 'junction');
    await assert.rejects(() => inventory(payload), /Reparse/);
    fail(run('pack', payload, join(root, 'reparse.zip')));
    assert.equal(await readFile(join(outside, 'sentinel.txt'), 'utf8'), 'do not follow');
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('only explicit includeAPI manifests receive the bounded large file inventory allowance', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuNativeVariantCount-'));
  try {
    const manifest = { schemaVersion: 1, version: '0.1.0-beta.3', sourceCommit: 'a'.repeat(40),
      files: Array.from({ length: 4097 }, (_, i) => ({ path: `api/file-${i}.js`, length: 0, sha256: 'a'.repeat(64) })) };
    await writeFile(join(root, 'manifest.json'), JSON.stringify(manifest));
    const ordinary = run('verify-dir', root); fail(ordinary);
    assert.match(ordinary.stderr, /Invalid payload count/);
    manifest.variant = 'includeAPI';
    await writeFile(join(root, 'manifest.json'), JSON.stringify(manifest));
    const variant = run('verify-dir', root); fail(variant);
    assert.match(variant.stderr, /Unexpected directory payload files/);
    manifest.files = Array.from({ length: 16385 }, (_, i) => ({ path: `api/file-${i}.js`, length: 0, sha256: 'a'.repeat(64) }));
    await writeFile(join(root, 'manifest.json'), JSON.stringify(manifest));
    const tooMany = run('verify-dir', root); fail(tooMany);
    assert.match(tooMany.stderr, /Invalid payload count/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
