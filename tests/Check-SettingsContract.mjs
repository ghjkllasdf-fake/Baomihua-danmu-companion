import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { defaultSettings, validHello, validSettingsSnapshot } from '../ui/src/model.ts';

assert(Number(process.versions.node.split('.')[0]) >= 26, 'The development contract check requires Node 26 or newer.');
assert(process.argv[2], 'A freshly generated native settings fixture is required.');
const snapshots = JSON.parse(await readFile(process.argv[2], 'utf8'));
assert(Array.isArray(snapshots) && snapshots.length === 3);
const [initial, ack, read] = snapshots;
assert(validHello(initial), 'The native handshake must satisfy the actual UI capability and complete hello validator.');
assert.equal(ack.type, 'response');
assert.equal(ack.ok, true);
assert.deepEqual(Object.keys(ack.data).sort(), Object.keys(defaultSettings).sort(), 'The v1 save response must retain bare Settings data for existing clients.');
const normalized = [initial, { settings: ack.data, settingsRevision: ack.settingsRevision }, read];
for (const snapshot of normalized) {
  assert(validSettingsSnapshot(snapshot), 'The current host settings response violates the actual UI snapshot validator.');
  assert.deepEqual(Object.keys(snapshot.settings).sort(), Object.keys(defaultSettings).sort(), 'Native settings must contain only the expected style fields.');
}
assert.deepEqual(normalized.map(s => s.settingsRevision), [0, 1, 1]);
assert.deepEqual(normalized.map(s => s.settings.fontSize), [28, 40, 40]);
console.log('PASS: actual hello/save/read settings snapshots satisfy the current TypeScript validator without a desktop window.');
