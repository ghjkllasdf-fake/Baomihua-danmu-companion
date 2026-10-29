import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { applyExactPatch } from '../prepare-lib.mjs';

const hash = text => createHash('sha256').update(text).digest('hex');
test('patch only applies to the exact pinned original and verifies modified bytes', () => {
  const original = 'import old;\noutput = old(input);\n';
  const modified = 'import native;\noutput = native(input);\n';
  const patch = { originalSha256: hash(original), modifiedSha256: hash(modified),
    replacements: [{ from: 'import old;', to: 'import native;' }, { from: 'old(input)', to: 'native(input)' }] };
  assert.equal(applyExactPatch(original, patch), modified);
  assert.throws(() => applyExactPatch(original + 'changed', patch), /original/i);
  assert.throws(() => applyExactPatch(original, { ...patch, modifiedSha256: hash('wrong') }), /modified/i);
  assert.throws(() => applyExactPatch(original, { ...patch, replacements: [{ from: 'missing', to: 'x' }] }), /replacement/i);
});
