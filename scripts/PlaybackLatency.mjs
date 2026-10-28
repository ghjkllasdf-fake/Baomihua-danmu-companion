function requireProof(condition, message) {
  if (!condition) throw new Error(message);
}
const finite = value => typeof value === 'number' && Number.isFinite(value) && value >= 0;
const sameMedia = (a, b) => a.title === b.title && a.windowTitle === b.windowTitle
  && a.hwnd === b.hwnd && a.mediaRevision === b.mediaRevision && a.seekRevision === b.seekRevision;

function visibleAutomatic(row) {
  return row.mode === 'automatic' && ['playing', 'paused'].includes(row.state)
    && row.running === (row.state === 'playing') && row.visible === true
    && row.title?.length > 0 && row.windowTitle?.length > 0 && /^[1-9]\d*$/.test(row.hwnd)
    && Number.isSafeInteger(row.mediaRevision) && row.mediaRevision > 0
    && Number.isSafeInteger(row.seekRevision) && row.seekRevision >= 0
    && ['uia-seekbar-ms+smtc', 'smtc'].includes(row.source)
    && finite(row.sampleStampMs) && row.sampleStampMs <= row.committedMs
    && row.nativeMs - row.sampleStampMs <= 500 && finite(row.positionMs)
    && Number.isSafeInteger(row.activeCount) && row.activeCount > 0
    && Array.isArray(row.text) && Array.isArray(row.x)
    && row.text.length === row.activeCount && row.x.length === row.activeCount
    && row.text.every(t => typeof t === 'string' && t.length > 0)
    && row.x.every(x => typeof x === 'number' && Number.isFinite(x));
}

export function assessPlaybackLatency(rows) {
  requireProof(Array.isArray(rows) && rows.length > 0, 'Missing playback evidence');
  let time = -1, sequence = 0;
  const samples = [];
  for (const row of rows) {
    requireProof(finite(row.nativeMs) && row.nativeMs >= time, 'Unordered or invalid observer time');
    time = row.nativeMs;
    requireProof(row.type !== 'failure', 'Observer reported failure');
    if (row.type !== 'sample') continue;
    requireProof(Number.isSafeInteger(row.sequence) && row.sequence > sequence, 'Unordered sample sequence');
    requireProof(finite(row.committedMs) && row.committedMs <= row.nativeMs, 'Invalid desktop-to-renderer order');
    sequence = row.sequence;
    samples.push(row);
  }
  const arms = rows.filter(r => r.type === 'arm'), seen = new Set(), results = [];
  requireProof(arms.length >= 2, 'Need actual pause and resume actions');
  let frozenMs = 0;
  for (const [index, arm] of arms.entries()) {
    requireProof(typeof arm.id === 'string' && arm.id.length > 0 && !seen.has(arm.id), 'Duplicate or missing action identity');
    seen.add(arm.id);
    requireProof(['paused', 'playing'].includes(arm.expectedState), 'Invalid action state');
    const baseline = samples.find(r => r.sequence === arm.baselineSequence);
    requireProof(baseline && visibleAutomatic(baseline) && baseline.state !== arm.expectedState
      && arm.nativeMs >= baseline.nativeMs && arm.nativeMs - baseline.nativeMs <= 400, 'Missing fresh opposite baseline');
    const inputs = rows.filter(r => r.type === 'input-completed' && r.id === arm.id);
    requireProof(inputs.length === 1 && inputs[0].nativeMs >= arm.nativeMs
      && ['space', 'play-pause-button'].includes(inputs[0].action)
      && inputs[0].windowTitle === baseline.windowTitle, 'Missing matching real input record');
    const end = arms[index + 1]?.nativeMs ?? Infinity;
    requireProof(inputs[0].nativeMs < end, 'Next action began before input completed');
    const interval = samples.filter(r => r.nativeMs > arm.nativeMs && r.nativeMs < end);
    requireProof(interval.length > 0, 'Missing action samples');
    let prior = baseline;
    for (const row of interval) {
      requireProof(visibleAutomatic(row) && sameMedia(row, baseline), 'Action lost automatic visible media identity');
      requireProof(row.nativeMs - prior.nativeMs <= 500, 'Observation gap exceeds response budget');
      prior = row;
    }
    const delivery = interval.find(r => r.state === arm.expectedState && r.sampleStampMs >= arm.nativeMs);
    requireProof(delivery && delivery.nativeMs - arm.nativeMs <= 500, 'Missing delivery within 500ms upper bound');
    if (arm.expectedState === 'paused') {
      const steady = interval.filter(r => r.nativeMs >= delivery.nativeMs + 600);
      requireProof(steady.length >= 2 && steady.at(-1).nativeMs - steady[0].nativeMs >= 10000, 'Missing ten-second visible pause');
      const first = steady[0];
      requireProof(steady.every(r => r.state === 'paused' && r.positionMs === first.positionMs
        && JSON.stringify(r.text) === JSON.stringify(first.text) && JSON.stringify(r.x) === JSON.stringify(first.x)), 'Paused overlay drifted');
      frozenMs = Math.max(frozenMs, steady.at(-1).nativeMs - first.nativeMs);
    } else {
      const later = interval.filter(r => r.nativeMs >= delivery.nativeMs + 300 && r.nativeMs <= delivery.nativeMs + 1500);
      requireProof(later.some(r => r.state === 'playing' && r.positionMs > delivery.positionMs
        && r.text.some((text, i) => {
          const j = delivery.text.indexOf(text);
          return j >= 0 && Math.abs(r.x[i] - delivery.x[j]) > 1;
        })), 'No resumed movement of the same visible comment');
    }
    results.push({ id: arm.id, action: inputs[0].action, state: arm.expectedState, upperMs: delivery.nativeMs - arm.nativeMs });
  }
  requireProof(results.some(r => r.state === 'paused') && results.some(r => r.state === 'playing'), 'Both pause and resume are required');
  return { actions: results.length, maxUpperMs: Math.max(...results.map(r => r.upperMs)), pausedFrozenMs: frozenMs, results };
}
