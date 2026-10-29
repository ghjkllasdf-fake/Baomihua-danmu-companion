import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { join } from 'node:path';

export async function sourceState(root) {
  function git(args) {
    const result = spawnSync('git', args, { cwd: root, encoding: 'utf8', windowsHide: true, maxBuffer: 8 * 1024 * 1024 });
    if (result.error || result.status !== 0) throw new Error('Cannot establish release source identity.');
    return result.stdout;
  }
  const head = git(['rev-parse', 'HEAD']).trim();
  const status = git(['status', '--porcelain', '--untracked-files=all']);
  const files = [...new Set(git(['ls-files', '-c', '-o', '--exclude-standard', '-z']).split('\0').filter(Boolean))].sort();
  const hash = createHash('sha256');
  for (const file of files) {
    hash.update(file + '\0');
    try { hash.update(await readFile(join(root, file))); }
    catch (error) { if (error.code !== 'ENOENT') throw error; hash.update('<deleted>'); }
    hash.update('\0');
  }
  return { head, status, digest: hash.digest('hex') };
}

export function requireSameSource(before, after) {
  if (JSON.stringify(before) !== JSON.stringify(after)) throw new Error('Release source changed during the build; artifacts must not be published.');
}
