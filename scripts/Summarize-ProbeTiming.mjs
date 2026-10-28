import { readFile } from 'node:fs/promises';

function summarize(rows) {
  const polls = rows.filter(row => row.type === 'poll');
  if (!polls.length || rows[0]?.type !== 'started' || rows.at(-1)?.type !== 'closed'
    || rows.some(row => row.type === 'failure')) throw new Error('Incomplete timing observations');
  const result = { polls: polls.length, discoveryPolls: polls.filter(row => row.discoveries > 0).length };
  for (const key of ['elapsedMs', 'discoverMs', 'readMs', 'otherMs']) {
    const values = polls.map(row => row[key]).sort((a, b) => a - b);
    if (values.some(value => !Number.isFinite(value) || value < 0)) throw new Error('Invalid timing value');
    result[key] = {
      mean: values.reduce((sum, value) => sum + value, 0) / values.length,
      p95: values[Math.ceil(values.length * 0.95) - 1],
      max: values.at(-1),
    };
  }
  return result;
}

if (process.argv.length < 3) throw new Error('Provide one or more probe timing JSONL files');
const results = [];
for (const path of process.argv.slice(2)) {
  const sources = JSON.parse(await readFile(`${path}.sources.json`, 'utf8'));
  if (sources.forcedStop || sources.exitCode !== 0 || sources.sourceStable !== true) throw new Error('Unconfirmed timing run');
  const rows = (await readFile(path, 'utf8')).trim().split(/\r?\n/).map(line => JSON.parse(line));
  results.push({ path, scope: 'Standalone sampler timing, not supervised-worker or presentation acceptance', ...summarize(rows) });
}
console.log(JSON.stringify(results, null, 2));
