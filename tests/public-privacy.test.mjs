import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, mkdtemp, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const scanner = resolve(import.meta.dirname, '../scripts/Test-PublicPrivacy.mjs');
async function scan(name, value) {
  const root = await mkdtemp(join(tmpdir(), 'danmu-privacy-'));
  const git = args => {
    const result = spawnSync('git', args, { cwd: root, windowsHide: true });
    assert.equal(result.status, 0, 'Fixture Git command must succeed');
  };
  git(['init', '--quiet']);
  await mkdir(dirname(join(root, name)), { recursive: true });
  await writeFile(join(root, name), value);
  git(['add', '--', name]);
  const result = spawnSync(process.execPath, [scanner, root], { encoding: 'utf8', windowsHide: true });
  assert.ok(result.status === 0 || result.status === 1, 'Scanner must finish with a structured report');
  return { code: result.status, report: JSON.parse(result.stdout) };
}
test('public privacy scan accepts source and reserved placeholder URLs', async () => {
  const result = await scan('README.md', 'API: https://danmu.example.com/YOUR_TOKEN\n');
  assert.equal(result.code, 0);
  assert.equal(result.report.scannedFiles, 1);
});
test('only the exact virtual-origin rejection fixture permits synthetic userinfo', async () => {
  const value = 'https://' + 'user:password@' + 'danmu.local/';
  assert.equal((await scan('tests/Test-BridgeBoundary.ps1', value)).code, 0);
  assert.equal((await scan('source.txt', value)).code, 1);
});
for (const [name, value, rule] of [
  ['source.txt', 'ghp_' + 'a'.repeat(36), 'github-token'],
  ['source.txt', '-'.repeat(5) + 'BEGIN PRIVATE KEY' + '-'.repeat(5), 'private-key'],
  ['source.txt', 'C:' + '\\Users\\' + 'ExamplePerson\\Downloads', 'personal-home-path'],
  ['.env', 'PRIVATE_CONFIG=true', 'private-or-generated-file'],
  ['api.dat', 'ENCRYPTED_EXAMPLE_ONLY', 'private-or-generated-file'],
  ['source.txt', 'https://' + '192.168.10.20/private', 'private-network-url'],
]) {
  test(`public privacy scan rejects ${rule} without echoing the matched value`, async () => {
    const result = await scan(name, value);
    assert.equal(result.code, 1);
    assert.ok(result.report.findings.some(f => f.rule === rule));
    assert.ok(!JSON.stringify(result.report).includes(value));
  });
}
