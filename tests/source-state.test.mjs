import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { sourceState, requireSameSource } from '../scripts/source-state.mjs';

test('release source guard detects edited bytes even with the same dirty status and ignores build outputs', async () => {
  const root = await mkdtemp(join(tmpdir(), 'DanmuSourceGuard-'));
  function git(args) { const result = spawnSync('git', args, { cwd: root, windowsHide: true }); assert.equal(result.status, 0); }
  try {
    git(['init', '-q']);
    await writeFile(join(root, '.gitignore'), 'output.txt\n');
    await writeFile(join(root, 'source.txt'), 'original');
    git(['add', '.']);git(['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'fixture']);
    const clean = await sourceState(root);
    await writeFile(join(root, 'output.txt'), 'ignored build result');
    requireSameSource(clean, await sourceState(root));
    await writeFile(join(root, 'source.txt'), 'edit one');
    const first = await sourceState(root);
    assert.throws(() => requireSameSource(clean, first), /changed/);
    await writeFile(join(root, 'source.txt'), 'edit two');
    const second = await sourceState(root);
    assert.equal(first.status, second.status);
    assert.throws(() => requireSameSource(first, second), /changed/);
    git(['add', '.']);git(['-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-qm', 'new fixture head']);
    assert.throws(() => requireSameSource(clean, { ...clean, head: 'other' }), /changed/);
  } finally { await rm(root, { recursive: true, force: true }); }
});
