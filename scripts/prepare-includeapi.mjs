import { createWriteStream } from 'node:fs';
import { cp, mkdir, mkdtemp, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { hashFile, inventoryPayload } from '../backend/prepare-lib.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const base = join(root, '.deps', 'includeapi');
const downloads = join(base, 'downloads');
const destination = join(base, 'payload');
const toolchain = JSON.parse(await readFile(join(root, 'backend', 'toolchain.json'), 'utf8'));
const args = process.argv.slice(2);
if (args.length > 1 || (args.length === 1 && args[0] !== '--verify')) throw new Error('Usage: node scripts/prepare-includeapi.mjs [--verify]');
const verifyOnly = args[0] === '--verify';
if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('Prepare on Windows x64 with Node and npm available.');
if (toolchain.schemaVersion !== 1) throw new Error('Unsupported toolchain.');

async function verify(path, expected) {
  const actual = await hashFile(path);
  if (actual !== expected) throw new Error(`SHA-256 mismatch: ${path}`);
}
async function download(name, url, expected, limit) {
  const target = join(downloads, name);
  try { await stat(target); await verify(target, expected); return target; }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  const temporary = `${target}.${process.pid}.tmp`;
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(300000) });
    if (!response.ok || !response.body) throw new Error(`Download failed (${response.status}): ${url}`);
    let received = 0;
    async function* bounded(source) {
      for await (const chunk of source) {
        received += chunk.length;
        if (received > limit) throw new Error(`Download size limit: ${name}`);
        yield chunk;
      }
    }
    await pipeline(bounded(Readable.fromWeb(response.body)), createWriteStream(temporary, { flags: 'wx' }));
    await verify(temporary, expected);
    await rename(temporary, target);
    return target;
  } finally { await rm(temporary, { force: true }); }
}
function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} failed with status ${result.status}`);
}
await mkdir(downloads, { recursive: true });
await verify(join(root, 'backend', 'package.json'), toolchain.packageManifestSha256);
await verify(join(root, 'backend', 'package-lock.json'), toolchain.productionLock.sha256);
const [archive, node, nodeLicense] = await Promise.all([
  download('upstream.zip', toolchain.upstream.archiveUrl, toolchain.upstream.archiveSha256, 20 * 1024 * 1024),
  download('node.exe', toolchain.node.url, toolchain.node.sha256, 200 * 1024 * 1024),
  download('node-LICENSE', toolchain.node.licenseUrl, toolchain.node.licenseSha256, 2 * 1024 * 1024)
]);
const temp = await mkdtemp(join(base, 'prepare-'));
try {
  const extracted = join(temp, 'source');
  const payload = join(temp, 'payload');
  const api = join(payload, 'api');
  await mkdir(extracted); await mkdir(api, { recursive: true });
  run('tar.exe', ['-xf', archive, '-C', extracted], root);
  const upstream = join(extracted, `danmu_api-${toolchain.upstream.commit}`);
  await verify(join(upstream, 'package.json'), toolchain.packageManifestSha256);
  await cp(upstream, join(api, 'upstream'), { recursive: true, errorOnExist: true });
  await cp(join(root, 'backend', 'package.json'), join(api, 'package.json'));
  await cp(join(root, 'backend', 'package-lock.json'), join(api, 'package-lock.json'));
  await cp(join(root, 'backend', 'entry.mjs'), join(api, 'entry.mjs'));
  await cp(node, join(api, 'node.exe'));
  await mkdir(join(api, 'licenses'));
  await cp(nodeLicense, join(api, 'licenses', 'Node-LICENSE.txt'));
  await cp(join(root, 'backend', 'licenses'), join(api, 'licenses'), { recursive: true });
  await cp(join(root, 'backend', 'NOTICES.md'), join(api, 'THIRD-PARTY-NOTICES.md'));
  await cp(join(root, 'backend', 'toolchain.json'), join(api, 'toolchain.json'));
  await writeFile(join(api, 'runtime.json'), JSON.stringify({ schemaVersion: 1, upstreamCommit: toolchain.upstream.commit, nodeVersion: toolchain.node.version }, null, 2) + '\n');
  run(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', 'npm.cmd ci --omit=dev --ignore-scripts --no-bin-links --no-audit --no-fund --prefer-offline'], api);
  const inventory = await inventoryPayload(payload);
  if (verifyOnly) {
    const expected = JSON.parse(await readFile(join(base, 'payload-inventory.json'), 'utf8'));
    if (JSON.stringify(inventory) !== JSON.stringify(expected)) throw new Error('Fresh preparation does not match the recorded inventory.');
    const installed = await inventoryPayload(destination);
    if (JSON.stringify(installed) !== JSON.stringify(expected)) throw new Error('Existing payload does not match the recorded inventory.');
  } else {
    const old = join(base, 'payload.previous');
    try { await stat(old); throw new Error(`Remove or relocate old preparation manually: ${old}`); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    let previous = false;
    try { await rename(destination, old); previous = true; } catch (error) { if (error.code !== 'ENOENT') throw error; }
    try { await rename(payload, destination); } catch (error) { if (previous) await rename(old, destination); throw error; }
    if (previous) await rm(old, { recursive: true });
    await writeFile(join(base, 'payload-inventory.json'), JSON.stringify(inventory, null, 2) + '\n');
  }
  console.log(JSON.stringify({ payload: destination, inventory: join(base, 'payload-inventory.json'), fileCount: inventory.fileCount, totalBytes: inventory.totalBytes }));
} finally { await rm(temp, { recursive: true, force: true }); }
