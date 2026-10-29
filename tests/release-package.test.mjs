import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { allowedEntry, inventory, releaseNames } from '../scripts/release-lib.mjs';

test('beta filenames are fixed and reject traversal/shell fragments', () => {
  assert.equal(releaseNames('0.1.0-beta.1').installer, 'DanmuCompanion-0.1.0-beta.1-win-x64-setup.exe');
  assert.equal(releaseNames('0.1.0-beta.1').portable, 'DanmuCompanion-0.1.0-beta.1-win-x64-portable.zip');
  for (const version of ['../test', '0.1.0-beta.0', '0.1.0;whoami', 'v0.1.0-beta.1', '0.1.0-preview.15']) {
    assert.throws(() => releaseNames(version));
  }
});

test('payload admits compiled dependencies and forbids app scripts/private files', () => {
  for (const path of ['DanmuCompanion.exe', 'DanmuCompanion.Probe.exe', 'coreclr.dll', 'zh-Hans/System.Windows.Forms.resources.dll',
    'ui/dist/assets/main-123.js', 'WebView2Runtime/msedgewebview2.exe', 'licenses/React/LICENSE.txt', 'manifest.json']) {
    assert.equal(allowedEntry(path), true, path);
  }
  for (const path of ['desktop/Host.ps1', 'pwsh.exe', 'powershell.exe', 'System.Management.Automation.dll', '.deps/pwsh/pwsh.dll',
    'start.bat', 'api.dat', 'data/preferences.json', 'obj/cache.json', 'a/../coreclr.dll', 'C:/private.dll', '/root.dll',
    'ui/dist/assets/../../escape.js', 'assets/CON.txt', 'WebView2Runtime/evil.ps1', 'WebView2Runtime/evil.bat']) {
    assert.equal(allowedEntry(path), false, path);
  }
  assert.equal(allowedEntry('WebView2Runtime/show_third_party_software_licenses.bat'), true,
    'Unmodified upstream credits helper is not the application launcher and does not use PowerShell.');
});

test('manifest inventory hashes actual bytes and rejects private additions', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuReleaseTest-'));
  try {
    await mkdir(join(root, 'assets'));
    await writeFile(join(root, 'assets', 'app.ico'), 'controlled icon');
    const records = await inventory(root);
    assert.equal(records.length, 1);
    assert.equal(records[0].path, 'assets/app.ico');
    assert.equal(records[0].length, 15);
    assert.match(records[0].sha256, /^[0-9a-f]{64}$/);
    await writeFile(join(root, 'api.dat'), 'private sentinel');
    await assert.rejects(() => inventory(root), /Forbidden payload/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
