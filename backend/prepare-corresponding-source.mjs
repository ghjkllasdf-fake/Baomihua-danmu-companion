import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { copyFile, cp, lstat, mkdir, mkdtemp, readFile, readdir, rename, rm, stat, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import { applyExactPatch, hashFile, lockedSourceFiles } from './prepare-lib.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const base = join(root, '.deps', 'includeapi');
const cache = join(base, 'source-downloads');
const destination = join(base, 'corresponding-source');
const inventoryPath = join(root, 'backend', 'corresponding-source-inventory.json');
const args = process.argv.slice(2);
if (args.length > 1 || (args.length && args[0] !== '--update-inventory')) throw new Error('Usage: node backend/prepare-corresponding-source.mjs [--update-inventory]');
const update = args[0] === '--update-inventory';
if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('Prepare on Windows x64.');
const toolchain = JSON.parse(await readFile(join(root, 'backend', 'toolchain.json'), 'utf8'));
if (toolchain.schemaVersion !== 1 || await hashFile(join(root, 'backend', 'dependency-sources.json')) !== toolchain.dependencySourcesSha256 ||
    await hashFile(join(root, 'backend', 'package-lock.json')) !== toolchain.productionLock.sha256) throw new Error('Source lock hash mismatch.');
const manifest = JSON.parse(await readFile(join(root, 'backend', 'dependency-sources.json'), 'utf8'));
const lock = JSON.parse(await readFile(join(root, 'backend', 'package-lock.json'), 'utf8'));
const files = lockedSourceFiles(lock, manifest);
files.push({ file: 'node-v22.23.4.tar.xz', url: toolchain.node.sourceUrl, sha256: toolchain.node.sourceSha256 });
files.push({ file: 'upstream.zip', url: toolchain.upstream.archiveUrl, sha256: toolchain.upstream.archiveSha256 });
const patchPath = join(root, 'backend', 'patches', '0001-node-brotli.json');
if (await hashFile(patchPath) !== toolchain.patches['0001-node-brotli.json']) throw new Error('Patch hash mismatch.');
const patch = JSON.parse(await readFile(patchPath, 'utf8'));
if (patch.sourceCommit !== toolchain.upstream.commit || patch.path !== 'danmu_api/utils/codec-util.js') throw new Error('Patch target mismatch.');
await mkdir(cache, { recursive: true });

async function sri(path) {
  const hash = createHash('sha512');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return 'sha512-' + hash.digest('base64');
}
async function download(item) {
  const path = join(cache, item.file);
  try { await stat(path); }
  catch (error) {
    if (error.code !== 'ENOENT') throw error;
    const temp = path + '.' + process.pid + '.tmp';
    try {
      const bundled = join(root, '..', 'third-party', 'archives', item.file);
      let available = false;
      try { available = (await stat(bundled)).isFile(); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      if (available) await copyFile(bundled, temp);
      else {
        const response = await fetch(item.url, { signal: AbortSignal.timeout(300000) });
        if (!response.ok || !response.body) throw new Error(`Download failed (${response.status}): ${item.file}`);
        let size = 0;
        async function* bounded(source) {
          for await (const chunk of source) {
            size += chunk.length;
            if (size > 200 * 1024 * 1024) throw new Error(`Source download too large: ${item.file}`);
            yield chunk;
          }
        }
        await pipeline(bounded(Readable.fromWeb(response.body)), createWriteStream(temp, { flags: 'wx' }));
      }
      if (await hashFile(temp) !== item.sha256 || (item.integrity && await sri(temp) !== item.integrity)) throw new Error(`Source download integrity mismatch: ${item.file}`);
      await rename(temp, path);
    } finally { await rm(temp, { force: true }); }
  }
  if (await hashFile(path) !== item.sha256 || (item.integrity && await sri(path) !== item.integrity)) throw new Error(`Cached source integrity mismatch: ${item.file}`);
  return path;
}
let next = 0;
await Promise.all(Array.from({ length: 6 }, async () => {
  for (;;) { const index = next++; if (index >= files.length) return; await download(files[index]); }
}));
const temporary = await mkdtemp(join(base, 'source-preparation-'));
try {
  const tree = join(temporary, 'tree'), archives = join(tree, 'archives'), extraction = join(temporary, 'extract');
  await mkdir(archives, { recursive: true }); await mkdir(extraction);
  for (const item of files) await copyFile(join(cache, item.file), join(archives, item.file));
  const archive = join(cache, 'upstream.zip');
  const listing = spawnSync('tar.exe', ['-tf', archive], { encoding: 'utf8', windowsHide: true });
  const prefix = `danmu_api-${toolchain.upstream.commit}`;
  if (listing.error || listing.status !== 0 || !listing.stdout.split(/\r?\n/).filter(Boolean).every(name => name === prefix + '/' || name.startsWith(prefix + '/'))) throw new Error('Invalid upstream archive layout.');
  const unpack = spawnSync('tar.exe', ['-xf', archive, '-C', extraction], { stdio: 'inherit', windowsHide: true });
  if (unpack.error || unpack.status !== 0) throw new Error('Cannot extract upstream source.');
  const original = join(tree, 'upstream', 'original'), modified = join(tree, 'upstream', 'modified');
  await cp(join(extraction, prefix), original, { recursive: true });
  await cp(original, modified, { recursive: true });
  const codec = join(modified, 'danmu_api', 'utils', 'codec-util.js');
  await writeFile(codec, applyExactPatch(await readFile(codec, 'utf8'), patch));
  await copyFile(join(root, 'backend', 'CORRESPONDING-SOURCE.md'), join(tree, 'README.md'));
  await mkdir(join(tree, 'dependencies', 'node'), { recursive: true });
  await mkdir(join(tree, 'dependencies', 'dan-any'), { recursive: true });
  await copyFile(join(root, 'backend', 'dependency-sources.json'), join(tree, 'dependencies', 'SOURCES.json'));
  await writeFile(join(tree, 'dependencies', 'node', 'README.md'), 'Node.js 22.23.4 source: ../../archives/node-v22.23.4.tar.xz. Official SHA-256: ' + toolchain.node.sourceSha256 + '\n');
  const danAny = manifest.packages.find(item => item.name === '@dan-uni/dan-any');
  if (!danAny) throw new Error('Missing LGPL library source.');
  await writeFile(join(tree, 'dependencies', 'dan-any', 'README.md'), 'LGPL-3.0-or-later preferred source: ../../archives/' + danAny.sourceFile + '\nOriginal npm archive: ../../archives/' + danAny.tarballFile + '\nRuntime metadata patch: project/backend/patches/0002-dan-any-node-profile.json\n');
  const records = [];
  async function visit(dir, prefix = '') {
    for (const name of (await readdir(dir)).sort()) {
      const path = join(dir, name), relative = prefix + name, info = await lstat(path);
      if (info.isSymbolicLink()) throw new Error('Source symlink not supported');
      if (info.isDirectory()) await visit(path, relative + '/');
      else if (info.isFile()) records.push({ path: relative, length: info.size, sha256: await hashFile(path) });
      else throw new Error('Unsupported source file');
    }
  }
  await visit(tree); records.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  const inventory = { schemaVersion: 1, files: records };
  if (!update) {
    const expected = JSON.parse(await readFile(inventoryPath, 'utf8'));
    if (JSON.stringify(inventory) !== JSON.stringify(expected)) throw new Error('Corresponding source does not match tracked inventory.');
  }
  try { await stat(destination); throw new Error('Corresponding source directory already exists; move it before preparing another.'); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  await rename(tree, destination);
  if (update) await writeFile(inventoryPath, JSON.stringify(inventory, null, 2) + '\n');
  console.log(JSON.stringify({ directory: destination, inventory: inventoryPath, files: records.length, bytes: records.reduce((n, file) => n + file.length, 0) }));
} finally { await rm(temporary, { recursive: true, force: true }); }
