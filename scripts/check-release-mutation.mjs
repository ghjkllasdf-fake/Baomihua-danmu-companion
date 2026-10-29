import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const root = resolve(import.meta.dirname, '..');
const original = await readFile(join(root, 'scripts', 'release-lib.mjs'), 'utf8');
const source = original.split('\n').filter(line => !line.includes('parts.some(part => /^(?:pwsh|powershell|System')).join('\n');
assert.notEqual(source, original, 'The intended dependency rejection guard must actually be mutated.');
const folder = await mkdtemp(join(tmpdir(), 'DanmuReleaseMutation-'));
try {
  const module = join(folder, 'mutated-lib.mjs'), tests = join(folder, 'mutation.test.mjs');
  await writeFile(module, source);
  const testSource = await readFile(join(root, 'tests', 'release-package.test.mjs'), 'utf8');
  await writeFile(tests, testSource.replace('../scripts/release-lib.mjs', pathToFileURL(module).href));
  const result = spawnSync(process.execPath, ['--test', tests], { windowsHide: true, encoding: 'utf8', timeout: 15000 });
  await mkdir(join(root, 'artifacts', 'native-beta'), { recursive: true });
  await writeFile(join(root, 'artifacts', 'native-beta', 'release-mutation.log'), (result.stdout ?? '') + (result.stderr ?? ''));
  assert.equal(result.status, 1, 'Tests must reject removal of the PowerShell dependency guard, without a crash or timeout.');
  assert.match(result.stdout + result.stderr, /System\.Management\.Automation\.dll/);
  assert.equal(await readFile(join(root, 'scripts', 'release-lib.mjs'), 'utf8'), original, 'Production source must remain unchanged.');
  console.log(JSON.stringify({ passed: true, mutation: 'remove PowerShell assembly rejection', killed: true, productionSourceUnchanged: true }));
} finally { await rm(folder, { recursive: true, force: true }); }
