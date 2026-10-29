import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { cp, mkdir, readFile, writeFile, copyFile, rename } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import { fileHash, inventory, releaseNames, walkFiles } from './release-lib.mjs';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const args = process.argv.slice(2);
let version, output = join(root, 'artifacts', 'releases'), allowDirty = false;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--version') version = args[++i];
  else if (args[i] === '--output') output = resolve(args[++i]);
  else if (args[i] === '--allow-dirty') allowDirty = true;
  else throw new Error('Unknown release build argument.');
}
const names = releaseNames(version);
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
const sourceCommit = run('git', ['rev-parse', 'HEAD'], true);
const sourceDirty = Boolean(run('git', ['status', '--porcelain', '--untracked-files=all'], true));
if (sourceDirty && !allowDirty) throw new Error('Commit release sources before producing final artifacts; use --allow-dirty only for candidates.');
for (const name of [names.portable, names.installer, 'SHA256SUMS.txt']) {
  if (existsSync(join(output, name))) throw new Error('Refusing to overwrite an existing release artifact.');
}
for (const dependency of [lock.dotnetSdk, lock.webview2Sdk, lock.windowsProjection, lock.webview2Runtime, lock.inno]) {
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
await mkdir(payload, { recursive: true }); await mkdir(output, { recursive: true });
run(process.env.ComSpec ?? 'cmd.exe', ['/d', '/s', '/c', 'npm.cmd --prefix ui run build']);
for (const [project, directory] of [['NativeHost', 'host'], ['NativeProbe', 'probe']]) {
  const published = join(work, directory);
  run(dotnet, ['publish', `desktop/${project}/${project}.csproj`, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', published]);
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
await mkdir(join(work, 'browser'));
run(join(process.env.SystemRoot, 'System32', 'expand.exe'), [join(root, lock.webview2Runtime.archive), '-F:*', join(work, 'browser')], true);
await rename(join(work, 'browser', `Microsoft.WebView2.FixedVersionRuntime.${lock.webview2Runtime.version}.x64`), join(payload, 'WebView2Runtime'));
await mkdir(join(payload, 'licenses', 'Dotnet'), { recursive: true });
for (const file of ['LICENSE.txt', 'ThirdPartyNotices.txt']) await copyFile(join(root, '.deps', 'dotnet', file), join(payload, 'licenses', 'Dotnet', file));
await copyFile(join(root, 'LICENSE'), join(payload, 'LICENSE'));
await copyFile(join(root, 'docs', 'THIRD-PARTY-NOTICES.md'), join(payload, 'THIRD-PARTY-NOTICES.md'));
await copyFile(join(root, 'docs', 'BETA-USER-GUIDE.md'), join(payload, 'README.md'));
const license = await readFile(join(root, 'LICENSE'), 'utf8');
const runtimeLicense = await readFile(join(root, 'licenses', 'WebView2Runtime', 'LICENSE.txt'), 'utf8');
await writeFile(join(payload, 'DISTRIBUTION-LICENSE.txt'), '\ufeff' + license + '\r\n\r\nBundled Microsoft WebView2 Runtime terms (separate from the original application MIT license):\r\n\r\n' + runtimeLicense.replace(/^\ufeff/, ''), 'utf8');
const manifest = { schemaVersion: 1, version, sourceCommit, sourceDirty, baseline: 'v0.1.0-preview.15',
  dependencies: Object.fromEntries(['dotnetSdk', 'dotnetRuntime', 'webview2Sdk', 'windowsProjection', 'webview2Runtime', 'inno'].map(key => [key, lock[key]])),
  files: await inventory(payload) };
await writeFile(join(payload, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
run(join(payload, 'DanmuCompanion.exe'), ['--check']);
run(dotnet, ['build', 'scripts/NativeReleaseTools/NativeReleaseTools.csproj', '-c', 'Release']);
const tools = join(root, 'scripts', 'NativeReleaseTools', 'bin', 'Release', 'net10.0', 'win-x64', 'NativeReleaseTools.exe');
run(tools, ['pack', payload, join(output, names.portable)]);
run(join(root, '.deps', 'inno', 'ISCC.exe'), [`/DAppVersion=${version}`, `/DPayloadDir=${payload}`, `/DReleaseDir=${output}`, 'installer/DanmuCompanion.iss']);
let sums = '';
for (const file of [names.installer, names.portable]) sums += `${await fileHash(join(output, file))}  ${file}\n`;
await writeFile(join(output, 'SHA256SUMS.txt'), sums, { flag: 'wx' });
await writeFile(join(work, 'build-result.json'), JSON.stringify({ version, sourceCommit, sourceDirty, payload, output, files: manifest.files.length }, null, 2));
console.log(JSON.stringify({ passed: true, version, output, payload, sourceDirty, files: manifest.files.length }, null, 2));
