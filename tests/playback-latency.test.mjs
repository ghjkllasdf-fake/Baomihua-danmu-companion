import test from 'node:test';
import assert from 'node:assert/strict';
import { assessPlaybackLatency } from '../scripts/PlaybackLatency.mjs';

function fixture(pauseAt = 1300) {
  const sample = (sequence, nativeMs, state, x = 500) => ({
    type: 'sample', sequence, committedMs: nativeMs - 1, nativeMs,
    title: 'fixture episode', windowTitle: 'fixture player', hwnd: '123',
    mediaRevision: 1, seekRevision: 0, mode: 'automatic', state,
    sampleStampMs: nativeMs - 25, source: 'uia-seekbar-ms+smtc',
    visible: true, running: state === 'playing', activeCount: 1,
    positionMs: state === 'paused' ? 1000 : nativeMs,
    text: ['Playback response TEST'], x: [x],
  });
  const rows = [sample(1, 1000, 'playing')];
  rows.push({ type: 'arm', id: 'pause1', expectedState: 'paused', nativeMs: 1050, baselineSequence: 1 });
  rows.push(sample(2, pauseAt, 'paused'));
  rows.push({ type: 'input-completed', id: 'pause1', nativeMs: 1400, action: 'space', windowTitle: 'fixture player' });
  rows.push(sample(3, 2000, 'paused'), sample(4, 7000, 'paused'), sample(5, 14200, 'paused'));
  rows.push({ type: 'arm', id: 'resume1', expectedState: 'playing', nativeMs: 14250, baselineSequence: 5 });
  rows.push(sample(6, 14500, 'playing'));
  rows.push({ type: 'input-completed', id: 'resume1', nativeMs: 14600, action: 'space', windowTitle: 'fixture player' });
  rows.push(sample(7, 15000, 'playing', 450));
  // The actual observer emits samples every desktop timer tick.
  const expanded = [];
  let sequence = 0;
  for (const row of rows) {
    const prior = expanded.filter(r => r.type === 'sample').at(-1);
    if (row.type === 'sample' && prior) {
      for (let at = prior.nativeMs + 200; at < row.nativeMs; at += 200) {
        expanded.push(sample(++sequence, at, prior.state, prior.x[0]));
      }
    }
    if (row.type === 'sample') row.sequence = ++sequence;
    if (row.type === 'arm') row.baselineSequence = expanded.filter(r => r.type === 'sample').at(-1).sequence;
    expanded.push(row);
  }
  // Expansion only interpolates sample state; rows are sorted by the actual writer order.
  expanded.sort((a, b) => a.nativeMs - b.nativeMs);
  return expanded;
}

test('proves pause/resume upper bounds, visible zero drift and resumed movement', () => {
  const result = assessPlaybackLatency(fixture());
  assert.equal(result.actions, 2);
  assert.equal(result.maxUpperMs, 250);
  assert.ok(result.pausedFrozenMs >= 10000);
});
test('proves real play-pause button clicks with the same timing and motion requirements', () => {
  const rows = fixture();
  for (const row of rows.filter(r => r.type === 'input-completed')) row.action = 'play-pause-button';
  const result = assessPlaybackLatency(rows);
  assert.equal(result.maxUpperMs, 250);
  assert.ok(result.pausedFrozenMs >= 10000);
  assert.ok(result.results.every(r => r.action === 'play-pause-button'));
});
test('rejects unsupported input actions', () => {
  const rows = fixture();
  rows.find(r => r.type === 'input-completed').action = 'unverified-action';
  assert.throws(() => assessPlaybackLatency(rows), /matching real input/);
});
test('button clicks retain the 500ms delivery limit', () => {
  const rows = fixture(1700);
  for (const row of rows.filter(r => r.type === 'input-completed')) row.action = 'play-pause-button';
  assert.throws(() => assessPlaybackLatency(rows), /500ms/);
});

const mutations = {
  'missing action input': rows => rows.filter(r => r.type !== 'input-completed'),
  'missing action delivery': rows => rows.filter(r => !(r.type === 'sample' && r.state === 'paused')),
  'stale baseline': rows => {
    Object.assign(rows[0], { nativeMs: 625, committedMs: 624, sampleStampMs: 600 });
    Object.assign(rows.find(r => r.type === 'sample' && r.sequence === 2),
      { nativeMs: 1100, committedMs: 1099, sampleStampMs: 1075 });
    return rows;
  },
  'wrong baseline state': rows => { rows[0].state = 'paused'; rows[0].running = false; return rows; },
  'changed media': rows => { rows.find(r => r.type === 'sample' && r.state === 'paused').mediaRevision++; return rows; },
  'changed window': rows => { rows.find(r => r.type === 'sample' && r.state === 'paused').hwnd = '999'; return rows; },
  'changed seek revision': rows => { rows.find(r => r.type === 'sample' && r.state === 'paused').seekRevision++; return rows; },
  'wrong input window': rows => { rows.find(r => r.type === 'input-completed').windowTitle = 'another player'; return rows; },
  'observation gap': rows => rows.filter(r => !(r.type === 'sample' && r.nativeMs > 2200 && r.nativeMs < 3000)),
  'duplicate input confirmation': rows => [...rows, { ...rows.find(r => r.type === 'input-completed'), nativeMs: rows.at(-1).nativeMs }],
  'stale proof': rows => { rows.find(r => r.type === 'sample' && r.state === 'paused').sampleStampMs -= 2000; return rows; },
  'future proof': rows => { rows[0].sampleStampMs = 1001; return rows; },
  'manual mode': rows => { rows.find(r => r.type === 'sample' && r.state === 'paused').mode = 'manual'; return rows; },
  'hidden paused overlay': rows => { rows.find(r => r.type === 'sample' && r.state === 'paused').visible = false; return rows; },
  'no visible comments': rows => { rows[0].activeCount = 0; rows[0].text = []; rows[0].x = []; return rows; },
  'renderer never freezes': rows => { rows.find(r => r.type === 'sample' && r.state === 'paused').running = true; return rows; },
  'paused drift': rows => { rows.find(r => r.type === 'sample' && r.nativeMs === 7000).x[0]++; return rows; },
  'no resumed movement': rows => { for (const r of rows.filter(r => r.type === 'sample')) r.x = [500]; return rows; },
  'duplicate sequence': rows => { rows.filter(r => r.type === 'sample')[1].sequence = 1; return rows; },
  'unordered delivery': rows => { rows[0].committedMs = 1001; return rows; },
  'missing identity': rows => { rows[0].title = ''; return rows; },
  'nonfinite timestamp': rows => { rows[0].nativeMs = NaN; return rows; },
};
for (const [name, mutate] of Object.entries(mutations)) {
  test(`rejects ${name}`, () => assert.throws(() => assessPlaybackLatency(mutate(fixture()))));
}
test('rejects a fresh, ordered and otherwise valid delivery above 500ms', () => {
  assert.throws(() => assessPlaybackLatency(fixture(1700)), /500ms/);
});
