import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readdir, lstat } from 'node:fs/promises';
import { join } from 'node:path';

export async function hashFile(path) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
}

export function applyExactPatch(original, patch) {
  const hash = text => createHash('sha256').update(text, 'utf8').digest('hex');
  if (hash(original) !== patch.originalSha256) throw new Error('Patch original SHA-256 mismatch');
  let modified = original;
  for (const { from, to } of patch.replacements) {
    const position = modified.indexOf(from);
    if (!from || position < 0 || modified.indexOf(from, position + from.length) !== -1) throw new Error('Patch replacement is not unique');
    modified = modified.slice(0, position) + to + modified.slice(position + from.length);
  }
  if (hash(modified) !== patch.modifiedSha256) throw new Error('Patch modified SHA-256 mismatch');
  return modified;
}

export function lockedSourceFiles(lock, manifest) {
  if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.packages)) throw new Error('Invalid source manifest');
  const expected = Object.keys(lock.packages).filter(path => path.startsWith('node_modules/'));
  if (expected.length !== manifest.packages.length) throw new Error('Source coverage mismatch');
  const seen = new Set(), files = new Map();
  for (const item of manifest.packages) {
    const key = `node_modules/${item.name}`, packageLock = lock.packages[key];
    if (!packageLock || seen.has(key) || packageLock.version !== item.version || packageLock.integrity !== item.integrity) throw new Error('Source package version or lock mismatch');
    seen.add(key);
    if (!/^[a-f0-9]{40}$/.test(item.sourceCommit)) throw new Error('Invalid source commit');
    for (const [file, url, hash, host, extension, integrity] of [
      [item.sourceFile, item.sourceUrl, item.sourceSha256, 'codeload.github.com', '.zip', null],
      [item.tarballFile, item.tarballUrl, item.tarballSha256, 'registry.npmjs.org', '.tgz', item.integrity]
    ]) {
      if (typeof file !== 'string' || !/^[A-Za-z0-9._-]+$/.test(file) || !file.endsWith(extension) ||
          !/^[a-f0-9]{64}$/.test(hash) || typeof url !== 'string' || !url.startsWith(`https://${host}/`)) throw new Error('Invalid source archive');
      const current = files.get(file);
      if (current && (current.sha256 !== hash || current.url !== url || current.integrity !== integrity)) throw new Error('Conflicting source archive');
      files.set(file, { file, url, sha256: hash, integrity });
    }
  }
  return [...files.values()].sort((a, b) => a.file.localeCompare(b.file));
}

export function prepareDanAnyManifest(replacement, reference) {
  if (replacement.name !== '@dan-uni/dan-any' || replacement.version !== reference.version) throw new Error('Replacement package name or version mismatch');
  if (replacement.license !== 'LGPL-3.0-or-later') throw new Error('Replacement library license mismatch');
  const dependencies = { ...replacement.dependencies };
  for (const name of ['@electric-sql/pglite', '@electric-sql/pglite-tools', 'drizzle-orm']) delete dependencies[name];
  const sorted = value => JSON.stringify(Object.entries(value ?? {}).sort(([a], [b]) => a.localeCompare(b)));
  if (sorted(dependencies) !== sorted(reference.dependencies) || sorted(replacement.optionalDependencies) !== sorted(reference.optionalDependencies) ||
      sorted(replacement.peerDependencies) !== sorted(reference.peerDependencies)) throw new Error('Replacement dependencies differ from the locked runtime graph');
  return { ...replacement, dependencies };
}

export async function inventoryPayload(root) {
  const files = [];
  async function visit(dir, relative) {
    for (const name of (await readdir(dir)).sort()) {
      const path = join(dir, name);
      const rel = relative ? `${relative}/${name}` : name;
      const stat = await lstat(path);
      if (stat.isSymbolicLink() || (!stat.isDirectory() && !stat.isFile())) throw new Error(`Unsupported payload entry: ${rel}`);
      if (/\.(?:ps1|psm1|psd1|bat|cmd)$/i.test(name) || name.toLowerCase() === '.bin') throw new Error(`Forbidden payload entry: ${rel}`);
      if (stat.isDirectory()) await visit(path, rel);
      else files.push({ path: rel, length: stat.size, sha256: await hashFile(path) });
    }
  }
  await visit(root, '');
  files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
  return { schemaVersion: 1, totalBytes: files.reduce((total, file) => total + file.length, 0), fileCount: files.length, files };
}
