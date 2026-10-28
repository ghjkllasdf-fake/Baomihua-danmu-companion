import { defaultSettings, validSettings } from '../ui/src/model.ts';

const budgetMs = 100;
const maxClockUncertaintyMs = 5;
const clockSafetyAllowanceMs = 1;

export function assertLatencyOwner(expectedNonce, observed) {
  if (typeof expectedNonce !== 'string' || !/^[a-f0-9]{32}$/.test(expectedNonce) ||
    observed?.nonce !== expectedNonce || observed.url !== 'https://danmu.local/index.html') throw new Error('The CDP document does not belong to this latency run.');
  return true;
}

function finite(value) {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw new Error('Invalid timestamp.');
  return value;
}

function nonempty(rows) {
  if (!Array.isArray(rows) || rows.length === 0) throw new Error('Missing evidence.');
  return rows;
}

function brackets(rows) {
  return nonempty(rows).map(row => {
    const before = finite(row.nativeBeforeMs), after = finite(row.nativeAfterMs), browser = finite(row.browserMs);
    if (after < before) throw new Error('Reversed calibration.');
    return { lower: before - browser, upper: after - browser, width: after - before, browser, before, after, origin: finite(row.timeOriginMs) };
  });
}

function settingsKey(value) {
  if (!validSettings(value)) throw new Error('Missing or invalid complete settings.');
  const keys = Object.keys(value).sort();
  if (keys.join('|') !== Object.keys(defaultSettings).sort().join('|')) throw new Error('Unexpected settings fields.');
  return JSON.stringify(keys.map(key => [key, value[key]]));
}

export function assessSettingsLatency(evidence) {
  if (evidence.ready !== true || evidence.finished !== true || evidence.failure !== '' || evidence.navigationCount !== 0 ||
    !Number.isSafeInteger(evidence.heartbeatCount) || evidence.heartbeatCount < 2 || finite(evidence.maxObserverGapMs) <= 0 ||
    evidence.maxObserverGapMs > 500) throw new Error('Incomplete or interrupted observation.');
  const beforeRows = brackets(evidence.calibrationBefore), afterRows = brackets(evidence.calibrationAfter);
  const allRows = [...beforeRows, ...afterRows], origin = allRows[0].origin;
  let lastNative = -1, lastBrowser = -1;
  for (const row of allRows) {
    if (origin <= 0 || row.origin !== origin || row.before < lastNative || row.browser <= lastBrowser) throw new Error('Calibration epoch or order changed.');
    lastNative = row.after; lastBrowser = row.browser;
  }
  const tightest = rows => [...rows].sort((a, b) => a.width - b.width)[0];
  const before = tightest(beforeRows), after = tightest(afterRows);
  const offsetLower = Math.min(before.lower, after.lower) - clockSafetyAllowanceMs;
  const offsetUpper = Math.max(before.upper, after.upper) + clockSafetyAllowanceMs;
  if (offsetUpper - offsetLower > maxClockUncertaintyMs || allRows.some(row =>
    row.upper + clockSafetyAllowanceMs < offsetLower || row.lower - clockSafetyAllowanceMs > offsetUpper)) throw new Error('Uncertain or contradictory clock mapping.');
  const edits = nonempty(evidence.edits), deliveries = nonempty(evidence.deliveries);
  if (edits.length !== deliveries.length) throw new Error('Missing or extra delivery.');
  const ids = new Set(); let previousBrowser = beforeRows.at(-1).browser, previousNative = -1, previousCommitted = beforeRows.at(-1).after;
  const samples = edits.map((edit, index) => {
    if (typeof edit.id !== 'string' || !edit.id || ids.has(edit.id)) throw new Error('Invalid edit identity.');
    ids.add(edit.id);
    const browser = finite(edit.browserMs), delivery = deliveries[index], native = finite(delivery.nativeMs), committed = finite(delivery.committedMs);
    if (edit.timeOriginMs !== origin || browser <= previousBrowser || browser >= afterRows[0].browser || native < previousNative) throw new Error('Out-of-order or uncalibrated event.');
    if (committed < previousCommitted || committed < browser + offsetLower || committed > native) throw new Error('Invalid commit-to-renderer order.');
    if (delivery.matchesCommitted !== true || settingsKey(edit.expectedSettings) !== settingsKey(delivery.settings)) throw new Error('Actual renderer settings did not match the edit.');
    const lowerMs = native - (browser + offsetUpper), upperMs = native - (browser + offsetLower);
    if (upperMs < 0 || native > afterRows[0].before) throw new Error('Delivery outside the calibrated series.');
    previousBrowser = browser; previousNative = native; previousCommitted = committed;
    return { id: edit.id, lowerMs, upperMs, passed: upperMs <= budgetMs };
  });
  return {
    passed: samples.every(sample => sample.passed), budgetMs,
    clockUncertaintyMs: offsetUpper - offsetLower,
    clockSafetyAllowanceMs,
    assumptions: 'Stable short-run browser/QPC alignment between calibrations and timer coarsening within the 1ms allowance; endpoints and heartbeat cannot exclude an unobserved out-and-back clock change.',
    offsetLowerMs: offsetLower, offsetUpperMs: offsetUpper,
    maxUpperMs: Math.max(...samples.map(sample => sample.upperMs)), samples,
  };
}

export function assessLoadedSettingsLatency(evidence) {
  const assessment = assessSettingsLatency(evidence), load = evidence.playbackLoad;
  if (load?.requested !== true || load.loadedCount !== 50000 || !load.start || !load.finish)
    throw new Error('Missing explicitly requested 50000-comment playback load');
  if (load.start.pendingWork !== false || load.finish.pendingWork !== false)
    throw new Error('Initial and final playback layouts must be fully prepared');
  const proofs = [load.start, ...evidence.deliveries.map(delivery => delivery.playback), load.finish];
  const identity = proof => JSON.stringify([proof.context.title, proof.context.windowTitle, proof.context.hwnd, proof.context.mediaRevision]);
  let firstIdentity, previousTime = -1;
  for (const [index, proof] of proofs.entries()) {
    const context = proof?.context;
    if (!context || context.mode !== 'automatic' || context.state !== 'playing'
      || typeof context.title !== 'string' || !context.title || typeof context.windowTitle !== 'string' || !context.windowTitle
      || typeof context.hwnd !== 'string' || !/^[1-9]\d*$/.test(context.hwnd) || !Number.isSafeInteger(context.mediaRevision) || context.mediaRevision < 1
      || !['uia-seekbar-ms+smtc', 'smtc'].includes(context.source) || proof.visible !== true || proof.running !== true
      || !Number.isSafeInteger(proof.activeCount) || proof.activeCount < 1) throw new Error('Missing active automatic playback load');
    const native = finite(proof.nativeMs), stamp = finite(context.sampleStampMs);
    if (native < previousTime || stamp > native || native - stamp > 500) throw new Error('Stale or unordered playback load');
    finite(proof.positionMs);
    firstIdentity ??= identity(proof);
    if (identity(proof) !== firstIdentity) throw new Error('Playback load changed media');
    if (index > 0 && index < proofs.length - 1 && native !== evidence.deliveries[index - 1].nativeMs)
      throw new Error('Playback proof must describe the actual settings delivery');
    previousTime = native;
  }
  if (load.start.nativeMs > evidence.deliveries[0].committedMs || load.finish.nativeMs < evidence.deliveries.at(-1).nativeMs
    || load.finish.positionMs <= load.start.positionMs) throw new Error('Missing advancing playback across settings edits');
  return { ...assessment, playback: { loadedCount: load.loadedCount, proofs: proofs.length,
    advancedMs: load.finish.positionMs - load.start.positionMs, scope: 'Active load at start, every configuration delivery and end; not continuous presentation proof' } };
}
