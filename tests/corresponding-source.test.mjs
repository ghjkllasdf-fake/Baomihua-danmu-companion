import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, writeFile, readFile, rm, cp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, relative, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { assertSourcePath, verifyModifiedUpstream, sourceInventory, buildFromReviewedTree, verifyUpstreamInputs, buildCorrespondingSource, validateSourceArchivePins, verifyArchiveSRI } from '../scripts/build-corresponding-source.mjs';
import { inventoryPayload } from '../backend/prepare-lib.mjs';

test('source build never silently succeeds without required arguments', () => {
  const result = spawnSync(process.execPath, ['scripts/build-corresponding-source.mjs'], { encoding: 'utf8' });
  assert.notEqual(result.status, 0);
});

test('source paths reject executable material and secrets but allow historical source scripts', () => {
  for (const path of ['desktop/Host.ps1', 'desktop/Verify-Package.bat', 'backend/entry.mjs', 'assets/app.ico'])
    assert.doesNotThrow(() => assertSourcePath(path));
  assert.doesNotThrow(() => assertSourcePath('config/.env.example', false, true));
  for (const path of ['upstream/original/config/.env.example', 'third-party/upstream/modified/config/.env.example'])
    assert.doesNotThrow(() => assertSourcePath(path));
  for (const path of ['../credentials.txt', '.deps/secret', 'artifacts/private.txt', 'x/node.exe', 'x/a.wasm', 'x/a.node',
    'x/.env', 'x/.env.local', 'config/.env.example', 'project/config/.env.example', 'x/key.pem', 'node_modules/package/index.js', 'x/evil.dll', 'x/CON.txt'])
    assert.throws(() => assertSourcePath(path));
});

test('source inventory verifies the exact files and bytes rather than trusting a supplied list', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourceInventory-'));
  try {
    await writeFile(join(root, 'source.ps1'), 'source');
    const records = await sourceInventory(root);
    assert.equal(records.length, 1);
    await sourceInventory(root, records);
    await writeFile(join(root, 'source.ps1'), 'edited');
    await assert.rejects(() => sourceInventory(root, records), /inventory/i);
    await writeFile(join(root, 'source.ps1'), 'source');
    await writeFile(join(root, 'extra.txt'), 'unexpected');
    await assert.rejects(() => sourceInventory(root, records), /inventory/i);
    await rm(join(root, 'extra.txt'));
    await writeFile(join(root, 'source.ps1'), Buffer.from([0x4d, 0x5a, 0x90, 0x00]));
    await assert.rejects(() => sourceInventory(root), /binary/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('only explicitly inventoried source archives in archives namespace may be nested', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourceArchives-'));
  try {
    await mkdir(join(root, 'archives'));
    const bytes = Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x11, 0x22]);
    await writeFile(join(root, 'archives', 'source.zip'), bytes);
    const record = { path: 'archives/source.zip', length: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
    await assert.rejects(() => sourceInventory(root), /Forbidden/);
    assert.deepEqual(await sourceInventory(root, [record], [record]), [record]);
    await assert.rejects(() => sourceInventory(root, [record], []), /Forbidden/);
    await writeFile(join(root, 'archives', 'source.zip'), Buffer.from([0x4d, 0x5a, 0, 0, 0, 0]));
    await assert.rejects(() => sourceInventory(root, [record], [record]), /archive|binary/i);
    await writeFile(join(root, 'archives', 'source.zip'), bytes);
    await writeFile(join(root, 'archives', 'unlisted.tgz'), Buffer.from([0x1f, 0x8b, 8, 0, 0, 0]));
    await assert.rejects(() => sourceInventory(root, [record], [record]), /Forbidden|inventory/i);
    await rm(join(root, 'archives', 'unlisted.tgz'));
    const xz = Buffer.from([0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00]);
    await writeFile(join(root, 'archives', 'node.tar.xz'), xz);
    const xzRecord = { path: 'archives/node.tar.xz', length: xz.length, sha256: createHash('sha256').update(xz).digest('hex') };
    assert.deepEqual(await sourceInventory(root, [xzRecord, record], [record, xzRecord]), [xzRecord, record]);
    await writeFile(join(root, 'archives', 'node.tar.xz'), bytes);
    await assert.rejects(() => sourceInventory(root, [xzRecord, record], [record, xzRecord]), /archive/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('reviewed source archives exactly match tracked package, Node and upstream locks', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourceLocks-'));
  try {
    const sha = value => createHash('sha256').update(value).digest('hex');
    const packageLock = { packages: { 'node_modules/example': { version: '1.0.0', integrity: 'sha512-test' } } };
    const sources = { schemaVersion: 1, packages: [{ name: 'example', version: '1.0.0', integrity: 'sha512-test', sourceCommit: 'a'.repeat(40),
      sourceFile: 'source.zip', sourceUrl: 'https://codeload.github.com/a/b/zip/' + 'a'.repeat(40), sourceSha256: sha('source'),
      tarballFile: 'npm.tgz', tarballUrl: 'https://registry.npmjs.org/example/-/example.tgz', tarballSha256: sha('npm') }] };
    const lockText = JSON.stringify(packageLock), sourceText = JSON.stringify(sources);
    await writeFile(join(root, 'package-lock.json'), lockText); await writeFile(join(root, 'dependency-sources.json'), sourceText);
    const toolchain = { productionLock: { sha256: sha(lockText) }, dependencySourcesSha256: sha(sourceText),
      upstream: { archiveSha256: sha('upstream') }, node: { version: '22.23.4', sourceSha256: sha('node') } };
    const record = (path, hash) => ({ path: `archives/${path}`, length: 1, sha256: hash });
    const records = [record('source.zip', sha('source')), record('npm.tgz', sha('npm')),
      record('upstream.zip', sha('upstream')), record('node-v22.23.4.tar.xz', sha('node'))];
    await validateSourceArchivePins(root, toolchain, records);
    await assert.rejects(() => validateSourceArchivePins(root, toolchain, records.slice(1)), /archive|source/i);
    await assert.rejects(() => validateSourceArchivePins(root, toolchain, [...records, record('extra.zip', sha('extra'))]), /archive|source/i);
    await assert.rejects(() => validateSourceArchivePins(root, { ...toolchain, dependencySourcesSha256: sha('tamper') }, records), /lock/i);
    sources.packages[0].sourceUrl = `https://codeload.github.com/a/b/zip/${'b'.repeat(40)}`;
    const wrongSourceText = JSON.stringify(sources); await writeFile(join(root, 'dependency-sources.json'), wrongSourceText);
    await assert.rejects(() => validateSourceArchivePins(root, { ...toolchain, dependencySourcesSha256: sha(wrongSourceText) }, records), /commit|source/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('npm archive bytes must also match lockfile SRI, not just reviewed SHA-256', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourceSRI-'));
  try {
    await mkdir(join(root, 'archives'));
    const bytes = Buffer.from([0x1f, 0x8b, 8, 0, 0, 0]);
    await writeFile(join(root, 'archives', 'npm.tgz'), bytes);
    const sri = 'sha512-' + createHash('sha512').update(bytes).digest('base64');
    await verifyArchiveSRI(root, [{ file: 'npm.tgz', integrity: sri }]);
    await assert.rejects(() => verifyArchiveSRI(root, [{ file: 'npm.tgz', integrity: 'sha512-bad' }]), /integrity|SRI/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('reviewed source archive is verified, refuses overwrite, dirty source and stale inputs', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourceBuild-'));
  const project = join(root, 'project'), tree = join(project, '.deps', 'sources');
  await mkdir(tree, { recursive: true });
  const output = join(root, 'source.zip');
  const tool = resolve('scripts/NativeReleaseTools/bin/Release/net10.0/win-x64/NativeReleaseTools.exe');
  const git = (...args) => { const result = spawnSync('git', args, { cwd: project, windowsHide: true }); assert.equal(result.status, 0); };
  try {
    await writeFile(join(project, '.gitignore'), '.deps/\n');
    await writeFile(join(project, 'project.ps1'), 'historical source');
    await writeFile(join(tree, 'upstream.js'), 'reviewed source');
    await mkdir(join(tree, 'archives'));
    const archiveBytes = Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x11, 0x22]);
    await writeFile(join(tree, 'archives', 'source.zip'), archiveBytes);
    const archiveRecord = { path: 'archives/source.zip', length: archiveBytes.length, sha256: createHash('sha256').update(archiveBytes).digest('hex') };
    const records = await sourceInventory(tree, null, [archiveRecord]);
    await writeFile(join(project, 'source-inventory.json'), JSON.stringify({ schemaVersion: 1, files: records }));
    git('init', '-q'); git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture');
    const args = { root: project, tree, inventoryPath: join(project, 'source-inventory.json'), output, version: '0.1.1-beta.3', tool };
    await assert.rejects(() => buildFromReviewedTree({ ...args, output: join(project, 'untracked-output.zip') }), /ignored|outside/i);
    const result = await buildFromReviewedTree(args);
    assert.equal(result.files, 5);
    assert.equal(spawnSync(tool, ['verify', output], { windowsHide: true }).status, 0);
    const original = await readFile(output);
    await assert.rejects(() => buildFromReviewedTree(args), /exist|overwrite/i);
    assert.deepEqual(await readFile(output), original);
    await assert.rejects(() => buildFromReviewedTree({ ...args, output: join(root, 'race.zip'),
      beforePublish: async () => writeFile(join(tree, 'upstream.js'), 'late modification') }), /inventory/i);
    await writeFile(join(tree, 'upstream.js'), 'reviewed source');
    await assert.rejects(() => buildFromReviewedTree({ ...args, output: join(root, 'race-project.zip'),
      beforePublish: async () => writeFile(join(project, 'project.ps1'), 'late modification') }), /changed/i);
    await writeFile(join(project, 'project.ps1'), 'historical source');
    await writeFile(join(project, 'project.ps1'), 'dirty edit');
    await assert.rejects(() => buildFromReviewedTree({ ...args, output: join(root, 'dirty.zip') }), /clean/i);
    await writeFile(join(project, 'project.ps1'), 'historical source');
    await writeFile(join(tree, 'upstream.js'), 'tampered');
    await assert.rejects(() => buildFromReviewedTree({ ...args, output: join(root, 'stale.zip') }), /inventory/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('modified upstream must equal the original plus exactly the reviewed patch', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourcePatch-'));
  try {
    const original = join(root, 'original'), modified = join(root, 'modified');
    await mkdir(original); await mkdir(modified);
    const sha = value => createHash('sha256').update(value).digest('hex');
    const patch = { originalSha256: sha('original'), modifiedSha256: sha('modified'), replacements: [{ from: 'original', to: 'modified' }] };
    await writeFile(join(original, 'codec.js'), 'original');
    await writeFile(join(modified, 'codec.js'), 'modified');
    await writeFile(join(original, 'other.js'), 'same'); await writeFile(join(modified, 'other.js'), 'same');
    await verifyModifiedUpstream(original, modified, 'codec.js', patch);
    await writeFile(join(modified, 'other.js'), 'tampered');
    await assert.rejects(() => verifyModifiedUpstream(original, modified, 'codec.js', patch), /upstream/i);
    await writeFile(join(modified, 'other.js'), 'same');
    await writeFile(join(modified, 'extra.js'), 'extra');
    await assert.rejects(() => verifyModifiedUpstream(original, modified, 'codec.js', patch), /upstream/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('upstream source is anchored to the pinned archive and actual packaged worker', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourceUpstream-'));
  try {
    const commit = 'a'.repeat(40), archiveRoot = join(root, `danmu_api-${commit}`);
    const original = join(root, 'original'), modified = join(root, 'modified'), runtime = join(root, 'runtime');
    for (const dir of [archiveRoot, original, modified, runtime]) await mkdir(dir);
    for (const dir of [archiveRoot, original, modified, runtime]) {
      await mkdir(join(dir, 'config'));
      await writeFile(join(dir, 'config', '.env.example'), 'EXAMPLE=placeholder\n');
    }
    const sha = value => createHash('sha256').update(value).digest('hex');
    for (const dir of [archiveRoot, original]) await writeFile(join(dir, 'codec.js'), 'original');
    for (const dir of [modified, runtime]) await writeFile(join(dir, 'codec.js'), 'modified');
    const archive = join(root, 'upstream.zip');
    assert.equal(spawnSync('tar.exe', ['-a', '-cf', archive, `danmu_api-${commit}`], { cwd: root, windowsHide: true }).status, 0);
    const archiveSha256 = sha(await readFile(archive));
    const patch = { schemaVersion: 1, sourceCommit: commit, path: 'codec.js', originalSha256: sha('original'),
      modifiedSha256: sha('modified'), replacements: [{ from: 'original', to: 'modified' }] };
    const args = { archive, archiveSha256, commit, original, modified, runtime, patch };
    await verifyUpstreamInputs(args);
    await verifyUpstreamInputs({ ...args, archive: relative(process.cwd(), archive) });
    await writeFile(join(runtime, 'codec.js'), 'stale');
    await assert.rejects(() => verifyUpstreamInputs(args), /upstream/i);
    await writeFile(join(runtime, 'codec.js'), 'modified');
    await writeFile(join(original, 'extra.js'), 'untrusted');
    await assert.rejects(() => verifyUpstreamInputs(args), /upstream/i);
    await rm(join(original, 'extra.js'));
    await assert.rejects(() => verifyUpstreamInputs({ ...args, archiveSha256: '0'.repeat(64) }), /archive/i);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test('complete source build binds reviewed source to the packaged upstream and refuses stale payload', async () => {
  const tmp = await mkdtemp(join(tmpdir(), 'DanmuSourceFull-')), root = join(tmp, 'project');
  const tree = join(root, '.deps', 'includeapi', 'corresponding-source'), api = join(root, '.deps', 'includeapi', 'payload', 'api');
  const backend = join(root, 'backend'), commit = 'a'.repeat(40), sha = value => createHash('sha256').update(value).digest('hex');
  const git = (...args) => { const result = spawnSync('git', args, { cwd: root, windowsHide: true }); assert.equal(result.status, 0); };
  try {
    for (const dir of [backend, join(backend, 'patches'), join(root, 'desktop'), join(tree, 'upstream', 'original'),
      join(tree, 'upstream', 'modified'), join(tree, 'dependencies', 'node'), join(tree, 'dependencies', 'dan-any'),
      join(tree, 'archives'), join(api, 'upstream'), join(root, '.deps', 'includeapi', 'downloads')]) await mkdir(dir, { recursive: true });
    await writeFile(join(root, '.gitignore'), '.deps/\n');
    await writeFile(join(root, 'desktop', 'NativeCommon.props'), '<Version>0.1.1-beta.3</Version><FileVersion>0.1.1.3</FileVersion>');
    for (const [path, content] of [['README.md', 'rebuild'], ['dependencies/node/source.txt', 'node source'],
      ['dependencies/dan-any/source.txt', 'library source'], ['upstream/original/codec.js', 'original'], ['upstream/modified/codec.js', 'modified']])
      await writeFile(join(tree, path), content);
    await writeFile(join(api, 'upstream', 'codec.js'), 'modified');
    const archiveRoot = join(tmp, `danmu_api-${commit}`); await mkdir(archiveRoot);
    await cp(join(tree, 'upstream', 'original', 'codec.js'), join(archiveRoot, 'codec.js'));
    const archive = join(root, '.deps', 'includeapi', 'downloads', 'upstream.zip');
    assert.equal(spawnSync('tar.exe', ['-a', '-cf', archive, `danmu_api-${commit}`], { cwd: tmp, windowsHide: true }).status, 0);
    await cp(archive, join(tree, 'archives', 'upstream.zip'));
    const nodeArchive = Buffer.from([0xfd, 0x37, 0x7a, 0x58, 0x5a, 0]);
    await writeFile(join(tree, 'archives', 'node-v22.23.4.tar.xz'), nodeArchive);
    const sourceZip = Buffer.from([0x50, 0x4b, 3, 4, 1, 2]), npmTgz = Buffer.from([0x1f, 0x8b, 8, 0, 0, 0]);
    await writeFile(join(tree, 'archives', 'dan-any.zip'), sourceZip); await writeFile(join(tree, 'archives', 'dan-any.tgz'), npmTgz);
    const patch = { schemaVersion: 1, sourceCommit: commit, path: 'codec.js', originalSha256: sha('original'), modifiedSha256: sha('modified'),
      replacements: [{ from: 'original', to: 'modified' }] };
    await writeFile(join(backend, 'patches', '0001-node-brotli.json'), JSON.stringify(patch));
    for (const name of ['entry.mjs', 'package.json']) {
      await writeFile(join(backend, name), name); await writeFile(join(api, name), name);
    }
    const npmIntegrity = 'sha512-' + createHash('sha512').update(npmTgz).digest('base64');
    const packageLock = JSON.stringify({ packages: { 'node_modules/@dan-uni/dan-any': { version: '2.3.9', integrity: npmIntegrity } } });
    await writeFile(join(backend, 'package-lock.json'), packageLock); await writeFile(join(api, 'package-lock.json'), packageLock);
    const sourceLock = JSON.stringify({ schemaVersion: 1, packages: [{ name: '@dan-uni/dan-any', version: '2.3.9', integrity: npmIntegrity,
      sourceCommit: commit, sourceFile: 'dan-any.zip', sourceUrl: `https://codeload.github.com/a/b/zip/${commit}`, sourceSha256: sha(sourceZip),
      tarballFile: 'dan-any.tgz', tarballUrl: 'https://registry.npmjs.org/a.tgz', tarballSha256: sha(npmTgz) }] });
    await writeFile(join(backend, 'dependency-sources.json'), sourceLock); await writeFile(join(tree, 'dependencies', 'SOURCES.json'), sourceLock);
    const toolchain = { schemaVersion: 1, upstream: { commit, archiveSha256: sha(await readFile(archive)) },
      node: { version: '22.23.4', sha256: sha('node'), sourceSha256: sha(nodeArchive) }, productionLock: { sha256: sha(packageLock) },
      dependencySourcesSha256: sha(sourceLock), packageManifestSha256: sha('package.json') };
    await writeFile(join(backend, 'toolchain.json'), JSON.stringify(toolchain));
    await writeFile(join(api, 'toolchain.json'), JSON.stringify(toolchain));
    await writeFile(join(api, 'node.exe'), 'node');
    await writeFile(join(api, 'runtime.json'), JSON.stringify({ schemaVersion: 1, upstreamCommit: commit, nodeVersion: '22.23.4' }));
    await writeFile(join(root, '.deps', 'includeapi', 'payload-inventory.json'), JSON.stringify(await inventoryPayload(join(root, '.deps', 'includeapi', 'payload'))));
    const zipBytes = await readFile(archive);
    const archiveRecords = [{ path: 'archives/upstream.zip', length: zipBytes.length, sha256: sha(zipBytes) },
      { path: 'archives/node-v22.23.4.tar.xz', length: nodeArchive.length, sha256: sha(nodeArchive) },
      { path: 'archives/dan-any.zip', length: sourceZip.length, sha256: sha(sourceZip) },
      { path: 'archives/dan-any.tgz', length: npmTgz.length, sha256: sha(npmTgz) }];
    await writeFile(join(backend, 'corresponding-source-inventory.json'), JSON.stringify({ schemaVersion: 1, files: await sourceInventory(tree, null, archiveRecords) }));
    git('init', '-q'); git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture');
    const tool = resolve('scripts/NativeReleaseTools/bin/Release/net10.0/win-x64/NativeReleaseTools.exe');
    const options = { root, version: '0.1.1-beta.3', directory: join(tmp, 'out'), prepareTool: async () => tool };
    await assert.rejects(() => buildCorrespondingSource({ ...options, directory: join(tmp, 'tool-race'), prepareTool: async () => {
      await writeFile(join(root, '.gitignore'), '.deps/\ncache/\n');
      git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'change during build');
      return tool;
    } }), /changed/i);
    await writeFile(join(root, '.gitignore'), '.deps/\n');
    git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'restore after race');
    const result = await buildCorrespondingSource(options);
    assert.match(result.output, /DanmuCompanion-0\.1\.1-beta\.3-corresponding-source\.zip$/);
    assert.equal(spawnSync(tool, ['verify', result.output], { windowsHide: true }).status, 0);
    const wrongZip = Buffer.from([0x50, 0x4b, 3, 4, 1, 2]);
    await writeFile(join(tree, 'archives', 'upstream.zip'), wrongZip);
    await assert.rejects(() => buildCorrespondingSource({ ...options, directory: join(tmp, 'bad-archive') }), /archive|inventory/i);
    const wrongRecords = archiveRecords.map(record => record.path === 'archives/upstream.zip' ? { ...record, length: wrongZip.length, sha256: sha(wrongZip) } : record);
    await writeFile(join(backend, 'corresponding-source-inventory.json'), JSON.stringify({ schemaVersion: 1, files: await sourceInventory(tree, null, wrongRecords) }));
    git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'wrong source archive');
    await assert.rejects(() => buildCorrespondingSource({ ...options, directory: join(tmp, 'wrong-archive') }), /archive/i);
    await cp(archive, join(tree, 'archives', 'upstream.zip'));
    await writeFile(join(backend, 'corresponding-source-inventory.json'), JSON.stringify({ schemaVersion: 1, files: await sourceInventory(tree, null, archiveRecords) }));
    git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'restore source inventory');
    await writeFile(join(api, 'upstream', 'codec.js'), 'stale');
    await assert.rejects(() => buildCorrespondingSource({ ...options, directory: join(tmp, 'other') }), /inventory|hash/i);
  } finally { await rm(tmp, { recursive: true, force: true }); }
});
