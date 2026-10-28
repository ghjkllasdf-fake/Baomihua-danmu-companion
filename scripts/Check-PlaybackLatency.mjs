import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { assessPlaybackLatency } from './PlaybackLatency.mjs';

const directory = process.argv[2];
if (!directory) throw new Error('Provide the owned playback-observer run directory.');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const rows = (await readFile(path.join(directory, 'observations.jsonl'), 'utf8'))
  .trim().split(/\r?\n/).map(line => JSON.parse(line));
if (rows[0]?.type !== 'started' || rows.at(-1)?.type !== 'closed' || rows.at(-1).normal !== true) {
  throw new Error('The observer did not close normally.');
}
const manifest = JSON.parse(await readFile(path.join(directory, 'sources.json'), 'utf8'));
if (!manifest.closedNormally || !manifest.sourceStable || !Array.isArray(manifest.before)
  || manifest.before.length === 0 || JSON.stringify(manifest.before) !== JSON.stringify(manifest.after)) {
  throw new Error('Missing stable source endpoints or normal owned-host closure.');
}
for (const required of ['desktop/native/Desktop.cs', 'desktop/native/Overlay.cs', 'desktop/native/ProbeProcess.cs',
  'tests/fixtures/PlaybackLatencyObserver.cs', 'scripts/PlaybackLatency.mjs', 'scripts/Check-PlaybackLatency.mjs']) {
  if (!manifest.before.some(entry => entry.path.replaceAll('\\', '/') === required)) {
    throw new Error(`Missing measurement source: ${required}`);
  }
}
for (const entry of manifest.before) {
  const file = path.resolve(root, entry.path);
  if (!file.startsWith(root + path.sep)) throw new Error('Source path escaped the workspace.');
  const hash = createHash('sha256').update(await readFile(file)).digest('hex');
  if (hash !== entry.sha256.toLowerCase()) throw new Error(`Source changed after measurement: ${entry.path}`);
}
console.log(JSON.stringify(assessPlaybackLatency(rows), null, 2));
