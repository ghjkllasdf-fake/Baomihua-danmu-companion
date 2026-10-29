import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { cp, mkdir, readFile, writeFile, copyFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { fileHash, inventory, releaseNames, walkFiles, assertPayloadBudget, validateApiProvenance } from './release-lib.mjs';
import { sourceState, requireSameSource } from './source-state.mjs';
import { constants } from 'node:fs';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const args = process.argv.slice(2);
let version, output, allowDirty = false, includeApi = false;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--version') version = args[++i];
  else if (args[i] === '--output') output = resolve(args[++i]);
  else if (args[i] === '--allow-dirty') allowDirty = true;
  else if (args[i] === '--include-api') includeApi = true;
  else throw new Error('Unknown release build argument.');
}
const names = releaseNames(version, includeApi);
output ??= join(root, 'artifacts', 'releases', version, ...(includeApi ? ['includeAPI'] : []));
const projectVersion = (await readFile(join(root, 'desktop', 'NativeCommon.props'), 'utf8')).match(/<Version>([^<]+)<\/Version>/)?.[1];
if (version !== projectVersion) throw new Error('Artifact version must match the compiled application version.');
const lock = JSON.parse(await readFile(join(root, 'native-toolchain.json'), 'utf8'));
const env = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1',
  DOTNET_CLI_HOME: join(root, '.deps', 'dotnet-home') };
function run(exe, arguments_, capture = false) {
  const result = spawnSync(exe, arguments_, { cwd: root, env, windowsHide: true,
    stdio: capture ? 'pipe' : 'inherit', encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 });
  if (result.error || result.status !== 0) throw new Error(`${exe} failed: ${result.error?.message ?? result.stderr ?? result.status}`);
  return result.stdout?.trim();
}
const dotnet = join(root, '.deps', 'dotnet', 'dotnet.exe');
const source = await sourceState(root);
const sourceCommit = source.head;
const sourceDirty = Boolean(source.status);
if (sourceDirty && !allowDirty) throw new Error('Commit release sources before producing final artifacts; use --allow-dirty only for candidates.');
for (const name of [names.portable, names.installer, 'SHA256SUMS.txt']) {
  if (existsSync(join(output, name))) throw new Error('Refusing to overwrite an existing release artifact.');
}
for (const dependency of [lock.dotnetSdk, lock.webview2Sdk, lock.windowsProjection, lock.webview2Bootstrapper, lock.inno]) {
  const algorithm = dependency.sha512 ? 'sha512' : 'sha256';
  if (await fileHash(join(root, dependency.archive), algorithm) !== dependency[algorithm]) throw new Error('Pinned build archive checksum mismatch.');
}
for (const file of lock.runtimeFiles) {
  if (await fileHash(join(root, file.path)) !== file.sha256) throw new Error('Pinned runtime assembly differs from verified input.');
}
if (await fileHash(join(root, '.deps', 'inno', 'ISCC.exe')) !== lock.inno.compilerSha256) throw new Error('Installer compiler differs from its verified extraction.');
if (run(dotnet, ['--version'], true) !== lock.dotnetSdk.version) throw new Error('Wrong local SDK version.');
const work = join(root, 'artifacts', 'native-build', `${version}-${randomUUID()}`);
const payload = join(work, 'payload');
const staged = join(work, 'release');
await mkdir(payload, { recursive: true }); await mkdir(staged); await mkdir(output, { recursive: true });
run(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', 'npm.cmd --prefix ui ci --ignore-scripts']);
run(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', 'npm.cmd --prefix ui run build']);
for (const [project, directory] of [['NativeHost', 'host'], ['NativeProbe', 'probe']]) {
  const published = join(work, directory);
  run(dotnet, ['restore', `desktop/${project}/${project}.csproj`, '--locked-mode', '-r', 'win-x64']);
  run(dotnet, ['publish', `desktop/${project}/${project}.csproj`, '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', published]);
  for (const relative of await walkFiles(published)) {
    if (/\.(?:pdb|xml)$/i.test(relative)) continue;
    const source = join(published, relative), target = join(payload, relative);
    await mkdir(dirname(target), { recursive: true });
    if (existsSync(target)) {
      if (await fileHash(source) !== await fileHash(target)) throw new Error('Published executables disagree on a shared dependency.');
    } else await copyFile(source, target);
  }
}
await cp(join(root, 'ui', 'dist'), join(payload, 'ui', 'dist'), { recursive: true });
await cp(join(root, 'assets'), join(payload, 'assets'), { recursive: true });
await cp(join(root, 'licenses', 'WebView2Runtime'), join(payload, 'licenses', 'WebView2Runtime'), { recursive: true });
let apiInventory = null, apiInventorySha256 = null;
if (includeApi) {
  const apiSource = join(root, '.deps', 'includeapi', 'payload');
  const inventoryPath = join(root, '.deps', 'includeapi', 'payload-inventory.json');
  const sourceInventory = JSON.parse(await readFile(inventoryPath, 'utf8'));
  if (sourceInventory.schemaVersion !== 1 || !Array.isArray(sourceInventory.files) ||
      sourceInventory.fileCount !== sourceInventory.files.length ||
      sourceInventory.totalBytes !== sourceInventory.files.reduce((sum, record) => sum + record.length, 0) ||
      !['api/runtime.json', 'api/node.exe', 'api/entry.mjs'].every(name => sourceInventory.files.some(record => record.path === name)))
    throw new Error('Invalid or incomplete includeAPI input inventory.');
  apiInventory = sourceInventory.files;
  await inventory(apiSource, apiInventory);
  await validateApiProvenance(join(apiSource, 'api'), join(root, 'backend'));
  apiInventorySha256 = await fileHash(inventoryPath);
  await cp(join(apiSource, 'api'), join(payload, 'api'), { recursive: true });
}
async function copyLicense(source, component, name) {
  const directory = join(payload, 'licenses', component);
  await mkdir(directory, { recursive: true });
  await copyFile(join(root, source), join(directory, name));
}
for (const file of ['LICENSE.txt', 'NOTICE.txt', 'Microsoft.Web.WebView2.nuspec']) {
  await copyLicense(`.deps/webview2/${file}`, 'WebView2', file);
}
await copyLicense('licenses/Windows-SDK.rtf', 'WindowsSDK', 'Windows-SDK.rtf');
await copyLicense('.deps/windows-sdk/Microsoft.Windows.SDK.NET.Ref.nuspec', 'WindowsSDK', 'Microsoft.Windows.SDK.NET.Ref.nuspec');
for (const [component, name] of [['react', 'React'], ['react-dom', 'ReactDOM'], ['scheduler', 'Scheduler']]) {
  await copyLicense(`ui/node_modules/${component}/LICENSE`, name, 'LICENSE.txt');
}
await copyFile(join(root, '.deps', 'webview2', 'runtimes', 'win-x64', 'native', 'WebView2Loader.dll'), join(payload, 'WebView2Loader.dll'));
await copyFile(join(root, lock.webview2Bootstrapper.archive), join(payload, 'MicrosoftEdgeWebview2Setup.exe'));
await mkdir(join(payload, 'licenses', 'Dotnet'), { recursive: true });
for (const file of ['LICENSE.txt', 'ThirdPartyNotices.txt']) await copyFile(join(root, '.deps', 'dotnet', file), join(payload, 'licenses', 'Dotnet', file));
await copyFile(join(root, 'LICENSE'), join(payload, 'LICENSE'));
await copyFile(join(root, 'docs', 'THIRD-PARTY-NOTICES.md'), join(payload, 'THIRD-PARTY-NOTICES.md'));
await copyFile(join(root, 'docs', includeApi ? 'INCLUDEAPI-USER-GUIDE.md' : 'BETA-USER-GUIDE.md'), join(payload, 'README.md'));
const license = await readFile(join(root, 'LICENSE'), 'utf8');
const runtimeLicense = await readFile(join(root, 'licenses', 'WebView2Runtime', 'LICENSE.txt'), 'utf8');
await writeFile(join(payload, 'DISTRIBUTION-LICENSE.txt'), '\ufeff' + license + '\r\n\r\nMicrosoft WebView2 Runtime terms (shared Evergreen Runtime; only its bootstrapper is bundled; separate from application MIT license):\r\n\r\n' + runtimeLicense.replace(/^\ufeff/, '') +
  (includeApi ? '\r\n\r\nincludeAPI includes separately licensed Node.js, danmu_api and dependencies. See README.md, THIRD-PARTY-NOTICES.md and the license files under api/. The upstream danmu_api LICENSE is AGPL-3.0; it is not covered by the application MIT license.\r\n' : ''), 'utf8');
const payloadFiles = [...await walkFiles(payload), 'manifest.json', 'install-files.txt'].sort();
await writeFile(join(payload, 'install-files.txt'), payloadFiles.map(file => file.replaceAll('/', '\\')).join('\r\n') + '\r\n');
const payloadFileList = join(work, 'IncomingPayload.iss');
await writeFile(payloadFileList, 'procedure AddIncomingPayloadFiles(Files: TStringList);\nbegin\n' +
  payloadFiles.map(file => `  Files.Add('${file.replaceAll('/', '\\').replaceAll("'", "''")}');`).join('\n') + '\nend;\n');
const manifest = { schemaVersion: 1, version, sourceCommit, sourceDirty, baseline: 'v0.1.0-preview.15', browserDistribution: 'evergreen', variant: includeApi ? 'includeAPI' : 'standard',
  ...(includeApi ? { apiInventorySha256 } : {}),
  dependencies: { ...Object.fromEntries(['dotnetSdk', 'dotnetRuntime', 'webview2Sdk', 'windowsProjection', 'webview2Bootstrapper', 'inno'].map(key => [key, lock[key]])),
    webview2Runtime: { distribution: 'evergreen', minimumMajor: 124, bundled: false } },
  files: await inventory(payload, apiInventory) };
const manifestText = JSON.stringify(manifest, null, 2) + '\n';
const payloadBytes = assertPayloadBudget(manifest.files, Buffer.byteLength(manifestText), includeApi);
await writeFile(join(payload, 'manifest.json'), manifestText);
run(join(payload, 'DanmuCompanion.exe'), ['--check']);
run(dotnet, ['restore', 'scripts/NativeReleaseTools/NativeReleaseTools.csproj', '--locked-mode']);
run(dotnet, ['build', 'scripts/NativeReleaseTools/NativeReleaseTools.csproj', '--no-restore', '-c', 'Release']);
const tools = join(root, 'scripts', 'NativeReleaseTools', 'bin', 'Release', 'net10.0', 'win-x64', 'NativeReleaseTools.exe');
run(tools, ['pack', payload, join(staged, names.portable)]);
run(join(root, '.deps', 'inno', 'ISCC.exe'), [`/DAppVersion=${version}`, `/DPayloadDir=${payload}`, `/DPayloadFileList=${payloadFileList}`, `/DReleaseDir=${staged}`,
  ...(includeApi ? ['/DIncludeApi=1'] : []), 'installer/DanmuCompanion.iss']);
let sums = '';
for (const file of [names.installer, names.portable]) sums += `${await fileHash(join(staged, file))}  ${file}\n`;
await writeFile(join(staged, 'SHA256SUMS.txt'), sums, { flag: 'wx' });
requireSameSource(source, await sourceState(root));
for (const file of [names.installer, names.portable, 'SHA256SUMS.txt']) await copyFile(join(staged, file), join(output, file), constants.COPYFILE_EXCL);
await writeFile(join(work, 'build-result.json'), JSON.stringify({ version, sourceCommit, sourceDirty, payload, output, files: manifest.files.length, payloadBytes }, null, 2));
console.log(JSON.stringify({ passed: true, version, output, payload, sourceDirty, files: manifest.files.length, payloadBytes }, null, 2));
