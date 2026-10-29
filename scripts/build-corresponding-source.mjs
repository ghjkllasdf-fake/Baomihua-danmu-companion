import { constants } from 'node:fs';
import { copyFile, lstat, mkdir, mkdtemp, open, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { applyExactPatch, lockedSourceFiles } from '../backend/prepare-lib.mjs';
import { fileHash, inventory, releaseNames, releaseVersions, validateApiProvenance, walkFiles } from './release-lib.mjs';
import { requireSameSource, sourceState } from './source-state.mjs';

// Expanded sources permit historical scripts and image resources, but not local
// build outputs, credentials or compiled binaries. Pinned upstream archives may
// contain their own binary test fixtures and remain isolated under archives/.
export function assertSourcePath(path, allowArchive = false, allowUpstreamExample = false) {
  if (!path || path.startsWith('/') || /[\\:\x00-\x1f<>"|?*]/.test(path)) throw new Error('Unsafe source path.');
  const parts = path.split('/');
  const upstreamExample = /^(?:third-party\/)?upstream\/(?:original|modified)\/config\/\.env\.example$/.test(path) ||
    (allowUpstreamExample && path === 'config/.env.example');
  if (parts.some(part => !part || part === '.' || part === '..' || /[. ]$/.test(part) ||
    /^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)/i.test(part) ||
    /^(?:\.git|\.deps|artifacts|node_modules)$/i.test(part) ||
    (/^\.env(?:\..*)?$/i.test(part) && !(upstreamExample && part === '.env.example'))) ||
    /\.(?:exe|dll|so|dylib|node|wasm|pdb|obj|o|a|lib|class|jar|7z|pem|pfx|p12|key)$/i.test(path) ||
    (/\.(?:zip|tgz|gz|xz)$/i.test(path) && !(allowArchive && /^(?:third-party\/)?archives\/.+\.(?:zip|tgz|tar\.gz|tar\.xz)$/i.test(path))))
    throw new Error(`Forbidden source entry: ${path}`);
}

export async function sourceInventory(root, expected = null, archiveRecords = [], allowUpstreamExample = false) {
  const records = [], seen = new Set();
  const archives = new Map();
  for (const record of archiveRecords) {
    if (!record?.path || archives.has(record.path) || !Number.isSafeInteger(record.length) || record.length < 0 || !/^[a-f0-9]{64}$/.test(record.sha256))
      throw new Error('Invalid reviewed archive record.');
    assertSourcePath(record.path, true);
    if (!/^(?:third-party\/)?archives\/.+\.(?:zip|tgz|tar\.gz|tar\.xz)$/i.test(record.path)) throw new Error('Invalid source archive path.');
    archives.set(record.path, record);
  }
  for (const path of await walkFiles(root)) {
    const expectedArchive = archives.get(path);
    assertSourcePath(path, Boolean(expectedArchive), allowUpstreamExample);
    const key = path.toLowerCase();
    if (seen.has(key)) throw new Error('Case-colliding source paths.');
    seen.add(key);
    const file = join(root, path), handle = await open(file, 'r');
    const header = Buffer.alloc(6);
    try { await handle.read(header, 0, 6, 0); } finally { await handle.close(); }
    if (expectedArchive) {
      const signature = path.toLowerCase().endsWith('.zip') ? [0x50, 0x4b, 0x03, 0x04] :
        path.toLowerCase().endsWith('.xz') ? [0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00] : [0x1f, 0x8b, 0x08];
      if (!header.subarray(0, signature.length).equals(Buffer.from(signature))) throw new Error(`Invalid source archive magic: ${path}`);
    } else
    if (header.subarray(0, 2).equals(Buffer.from('MZ')) ||
        header.subarray(0, 4).equals(Buffer.from([0x7f, 0x45, 0x4c, 0x46])) ||
        header.subarray(0, 4).equals(Buffer.from([0x00, 0x61, 0x73, 0x6d])) ||
        header.subarray(0, 4).equals(Buffer.from([0x50, 0x4b, 0x03, 0x04])) ||
        header.subarray(0, 3).equals(Buffer.from([0x1f, 0x8b, 0x08])) ||
        header.equals(Buffer.from([0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00]))) throw new Error(`Compiled binary or archive in source input: ${path}`);
    const record = { path, length: (await lstat(file)).size, sha256: await fileHash(file) };
    if (expectedArchive && (expectedArchive.length !== record.length || expectedArchive.sha256 !== record.sha256)) throw new Error(`Source archive inventory mismatch: ${path}`);
    records.push(record);
  }
  if ([...archives.keys()].some(path => !records.some(record => record.path === path))) throw new Error('Reviewed source archive missing.');
  if (expected !== null && JSON.stringify(records) !== JSON.stringify(expected)) throw new Error('Source inventory mismatch.');
  return records;
}

export async function verifyModifiedUpstream(original, modified, patchedPath, patch) {
  assertSourcePath(patchedPath);
  const before = await walkFiles(original), after = await walkFiles(modified);
  if (JSON.stringify(before) !== JSON.stringify(after) || !before.includes(patchedPath)) throw new Error('Upstream source file sets differ.');
  for (const path of before) {
    assertSourcePath(path, false, true);
    if (path === patchedPath) {
      const expected = applyExactPatch(await readFile(join(original, path), 'utf8'), patch);
      if (expected !== await readFile(join(modified, path), 'utf8')) throw new Error('Modified upstream patch mismatch.');
    } else if (await fileHash(join(original, path)) !== await fileHash(join(modified, path)))
      throw new Error(`Unexpected modified upstream source: ${path}`);
  }
}

function run(executable, args, cwd) {
  const result = spawnSync(executable, args, { cwd, windowsHide: true, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  if (result.error || result.status !== 0) throw new Error(`${executable} failed: ${result.error?.message ?? result.stderr}`);
  return result.stdout;
}

export async function verifyUpstreamInputs({ archive, archiveSha256, commit, original, modified, runtime, patch }) {
  archive = resolve(archive);
  if (!/^[a-f0-9]{40}$/.test(commit) || !/^[a-f0-9]{64}$/.test(archiveSha256) ||
      await fileHash(archive) !== archiveSha256) throw new Error('Pinned upstream archive hash mismatch.');
  if (patch.schemaVersion !== 1 || patch.sourceCommit !== commit) throw new Error('Upstream patch provenance mismatch.');
  const top = `danmu_api-${commit}`;
  const names = run('tar.exe', ['-tf', archive], dirname(archive)).split(/\r?\n/).filter(Boolean);
  if (!names.length) throw new Error('Empty upstream archive.');
  for (const raw of names) {
    const name = raw.replace(/\/$/, '');
    if (name !== top && !name.startsWith(top + '/')) throw new Error('Unsafe upstream archive path.');
    if (name !== top) assertSourcePath(name.slice(top.length + 1), false, true);
  }
  const temp = await mkdtemp(join(tmpdir(), 'DanmuUpstream-'));
  try {
    run('tar.exe', ['-xf', archive, '-C', temp], temp);
    const authoritative = await sourceInventory(join(temp, top), null, [], true);
    try { await sourceInventory(original, authoritative, [], true); }
    catch { throw new Error('Upstream original differs from pinned archive.'); }
    await verifyModifiedUpstream(original, modified, patch.path, patch);
    try { await sourceInventory(runtime, await sourceInventory(modified, null, [], true), [], true); }
    catch { throw new Error('Upstream modified source differs from packaged runtime.'); }
  } finally { await rm(temp, { recursive: true, force: true }); }
}

export async function validateSourceArchivePins(backend, toolchain, records) {
  const lockPath = join(backend, 'package-lock.json'), sourcesPath = join(backend, 'dependency-sources.json');
  if (await fileHash(lockPath) !== toolchain.productionLock?.sha256 ||
      await fileHash(sourcesPath) !== toolchain.dependencySourcesSha256) throw new Error('Tracked source lock checksum mismatch.');
  const lock = JSON.parse(await readFile(lockPath, 'utf8')), sources = JSON.parse(await readFile(sourcesPath, 'utf8'));
  const locked = lockedSourceFiles(lock, sources);
  if (sources.packages.some(item => !item.sourceUrl.endsWith('/' + item.sourceCommit)))
    throw new Error('Source archive URL does not match the recorded commit.');
  const expected = locked.map(item => ({ path: `archives/${item.file}`, sha256: item.sha256 }));
  expected.push({ path: 'archives/upstream.zip', sha256: toolchain.upstream.archiveSha256 },
    { path: `archives/node-v${toolchain.node.version}.tar.xz`, sha256: toolchain.node.sourceSha256 });
  const actual = records.filter(record => record.path.startsWith('archives/'));
  const keys = new Map(actual.map(record => [record.path, record.sha256]));
  if (keys.size !== actual.length || expected.length !== actual.length ||
      expected.some(record => !/^[a-f0-9]{64}$/.test(record.sha256) || keys.get(record.path) !== record.sha256))
    throw new Error('Source archives do not exactly match the tracked dependency and upstream locks.');
  return locked;
}

export async function verifyArchiveSRI(tree, locked) {
  for (const item of locked) {
    if (!item.integrity) continue;
    if (!/^[A-Za-z0-9._-]+\.tgz$/.test(item.file)) throw new Error('Invalid npm source archive name.');
    const digest = Buffer.from(await fileHash(join(tree, 'archives', item.file), 'sha512'), 'hex').toString('base64');
    if (`sha512-${digest}` !== item.integrity) throw new Error(`npm source archive SRI mismatch: ${item.file}`);
  }
}

export async function buildFromReviewedTree({ root, tree, inventoryPath, output, version, tool, expectedState, beforePublish = async () => {} }) {
  releaseNames(version);
  root = resolve(root); tree = resolve(tree); inventoryPath = resolve(inventoryPath); output = resolve(output);
  const before = await sourceState(root);
  if (expectedState) requireSameSource(expectedState, before);
  if (before.status) throw new Error('Corresponding-source build requires clean committed Git source.');
  try { await lstat(output); throw new Error('Refusing to overwrite an existing source archive.'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const treeRelative = relative(tree, output);
  if (!isAbsolute(treeRelative) && treeRelative !== '..' && !treeRelative.startsWith('..' + sep))
    throw new Error('Source archive output must be outside the reviewed input tree.');
  const projectRelative = relative(root, output);
  if (!isAbsolute(projectRelative) && projectRelative !== '..' && !projectRelative.startsWith('..' + sep)) {
    const ignored = spawnSync('git', ['check-ignore', '-q', '--', projectRelative], { cwd: root, windowsHide: true });
    if (ignored.error || ignored.status !== 0) throw new Error('Source archive output must be outside the project or Git-ignored.');
  }
  const listed = run('git', ['ls-files', '--stage', '-z'], root).split('\0').filter(Boolean);
  const tracked = listed.map(line => {
    const match = /^(100644|100755) [a-f0-9]{40,64} 0\t(.+)$/.exec(line);
    if (!match) throw new Error('Tracked source contains a symlink, submodule or unmerged entry.');
    assertSourcePath(match[2]); return match[2];
  });
  if (!tracked.includes(relative(root, inventoryPath).replaceAll('\\', '/'))) throw new Error('Source inventory must be tracked by Git.');
  const input = JSON.parse(await readFile(inventoryPath, 'utf8'));
  if (input.schemaVersion !== 1 || !Array.isArray(input.files) || input.files.length === 0) throw new Error('Invalid reviewed source inventory.');
  const archives = input.files.filter(record => record.path.startsWith('archives/'));
  await sourceInventory(tree, input.files, archives);
  const temp = await mkdtemp(join(tmpdir(), 'DanmuCorrespondingSource-'));
  try {
    const stage = join(temp, 'stage'), project = join(stage, 'project'), dependencies = join(stage, 'third-party');
    await mkdir(project, { recursive: true }); await mkdir(dependencies);
    for (const [base, target, files] of [[root, project, tracked], [tree, dependencies, input.files.map(file => file.path)]]) {
      for (const path of files) {
        assertSourcePath(path, base === tree && archives.some(record => record.path === path));
        const source = join(base, path), destination = join(target, path);
        if (!(await lstat(source)).isFile()) throw new Error('Source input must be an ordinary file.');
        await mkdir(dirname(destination), { recursive: true });
        await copyFile(source, destination, constants.COPYFILE_EXCL);
      }
    }
    await sourceInventory(dependencies, input.files, archives);
    const files = await sourceInventory(stage, null, archives.map(record => ({ ...record, path: 'third-party/' + record.path })));
    const manifest = { schemaVersion: 1, version, variant: 'corresponding-source', sourceCommit: before.head,
      sourceDirty: false, inputInventorySha256: await fileHash(inventoryPath), files };
    const json = JSON.stringify(manifest, null, 2) + '\n';
    if (files.length > 131072 || Buffer.byteLength(json) > 32 * 1024 * 1024 ||
        files.some(file => file.length > 512 * 1024 * 1024) || files.reduce((total, file) => total + file.length, 0) > 2 * 1024 ** 3)
      throw new Error('Source archive exceeds its bounded limits.');
    await writeFile(join(stage, 'manifest.json'), json, { flag: 'wx' });
    const archive = join(temp, 'source.zip');
    run(tool, ['pack', stage, archive], root);
    await sourceInventory(tree, input.files, archives);
    await beforePublish();
    await sourceInventory(tree, input.files, archives);
    requireSameSource(before, await sourceState(root));
    await copyFile(archive, output, constants.COPYFILE_EXCL);
    if (await fileHash(output) !== await fileHash(archive)) throw new Error('Published source archive copy differs from verified archive.');
    return { output, sourceCommit: before.head, files: files.length, sha256: await fileHash(output) };
  } finally { await rm(temp, { recursive: true, force: true }); }
}

export async function buildCorrespondingSource({ root, version, directory, prepareTool }) {
  root = resolve(root);
  releaseNames(version);
  releaseVersions(await readFile(join(root, 'desktop', 'NativeCommon.props'), 'utf8'), version);
  const state = await sourceState(root);
  if (state.status) throw new Error('Corresponding-source build requires clean committed Git source.');
  const base = join(root, '.deps', 'includeapi'), tree = join(base, 'corresponding-source');
  const inventoryPath = join(root, 'backend', 'corresponding-source-inventory.json');
  const reviewed = JSON.parse(await readFile(inventoryPath, 'utf8'));
  for (const prefix of ['README.md', 'upstream/original/', 'upstream/modified/', 'dependencies/node/', 'dependencies/dan-any/'])
    if (!reviewed.files?.some(file => prefix.endsWith('/') ? file.path.startsWith(prefix) : file.path === prefix))
      throw new Error(`Missing reviewed corresponding source: ${prefix}`);
  const toolchain = JSON.parse(await readFile(join(root, 'backend', 'toolchain.json'), 'utf8'));
  const lockedArchives = await validateSourceArchivePins(join(root, 'backend'), toolchain, reviewed.files);
  if (!reviewed.files.some(file => file.path === 'dependencies/SOURCES.json' && file.sha256 === toolchain.dependencySourcesSha256))
    throw new Error('Dependency source manifest in the source tree is missing or mismatched.');
  for (const [name, expected] of Object.entries(toolchain.patches ?? {})) {
    if (!/^[A-Za-z0-9._-]+\.json$/.test(name) || await fileHash(join(root, 'backend', 'patches', name)) !== expected)
      throw new Error('Source patch lock mismatch.');
  }
  const patch = JSON.parse(await readFile(join(root, 'backend', 'patches', '0001-node-brotli.json'), 'utf8'));
  const upstream = { archive: join(base, 'downloads', 'upstream.zip'), archiveSha256: toolchain.upstream.archiveSha256,
    commit: toolchain.upstream.commit, original: join(tree, 'upstream', 'original'), modified: join(tree, 'upstream', 'modified'),
    runtime: join(base, 'payload', 'api', 'upstream'), patch };
  const validateInputs = async () => {
    const apiInventory = JSON.parse(await readFile(join(base, 'payload-inventory.json'), 'utf8'));
    if (apiInventory.schemaVersion !== 1 || apiInventory.fileCount !== apiInventory.files?.length ||
        apiInventory.totalBytes !== apiInventory.files.reduce((sum, record) => sum + record.length, 0)) throw new Error('Invalid prepared API inventory.');
    await inventory(join(base, 'payload'), apiInventory.files);
    await validateApiProvenance(join(base, 'payload', 'api'), join(root, 'backend'));
    await verifyArchiveSRI(tree, lockedArchives);
    await verifyUpstreamInputs(upstream);
  };
  await validateInputs();
  requireSameSource(state, await sourceState(root));
  const tool = await prepareTool(root);
  requireSameSource(state, await sourceState(root));
  directory ??= join(root, 'artifacts', 'releases', version);
  await mkdir(directory, { recursive: true });
  const output = join(directory, `DanmuCompanion-${version}-corresponding-source.zip`);
  return buildFromReviewedTree({ root, tree, inventoryPath, output, version, tool, expectedState: state, beforePublish: validateInputs });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
  const args = process.argv.slice(2);
  let version, directory;
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--version' && args[i + 1]) version = args[++i];
    else if (args[i] === '--output' && args[i + 1]) directory = resolve(args[++i]);
    else throw new Error('Usage: node scripts/build-corresponding-source.mjs --version VERSION [--output DIRECTORY]');
  }
  const prepareTool = async root => {
    const lock = JSON.parse(await readFile(join(root, 'native-toolchain.json'), 'utf8'));
    const dotnet = join(root, '.deps', 'dotnet', 'dotnet.exe');
    if (await fileHash(join(root, lock.dotnetSdk.archive), 'sha512') !== lock.dotnetSdk.sha512 ||
        run(dotnet, ['--version'], root).trim() !== lock.dotnetSdk.version) throw new Error('Pinned SDK mismatch.');
    run(dotnet, ['restore', 'scripts/NativeReleaseTools/NativeReleaseTools.csproj', '--locked-mode'], root);
    run(dotnet, ['build', 'scripts/NativeReleaseTools/NativeReleaseTools.csproj', '--no-restore', '-c', 'Release'], root);
    return join(root, 'scripts', 'NativeReleaseTools', 'bin', 'Release', 'net10.0', 'win-x64', 'NativeReleaseTools.exe');
  };
  console.log(JSON.stringify({ passed: true, ...await buildCorrespondingSource({ root, version, directory, prepareTool }) }));
}
