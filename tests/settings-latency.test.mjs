import assert from 'node:assert/strict';
import test from 'node:test';
import { assessSettingsLatency, assessLoadedSettingsLatency, assertLatencyOwner } from '../scripts/SettingsLatency.mjs';
import { defaultSettings } from '../ui/src/model.ts';

function fixture() {
  return {
    ready: true, finished: true, failure: '', navigationCount: 0, heartbeatCount: 2, maxObserverGapMs: 50,
    calibrationBefore: [{ nativeBeforeMs: 1000, browserMs: 100, nativeAfterMs: 1002, timeOriginMs: 100000 }],
    calibrationAfter: [{ nativeBeforeMs: 3000, browserMs: 2100, nativeAfterMs: 3002, timeOriginMs: 100000 }],
    edits: [{ id: 'font-1', browserMs: 200, timeOriginMs: 100000, expectedSettings: { ...defaultSettings, fontSize: 29 } }],
    deliveries: [{ committedMs: 1149, nativeMs: 1150, settings: { ...defaultSettings, fontSize: 29 }, matchesCommitted: true }],
  };
}
function loadedFixture() {
  const evidence = fixture();
  const proof = (nativeMs, positionMs) => ({
    nativeMs, visible: true, running: true, activeCount: 20, positionMs, pendingWork: false,
    context: { mode: 'automatic', state: 'playing', title: 'fixture', windowTitle: 'fixture player', hwnd: '123',
      mediaRevision: 1, sampleStampMs: nativeMs - 20, source: 'uia-seekbar-ms+smtc' },
  });
  evidence.playbackLoad = { requested: true, loadedCount: 50000, start: proof(1005, 1000), finish: proof(3003, 3000) };
  evidence.deliveries[0].playback = proof(1150, 1150);
  return evidence;
}

test('loaded settings require actual visible moving playback at configuration delivery', () => {
  const result = assessLoadedSettingsLatency(loadedFixture());
  assert.equal(result.passed, true);
  assert.equal(result.playback.loadedCount, 50000);
});
test('idle or unlabeled load cannot stand in for loaded settings evidence', () => {
  for (const change of [
    evidence => { delete evidence.playbackLoad; },
    evidence => { evidence.playbackLoad.loadedCount = 0; },
    evidence => { delete evidence.deliveries[0].playback; },
    evidence => { evidence.deliveries[0].playback.visible = false; },
    evidence => { evidence.deliveries[0].playback.context.state = 'paused'; },
    evidence => { evidence.deliveries[0].playback.context.mode = 'manual'; },
    evidence => { evidence.deliveries[0].playback.activeCount = 0; },
    evidence => { evidence.deliveries[0].playback.context.sampleStampMs = 0; },
    evidence => { evidence.deliveries[0].playback.context.mediaRevision++; },
    evidence => { evidence.playbackLoad.finish.positionMs = evidence.playbackLoad.start.positionMs; },
    evidence => { evidence.playbackLoad.start.pendingWork = true; },
    evidence => { evidence.playbackLoad.finish.pendingWork = true; },
  ]) {
    const evidence = loadedFixture(); change(evidence);
    assert.throws(() => assessLoadedSettingsLatency(evidence));
  }
});
test('rendering load never relaxes the existing 100ms setting deadline', () => {
  const evidence = loadedFixture();
  evidence.deliveries[0].nativeMs = 1199.001;
  evidence.deliveries[0].playback.nativeMs = 1199.001;
  assert.equal(assessLoadedSettingsLatency(evidence).passed, false);
});

test('reports conservative clock-bounded latency after actual delivery', () => {
  const result = assessSettingsLatency(fixture());
  assert.equal(result.passed, true);
  assert.equal(result.samples[0].lowerMs, 47);
  assert.equal(result.samples[0].upperMs, 51);
});

test('selects tight phase brackets, enclosing measured offset movement', () => {
  const evidence = fixture();
  evidence.calibrationBefore.unshift({ nativeBeforeMs: 990, browserMs: 99, nativeAfterMs: 999, timeOriginMs: 100000 });
  evidence.calibrationAfter[0] = { nativeBeforeMs: 3001, browserMs: 2100, nativeAfterMs: 3003, timeOriginMs: 100000 };
  assert.equal(assessSettingsLatency(evidence).samples[0].lowerMs, 46);
});

test('the exact 100ms upper bound passes but 100.001ms fails', () => {
  const evidence = fixture();
  evidence.deliveries[0].nativeMs = 1199;
  assert.equal(assessSettingsLatency(evidence).passed, true);
  evidence.deliveries[0].nativeMs = 1199.001;
  assert.equal(assessSettingsLatency(evidence).passed, false);
});

test('missing and empty evidence cannot pass', () => {
  for (const key of ['edits', 'deliveries', 'calibrationBefore', 'calibrationAfter']) {
    const evidence = fixture(); evidence[key] = [];
    assert.throws(() => assessSettingsLatency(evidence));
  }
});

test('a configuration enqueue or acknowledgement cannot substitute for delivery', () => {
  for (const patch of [{ nativeMs: undefined }, { matchesCommitted: false }, { settings: { fontSize: 28, areaFraction: .5 } }]) {
    const evidence = fixture(); Object.assign(evidence.deliveries[0], patch);
    assert.throws(() => assessSettingsLatency(evidence));
  }
});

test('every delivery must match the complete expected settings, independent of key order', () => {
  const evidence = fixture();
  evidence.deliveries[0].settings = Object.fromEntries(Object.entries({ ...defaultSettings, fontSize: 29 }).reverse());
  assert.equal(assessSettingsLatency(evidence).passed, true);
  evidence.deliveries[0].settings.extra = true;
  assert.throws(() => assessSettingsLatency(evidence));
});

test('duplicates, extra and reordered deliveries are rejected', () => {
  const evidence = fixture();
  evidence.edits.push({ id: 'font-2', browserMs: 300, timeOriginMs: 100000, expectedSettings: { ...defaultSettings, fontSize: 30 } });
  evidence.deliveries.push({ committedMs: 1249, nativeMs: 1250, settings: { ...defaultSettings, fontSize: 30 }, matchesCommitted: true });
  assert.equal(assessSettingsLatency(evidence).passed, true);
  evidence.deliveries.reverse();
  assert.throws(() => assessSettingsLatency(evidence));
  evidence.deliveries.reverse(); evidence.edits[1].id = evidence.edits[0].id;
  assert.throws(() => assessSettingsLatency(evidence));
  evidence.edits.pop();
  assert.throws(() => assessSettingsLatency(evidence));
});

test('non-finite, reversed, discontinuous or overly uncertain clocks are rejected', () => {
  for (const calibration of [
    { nativeBeforeMs: NaN, browserMs: 100, nativeAfterMs: 1002, timeOriginMs: 100000 },
    { nativeBeforeMs: 1003, browserMs: 100, nativeAfterMs: 1002, timeOriginMs: 100000 },
    { nativeBeforeMs: 1000, browserMs: 100, nativeAfterMs: 1006, timeOriginMs: 100000 },
  ]) {
    const evidence = fixture(); evidence.calibrationBefore = [calibration];
    assert.throws(() => assessSettingsLatency(evidence));
  }
  const evidence = fixture(); evidence.calibrationAfter[0].nativeBeforeMs += 20; evidence.calibrationAfter[0].nativeAfterMs += 20;
  assert.throws(() => assessSettingsLatency(evidence));
});

test('events outside the calibrated series and deliveries before input cannot pass', () => {
  for (const patch of [{ browserMs: 50 }, { browserMs: 2200 }]) {
    const evidence = fixture(); Object.assign(evidence.edits[0], patch);
    assert.throws(() => assessSettingsLatency(evidence));
  }
  const evidence = fixture(); evidence.deliveries[0].nativeMs = 1090;
  assert.throws(() => assessSettingsLatency(evidence));
});

test('all observed samples participate; a slow tail cannot be hidden by a percentile', () => {
  const evidence = fixture();
  evidence.edits.push({ id: 'font-2', browserMs: 300, timeOriginMs: 100000, expectedSettings: { ...defaultSettings, fontSize: 30 } });
  evidence.deliveries.push({ committedMs: 1400, nativeMs: 1401, settings: { ...defaultSettings, fontSize: 30 }, matchesCommitted: true });
  const result = assessSettingsLatency(evidence);
  assert.equal(result.passed, false); assert.equal(result.maxUpperMs, 202);
});

test('a wider calibration proving an inconsistent offset cannot be discarded', () => {
  const evidence = fixture();
  evidence.calibrationBefore.push({ nativeBeforeMs: 1040, browserMs: 110, nativeAfterMs: 1043, timeOriginMs: 100000 });
  assert.throws(() => assessSettingsLatency(evidence));
});

test('both sides must contain exactly the current fourteen valid settings fields', () => {
  for (const settings of [{ fontSize: 29 }, { ...defaultSettings, fontSize: 999 }, { ...defaultSettings, enabled: 'true' }]) {
    const evidence = fixture(); evidence.edits[0].expectedSettings = settings; evidence.deliveries[0].settings = settings;
    assert.throws(() => assessSettingsLatency(evidence));
  }
});

test('origin changes, observed navigation and observation gaps invalidate the series', () => {
  for (const change of [
    e => { e.calibrationAfter[0].timeOriginMs++; },
    e => { e.edits[0].timeOriginMs++; },
    e => { e.navigationCount = 1; },
    e => { e.maxObserverGapMs = 501; },
    e => { e.maxObserverGapMs = 0; },
    e => { e.heartbeatCount = 1; },
    e => { delete e.maxObserverGapMs; },
    e => { e.finished = false; },
    e => { e.failure = 'observer failure'; },
  ]) {
    const evidence = fixture(); change(evidence);
    assert.throws(() => assessSettingsLatency(evidence));
  }
});

test('all calibration timestamps are ordered and bracket the entire edit series', () => {
  const evidence = fixture();
  evidence.calibrationBefore.push({ nativeBeforeMs: 1050, browserMs: 150, nativeAfterMs: 1052, timeOriginMs: 100000 });
  evidence.edits[0].browserMs = 140;
  assert.throws(() => assessSettingsLatency(evidence));
  evidence.edits[0].browserMs = 200;
  evidence.calibrationBefore.reverse();
  assert.throws(() => assessSettingsLatency(evidence));
});

test('the CDP page must prove this run ownership before inputs', () => {
  const nonce = 'b'.repeat(32), observed = { url: 'https://danmu.local/index.html', nonce };
  assert.equal(assertLatencyOwner(nonce, observed), true);
  for (const candidate of [{ ...observed, nonce: 'a'.repeat(32) }, { ...observed, nonce: undefined }, { ...observed, url: 'https://danmu.local/other.html' }]) {
    assert.throws(() => assertLatencyOwner(nonce, candidate));
  }
  assert.throws(() => assertLatencyOwner(undefined, { url: observed.url }));
});

test('commit evidence must be finite, after input and no later than actual delivery', () => {
  for (const committedMs of [undefined, NaN, 1151, 1090]) {
    const evidence = fixture(); evidence.deliveries[0].committedMs = committedMs;
    assert.throws(() => assessSettingsLatency(evidence));
  }
  const evidence = fixture();
  evidence.edits[0].browserMs = 100.1;
  evidence.deliveries[0].committedMs = 1000.9; evidence.deliveries[0].nativeMs = 1001;
  assert.throws(() => assessSettingsLatency(evidence));
});
