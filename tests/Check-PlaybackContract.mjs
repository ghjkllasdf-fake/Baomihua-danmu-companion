import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { playbackStatus, validPlayer } from '../ui/src/model.ts';

// Node 26 strips erasable TypeScript types here; the UI's tsc build remains a separate gate.
// https://nodejs.org/api/typescript.html#type-stripping
assert(Number(process.versions.node.split('.')[0]) >= 26, 'The development contract check requires Node 26 or newer.');
assert(process.argv[2], 'A freshly generated native snapshot fixture is required.');
const snapshots = JSON.parse(await readFile(process.argv[2], 'utf8'));
assert(Array.isArray(snapshots) && snapshots.length === 9, 'The native fixture must cover all nine clock/metadata scenarios.');
for (const [index, snapshot] of snapshots.entries()) {
  assert(validPlayer(snapshot), `Current native snapshot ${index} violates the current UI boundary (${snapshot.syncMode}/${snapshot.state}).`);
}
assert.deepEqual(snapshots.map(p => p.state), ['playing', 'paused', 'unknown', 'playing', 'unavailable', 'unknown', 'unknown', 'unknown', 'paused']);
assert.equal(snapshots[0].positionMs, 10200);
assert.equal(snapshots[0].videoBoundsPx.x, -1920);
assert.equal(playbackStatus(snapshots[2]), '\u540c\u6b65\u4fe1\u53f7\u5df2\u4e2d\u65ad');
assert.equal(snapshots[4].positionMs, null);
assert.equal(snapshots[6].title, 'B');
assert.equal(snapshots[6].positionMs, null);
assert.equal(snapshots[7].syncMode, 'unavailable');
assert.equal(snapshots[7].positionMs, null);
assert.equal(snapshots[8].positionMs, 5000);
console.log('PASS: freshly serialized native snapshots satisfy the actual UI validator and status labels (no browser).');
