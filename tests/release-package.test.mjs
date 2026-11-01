import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm, copyFile, stat, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve, win32 } from 'node:path';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { allowedEntry, inventory, releaseNames, assertPayloadBudget, validateApiProvenance, releaseVersions, assertReleaseGuide, releaseSourceStatus, candidateApiDirectory, nativePublishEntry, retainedResourceCultures } from '../scripts/release-lib.mjs';

test('release verification normalizes the trailing separator returned by Inno registration', async () => {
  const source = await readFile(new URL('../scripts/test-release.mjs', import.meta.url), 'utf8');
  const implementation = source.match(/function registered\(\) \{[\s\S]*?\n\}/)?.[0];
  assert.ok(implementation);
  const actual = new Function('run', 'tools', 'includeApi', 'resolve', implementation + '\nreturn registered();');
  for (const input of [null, 'C:\\Program Files\\Danmu', 'C:\\Program Files\\Danmu\\']) {
    const result = actual(() => ({ stdout: JSON.stringify({ location: input }) }), 'controlled tool', false, win32.resolve);
    assert.equal(result, input === null ? null : 'C:\\Program Files\\Danmu');
  }
});

test('runtime API notices are rendered from current source for the exact release, not copied from an old prepared payload', async () => {
  const { renderApiNotice } = await import('../scripts/release-lib.mjs');
  assert.equal(typeof renderApiNotice, 'function');
  const template = await readFile(new URL('../backend/NOTICES.md', import.meta.url), 'utf8');
  const stale = Buffer.from('DanmuCompanion-0.1.1-beta.3-corresponding-source.zip');
  const input = [{ path: 'api/THIRD-PARTY-NOTICES.md', length: stale.length, sha256: createHash('sha256').update(stale).digest('hex') }];
  const artifact = renderApiNotice(input, template, '0.1.1-beta.7');
  const payload = await mkdtemp(join(tmpdir(), 'DanmuApiNotice-'));
  try {
    await mkdir(join(payload, 'api'));
    await writeFile(join(payload, input[0].path), artifact.bytes);
    const actual = await readFile(join(payload, input[0].path), 'utf8');
    assert.match(actual, /DanmuCompanion-0\.1\.1-beta\.7-corresponding-source\.zip/);
    assert.match(actual, /\/releases\/tag\/v0\.1\.1-beta\.7/);
    assert.doesNotMatch(actual, /beta\.3|DanmuCompanion-VERSION/);
    assert.deepEqual(await inventory(payload, artifact.files), artifact.files);
    assert.equal(input[0].sha256, createHash('sha256').update(stale).digest('hex'), 'Prepared input provenance must remain unchanged.');
  } finally { await rm(payload, { recursive: true, force: true }); }
});

test('retired updater executable, assembly and configuration files are excluded from the payload', () => {
  for (const path of ['DanmuCompanion.Updater.exe', 'DanmuCompanion.Updater.dll', 'DanmuCompanion.Updater.deps.json', 'DanmuCompanion.Updater.runtimeconfig.json']) assert.equal(allowedEntry(path), false);
  for (const path of ['Updater.exe', 'DanmuCompanion.Updater.cmd', 'update.ps1', 'DanmuCompanion.Updater.exe:stream']) assert.equal(allowedEntry(path), false);
});

test('runtime payload keeps the actual ICO entries and excludes source artwork and the retired SVG favicon', () => {
  assert.equal(allowedEntry('ui/dist/favicon.ico'), true);
  assert.equal(allowedEntry('assets/app.ico'), true);
  assert.equal(allowedEntry('assets/tray.ico'), true);
  for (const path of ['assets/app.svg', 'assets/tray.svg', 'assets/other.ico', 'ui/dist/favicon.svg',
    'ui/dist/other.ico', 'ui/dist/favicon.exe', 'ui/dist/favicon.ico:stream', 'ui/dist/../favicon.ico']) {
    assert.equal(allowedEntry(path), false);
  }
});

test('release payload retains Chinese/English resource satellites and rejects all other language directories', () => {
  for (const culture of ['en', 'zh-Hans', 'zh-Hant']) {
    assert.equal(allowedEntry(`${culture}/System.Windows.Forms.resources.dll`), true);
    assert.equal(allowedEntry(`${culture}/PresentationFramework.resources.dll`), true);
  }
  for (const culture of ['cs', 'de', 'es', 'fr', 'it', 'ja', 'ko', 'pl', 'pt-BR', 'ru', 'tr']) {
    assert.equal(allowedEntry(`${culture}/System.Windows.Forms.resources.dll`), false, culture);
  }
  assert.equal(allowedEntry('System.Windows.Forms.dll'), true, 'Neutral English runtime resources must stay in the root assembly.');
  assert.equal(allowedEntry('zh-Hans/plugin.dll'), false, 'Culture folders must not become an arbitrary DLL allowance.');
});

test('all native projects use the same language policy and the release merge enforces it independently', async () => {
  const root = resolve(import.meta.dirname, '..');
  const props = await readFile(join(root, 'desktop/NativeCommon.props'), 'utf8');
  assert.deepEqual(props.match(/<SatelliteResourceLanguages>([^<]+)<\/SatelliteResourceLanguages>/)?.[1].split(';'), retainedResourceCultures);
  for (const project of ['NativeHost', 'NativeProbe']) {
    assert.match(await readFile(join(root, `desktop/${project}/${project}.csproj`), 'utf8'), /NativeCommon.props/);
  }
  assert.deepEqual(['coreclr.dll', 'System.Windows.Forms.dll', 'en/System.Windows.Forms.resources.dll', 'zh-Hans/System.Windows.Forms.resources.dll',
    'zh-Hant/System.Windows.Forms.resources.dll', 'de/System.Windows.Forms.resources.dll', 'pt-BR/System.Windows.Forms.resources.dll'].filter(nativePublishEntry),
    ['coreclr.dll', 'System.Windows.Forms.dll', 'en/System.Windows.Forms.resources.dll', 'zh-Hans/System.Windows.Forms.resources.dll', 'zh-Hant/System.Windows.Forms.resources.dll']);
  assert.throws(() => nativePublishEntry('zh-Hans/plugin.dll'), /Unexpected/);
  assert.throws(() => nativePublishEntry('../coreclr.dll'), /Unexpected/);
});

test('installer file version comes from the same native properties as the artifact version', () => {
  const props = '<Project><Version>0.1.1-beta.3</Version><FileVersion>0.1.1.3</FileVersion></Project>';
  assert.deepEqual(releaseVersions(props, '0.1.1-beta.3'), { fileVersion: '0.1.1.3' });
  assert.throws(() => releaseVersions(props, '0.1.0-beta.3'));
  assert.throws(() => releaseVersions(props.replace('0.1.1.3', '0.1.0.3'), '0.1.1-beta.3'));
  assert.throws(() => releaseVersions(props.replace('<FileVersion>0.1.1.3</FileVersion>', ''), '0.1.1-beta.3'));
});

test('release guide identifies the same version in its title', () => {
  assert.doesNotThrow(() => assertReleaseGuide('# App 0.1.1-beta.3 (portable)\n\nBody', '0.1.1-beta.3'));
  assert.throws(() => assertReleaseGuide('# App 0.1.0-beta.2\n\nBody mentions 0.1.1-beta.3', '0.1.1-beta.3'));
});

test('custom library packages are explicit dirty candidates, never final artifacts', () => {
  assert.throws(() => releaseSourceStatus(false, true, false), /custom|dirty/i);
  assert.deepEqual(releaseSourceStatus(false, true, true), { sourceDirty: true, customDanAny: true });
  assert.deepEqual(releaseSourceStatus(false, false, false), { sourceDirty: false, customDanAny: false });
});

test('isolated custom API input requires the explicit candidate flags', () => {
  assert.throws(() => candidateApiDirectory('custom', false, true, true), /candidate/i);
  assert.throws(() => candidateApiDirectory('custom', true, false, true), /candidate/i);
  assert.throws(() => candidateApiDirectory('custom', true, true, false), /candidate/i);
  assert.equal(candidateApiDirectory('custom', true, true, true), resolve('custom'));
  assert.equal(candidateApiDirectory(undefined, false, false), null);
});

test('beta filenames are fixed and reject traversal/shell fragments', () => {
  assert.equal(releaseNames('0.1.0-beta.1').installer, 'DanmuCompanion-0.1.0-beta.1-win-x64-setup.exe');
  assert.equal(releaseNames('0.1.0-beta.1').portable, 'DanmuCompanion-0.1.0-beta.1-win-x64-portable.zip');
  for (const version of ['../test', '0.1.0-beta.0', '0.1.0;whoami', 'v0.1.0-beta.1', '0.1.0-preview.15']) {
    assert.throws(() => releaseNames(version));
  }
});

test('includeAPI names and size budget are explicit and leave ordinary defaults unchanged', () => {
  assert.equal(releaseNames('0.1.0-beta.3', true).installer, 'DanmuCompanion-0.1.0-beta.3-win-x64-includeAPI-setup.exe');
  assert.equal(releaseNames('0.1.0-beta.3', true).portable, 'DanmuCompanion-0.1.0-beta.3-win-x64-includeAPI-portable.zip');
  assert.equal(assertPayloadBudget([{ length: 300 * 1024 * 1024 }], 0, true), 300 * 1024 * 1024);
  assert.throws(() => assertPayloadBudget([{ length: 300 * 1024 * 1024 }]));
  assert.equal(assertPayloadBudget([{ length: 390 * 1024 * 1024 }], 0, true), 390 * 1024 * 1024);
  assert.throws(() => assertPayloadBudget([{ length: 400 * 1024 * 1024 }], 1, true));
});

test('includeAPI admits only exact inventoried bytes and never permits PowerShell or launch scripts', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuApiReleaseTest-'));
  try {
    await mkdir(join(root, 'api'));
    const data = join(root, 'api', 'entry.mjs');
    await writeFile(data, 'controlled');
    const record = { path: 'api/entry.mjs', length: 10, sha256: createHash('sha256').update('controlled').digest('hex') };
    await assert.rejects(() => inventory(root), /Forbidden/);
    assert.equal((await inventory(root, [record])).length, 1);
    await writeFile(data, 'tampered!!');
    await assert.rejects(() => inventory(root, [record]), /inventory|hash/i);
    await writeFile(data, 'controlled');
    await writeFile(join(root, 'api', 'extra.mjs'), 'unlisted');
    await assert.rejects(() => inventory(root, [record]), /Forbidden|inventory/i);
    await rm(join(root, 'api', 'extra.mjs'));
    await writeFile(join(root, 'api', 'bad.ps1'), 'bad');
    const bad = { path: 'api/bad.ps1', length: 3, sha256: createHash('sha256').update('bad').digest('hex') };
    await assert.rejects(() => inventory(root, [record, bad]), /Forbidden/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('includeAPI refuses a stale prepared adapter or mismatched runtime provenance', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuApiSourceTest-'));
  try {
    const backend = join(root, 'backend'), api = join(root, 'api');
    await mkdir(backend); await mkdir(api);
    for (const name of ['entry.mjs', 'package.json', 'package-lock.json']) {
      await writeFile(join(backend, name), name); await writeFile(join(api, name), name);
    }
    const sha = value => createHash('sha256').update(value).digest('hex');
    const toolchain = { schemaVersion: 1, upstream: { commit: 'a'.repeat(40) }, node: { version: '22.23.4', sha256: sha('node') },
      productionLock: { sha256: sha('package-lock.json') }, packageManifestSha256: sha('package.json') };
    await writeFile(join(backend, 'toolchain.json'), JSON.stringify(toolchain));
    await writeFile(join(api, 'toolchain.json'), JSON.stringify(toolchain));
    await writeFile(join(api, 'node.exe'), 'node');
    await writeFile(join(api, 'runtime.json'), JSON.stringify({ schemaVersion: 1, upstreamCommit: 'a'.repeat(40), nodeVersion: '22.23.4' }));
    await validateApiProvenance(api, backend);
    await writeFile(join(backend, 'entry.mjs'), 'updated adapter');
    await assert.rejects(() => validateApiProvenance(api, backend), /stale|mismatch/i);
    await writeFile(join(backend, 'entry.mjs'), 'entry.mjs');
    await writeFile(join(api, 'runtime.json'), JSON.stringify({ schemaVersion: 1, upstreamCommit: 'b'.repeat(40), nodeVersion: '22.23.4' }));
    await assert.rejects(() => validateApiProvenance(api, backend), /runtime|provenance/i);
    await writeFile(join(api, 'runtime.json'), JSON.stringify({ schemaVersion: 1, upstreamCommit: 'a'.repeat(40), nodeVersion: '22.0.0' }));
    await assert.rejects(() => validateApiProvenance(api, backend), /runtime|provenance/i);
    await writeFile(join(api, 'runtime.json'), JSON.stringify({ schemaVersion: 1, upstreamCommit: 'a'.repeat(40), nodeVersion: '22.23.4', customDanAny: true }));
    await assert.rejects(() => validateApiProvenance(api, backend), /custom|provenance/i);
    assert.deepEqual(await validateApiProvenance(api, backend, { allowCustom: true }), { customDanAny: true });
    await writeFile(join(api, 'runtime.json'), JSON.stringify({ schemaVersion: 1, upstreamCommit: 'a'.repeat(40), nodeVersion: '22.23.4' }));
    await writeFile(join(api, 'node.exe'), 'wrong-node');
    await assert.rejects(() => validateApiProvenance(api, backend), /node|provenance/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('Inno compiles standard and includeAPI to distinct installer names', async () => {
  const root = resolve(import.meta.dirname, '..'), tmp = await mkdtemp(join(tmpdir(), 'DanmuInnoVariant-'));
  try {
    const payload = join(tmp, 'payload'); await mkdir(join(payload, 'assets'), { recursive: true });
    await copyFile(join(root, 'assets', 'app.ico'), join(payload, 'assets', 'app.ico'));
    await writeFile(join(payload, 'DISTRIBUTION-LICENSE.txt'), 'test license');
    await writeFile(join(payload, 'MicrosoftEdgeWebview2Setup.exe'), 'test-only placeholder');
    const list = join(tmp, 'incoming.iss');
    for (const includeApi of [false, true]) {
      // Exercise the real includeAPI scale rather than just compiling an empty list.
      const incoming = includeApi ? Array.from({ length: 11000 }, (_, i) => `  Files.Add('api\\node_modules\\entry-${i}.js');`).join('\n') : '';
      await writeFile(list, `procedure AddIncomingPayloadFiles(Files: TStringList);\nbegin\n${incoming}\nend;\n`);
      const args = ['--quiet', '/DAppVersion=0.1.0-beta.3', '/DAppFileVersion=0.1.0.3', `/DPayloadDir=${payload}`, `/DPayloadFileList=${list}`,
        `/DReleaseDir=${tmp}`, ...(includeApi ? ['/DIncludeApi=1'] : []), 'installer/DanmuCompanion.iss'];
      const result = spawnSync(join(root, '.deps', 'inno', 'ISCC.exe'), args, { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 60000 });
      assert.equal(result.status, 0, result.stdout + result.stderr);
      const installer = join(tmp, releaseNames('0.1.0-beta.3', includeApi).installer);
      assert.ok((await stat(installer)).size > 0);
      const metadata = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        '[System.Diagnostics.FileVersionInfo]::GetVersionInfo($env:DANMU_TEST_INSTALLER).FileVersion'],
        { encoding: 'utf8', windowsHide: true, env: { ...process.env, DANMU_TEST_INSTALLER: installer } });
      assert.equal(metadata.status, 0, metadata.stderr);
      assert.equal(metadata.stdout.trim(), '0.1.0.3');
    }
  } finally { await rm(tmp, { recursive: true, force: true }); }
});

test('payload admits compiled dependencies and forbids app scripts/private files', () => {
  for (const path of ['DanmuCompanion.exe', 'DanmuCompanion.Probe.exe', 'coreclr.dll', 'zh-Hans/System.Windows.Forms.resources.dll',
    'ui/dist/assets/main-123.js', 'MicrosoftEdgeWebview2Setup.exe', 'licenses/React/LICENSE.txt', 'manifest.json']) {
    assert.equal(allowedEntry(path), true, path);
  }
  for (const path of ['desktop/Host.ps1', 'pwsh.exe', 'powershell.exe', 'System.Management.Automation.dll', '.deps/pwsh/pwsh.dll',
    'start.bat', 'api.dat', 'data/preferences.json', 'obj/cache.json', 'a/../coreclr.dll', 'C:/private.dll', '/root.dll',
    'ui/dist/assets/../../escape.js', 'assets/CON.txt', 'WebView2Runtime/evil.ps1', 'WebView2Runtime/evil.bat', 'WebView2Runtime/msedgewebview2.exe']) {
    assert.equal(allowedEntry(path), false, path);
  }
  assert.equal(allowedEntry('WebView2Runtime/show_third_party_software_licenses.bat'), false,
    'The shared-browser release must not accidentally include a private runtime, including its credits helper.');
});

test('shared browser payload has a hard 210 MiB budget without padding', () => {
  assert.equal(assertPayloadBudget([{ length: 200 * 1024 * 1024 }], 1000), 200 * 1024 * 1024 + 1000);
  assert.throws(() => assertPayloadBudget([{ length: 210 * 1024 * 1024 }], 1), /budget/);
  assert.throws(() => assertPayloadBudget([{ length: -1 }]), /length/);
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
