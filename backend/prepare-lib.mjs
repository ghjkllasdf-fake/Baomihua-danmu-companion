import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readdir, lstat } from 'node:fs/promises';
import { join } from 'node:path';

export async function hashFile(path) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
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
