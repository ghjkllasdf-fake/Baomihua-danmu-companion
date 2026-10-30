import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';

const root = resolve(import.meta.dirname, '..');
const supplied = {
  app: { ico: 'fb7b6bbfdafc5d29afc085965e7bc82f6bf4e0fc78641b02bb49e7396cdafd9c',
    svg: 'bc6114c77cc5ab8632c2c46f8df2d4a1e1418a758fd5f7b1e7bc5c9266314eab',
    png: 'cb698e8f1e81065001c275f433b96463df26a795a8622132321161bea33e219f',
    sizes: [16, 20, 24, 32, 40, 48, 64, 96, 128, 256] },
  tray: { ico: '118664d68d5ead8bbc38583020923895e2173014aee0c9c8d221d2c26f2b9f62',
    svg: 'f41f91123fdec6c0e85898d78d5f70e62006b4ed3ed09d125597dcef15abd4d6',
    png: '8804085ecf1d136cb87df706d88d1a1c0244569d908767904d3befce73eb3dbe',
    sizes: [16, 20, 24, 32, 40, 48, 64, 256] },
};
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

test('all supplied application and tray artwork is preserved, including optimized small ICO frames', async () => {
  for (const [name, expected] of Object.entries(supplied)) {
    const bytes = await readFile(join(root, 'assets', `${name}.ico`));
    assert.equal(hash(bytes), expected.ico, `${name}.ico must match the supplied set exactly.`);
    assert.equal(bytes.readUInt16LE(0), 0);
    assert.equal(bytes.readUInt16LE(2), 1);
    assert.equal(bytes.readUInt16LE(4), expected.sizes.length);
    for (const [index, size] of expected.sizes.entries()) {
      const entry = 6 + 16 * index;
      assert.equal(bytes[entry] || 256, size);
      assert.equal(bytes[entry + 1] || 256, size);
      assert.equal(bytes.readUInt16LE(entry + 4), 1);
      assert.equal(bytes.readUInt16LE(entry + 6), 32);
      const length = bytes.readUInt32LE(entry + 8), offset = bytes.readUInt32LE(entry + 12);
      assert.ok(offset >= 6 + 16 * expected.sizes.length && length > 24 && offset + length <= bytes.length);
      const png = bytes.subarray(offset, offset + length);
      assert.equal(png.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
      assert.equal(png.readUInt32BE(16), size);
      assert.equal(png.readUInt32BE(20), size);
    }
    assert.equal(hash(await readFile(join(root, 'assets', `${name}.svg`))), expected.svg);
    assert.equal(hash(await readFile(join(root, 'docs/assets', `${name}-icon.png`))), expected.png);
  }
  assert.equal(hash(await readFile(join(root, 'ui/public/favicon.ico'))), supplied.app.ico);
  assert.match(await readFile(join(root, 'ui/index.html'), 'utf8'), /href="\/favicon\.ico"/);
});

test('refreshing icon previews keeps source ICO bytes unchanged and writes browser assets to an isolated output', async () => {
  const script = join(root, 'scripts/Build-Icons.mjs');
  assert.ok((await readFile(script, 'utf8')).includes('--out'), 'The preview generator needs an isolated output option.');
  const output = await mkdtemp(join(tmpdir(), 'DanmuIconPreview-'));
  try {
    const result = spawnSync(process.execPath, [script, '--out', output], {
      cwd: root, windowsHide: true, encoding: 'utf8', timeout: 60000,
    });
    assert.equal(result.status, 0, result.stderr || result.error?.message || result.stdout);
    for (const [name, expected] of Object.entries(supplied)) {
      assert.equal(hash(await readFile(join(root, 'assets', `${name}.ico`))), expected.ico);
      assert.equal(hash(await readFile(join(output, 'docs/assets', `${name}-icon.png`))), expected.png);
    }
    assert.equal(hash(await readFile(join(output, 'ui/public/favicon.ico'))), supplied.app.ico);
    for (const name of ['icon-preview.png', 'icon-size-review.png']) {
      const png = await readFile(join(output, 'docs/assets', name));
      assert.equal(png.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
      assert.ok(png.readUInt32BE(16) >= 500 && png.readUInt32BE(20) >= 300);
    }
  } finally { await rm(output, { recursive: true, force: true }); }
});
