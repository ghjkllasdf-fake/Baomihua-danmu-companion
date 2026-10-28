import { afterEach, describe, expect, it, vi } from 'vitest';
import { createNativeNoticeHandler } from './host-notices';
import { defaultSettings } from './model';

const handshake = (stateSequence = 0) => ({ apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence, protocolVersion: 1, capabilities: ['media-context-v1'] });
const searchPayload = { apiBase: 'http://fixture.invalid/token', value: 'A' };

function nativeWindow() {
  const callbacks: ((event: { data: unknown }) => void)[] = [];
  const sent: { id: string; type: string }[] = [];
  return {
    sent,
    emit: (data: unknown) => callbacks.forEach(callback => callback({ data })),
    window: { chrome: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string }) => sent.push(message),
    } } },
  };
}
async function connectedBridge(native: ReturnType<typeof nativeWindow>) {
  const bridge = await import('./bridge');
  const hello = bridge.request('hello');
  native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: handshake() });
  await hello; native.sent.length = 0;
  return bridge;
}
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); vi.resetModules(); });

describe('bridge document identity', () => {
  it('clears a recovered host notice from subscribed states without removing an unrelated error', async () => {
    vi.useFakeTimers(); vi.resetModules();
    const native = nativeWindow(); vi.stubGlobal('window', native.window);
    const bridge = await import('./bridge');
    let error = '';
    const notice = createNativeNoticeHandler(update => { error = update(error); });
    const stop = bridge.subscribe(data => { if (data && typeof data === 'object') notice((data as Record<string, unknown>).notice); });
    const hello = bridge.request('hello');
    native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: handshake() });
    await hello;
    native.emit({ type: 'state', sequence: 1, data: { notice: 'preference warning' } });
    expect(error).toBe('preference warning');
    native.emit({ type: 'state', sequence: 2, data: { notice: '' } });
    expect(error).toBe('');
    native.emit({ type: 'state', sequence: 3, data: { notice: 'preference warning' } });
    error = 'manual clock failed';
    native.emit({ type: 'state', sequence: 4, data: { notice: '' } });
    expect(error).toBe('manual clock failed'); stop();
  });
  it('adapts an additive native save revision without changing the existing wire settings data', async () => {
    vi.useFakeTimers(); vi.resetModules();
    const native = nativeWindow(); vi.stubGlobal('window', native.window);
    const bridge = await connectedBridge(native);
    const settings = { ...defaultSettings, fontSize: 40 };
    const update = bridge.request('update-settings', { settings });
    native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: settings, settingsRevision: 3 });
    await expect(update).resolves.toEqual({ settings, settingsRevision: 3 });
  });
  it('distinguishes a settings timeout from a confirmed native rejection and ignores late replies', async () => {
    vi.useFakeTimers(); vi.resetModules();
    const native = nativeWindow(); vi.stubGlobal('window', native.window);
    const bridge = await connectedBridge(native);
    const update = bridge.request('update-settings', { settings: defaultSettings });
    let failure: unknown;
    const outcome = update.catch(error => { failure = error; });
    await vi.advanceTimersByTimeAsync(10000);
    try {
      expect(failure).toBeInstanceOf(Error);
      expect(failure).toMatchObject({ code: 'timeout' });
      native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: 'late success' });
      const read = bridge.request('get-settings');
      native.emit({ type: 'response', id: native.sent[1].id, ok: false, code: 'settings-not-applied', error: 'known failure' });
      await expect(read).rejects.toMatchObject({ code: 'settings-not-applied', message: 'known failure' });
    } finally {
      native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: 'cleanup' });
      await outcome;
    }
  });
  it('does not let a previous document response settle a replacement document request', async () => {
    vi.useFakeTimers(); vi.resetModules();
    const old = nativeWindow(); vi.stubGlobal('window', old.window);
    const first = await connectedBridge(old);
    const previous = first.request('search', searchPayload);
    const oldId = old.sent[0].id;
    vi.resetModules();
    const current = nativeWindow(); vi.stubGlobal('window', current.window);
    const second = await connectedBridge(current);
    const replacement = second.request('search', searchPayload);
    let outcome: unknown;
    void replacement.then(value => { outcome = value; }, error => { outcome = error; });
    try {
      current.emit({ type: 'response', id: oldId, ok: false, error: 'Old request cancelled.' });
      await Promise.resolve();
      expect(outcome).toBeUndefined();
      expect(current.sent[0].id).not.toBe(oldId);
      expect(current.sent[0].id.length).toBeLessThanOrEqual(64);
      current.emit({ type: 'response', id: current.sent[0].id, ok: true, data: { animes: [] } });
      await expect(replacement).resolves.toEqual({ animes: [] });
    } finally {
      old.emit({ type: 'response', id: oldId, ok: true, data: { animes: [] } });
      current.emit({ type: 'response', id: current.sent[0].id, ok: true, data: 'cleanup' });
      await previous;
    }
  });
  it('accepts only newer state events after the current document handshake watermark', async () => {
    vi.useFakeTimers(); vi.resetModules();
    const native = nativeWindow(); vi.stubGlobal('window', native.window);
    const bridge = await import('./bridge');
    const states: unknown[] = []; bridge.subscribe(data => states.push(data));
    const hello = bridge.request('hello');
    try {
      native.emit({ type: 'state', sequence: 4, data: 'old document' });
      expect(states).toEqual([]);
      native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: handshake(5) });
      await hello;
      native.emit({ type: 'state', sequence: 5, data: 'before handshake' });
      native.emit({ type: 'state', sequence: 6, data: 'current' });
      native.emit({ type: 'state', sequence: 6, data: 'duplicate' });
      native.emit({ type: 'state', sequence: 3, data: 'out of order' });
      native.emit({ type: 'state', sequence: '7', data: 'malformed' });
      native.emit({ type: 'state', sequence: 7, data: 'latest' });
      expect(states).toEqual(['current', 'latest']);
    } finally {
      native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: handshake(5) });
      await hello;
    }
  });
  it.each([undefined, null, {}, { stateSequence: -1 }, { stateSequence: 1.5 }, { stateSequence: '1' }, { stateSequence: Number.MAX_SAFE_INTEGER + 1 }])('rejects an invalid hello watermark and allows a valid retry: %j', async data => {
    vi.useFakeTimers(); vi.resetModules();
    const native = nativeWindow(); vi.stubGlobal('window', native.window);
    const bridge = await import('./bridge');
    const states: unknown[] = []; bridge.subscribe(data => states.push(data));
    const hello = bridge.request('hello');
    native.emit({ type: 'response', id: native.sent[0].id, ok: true, data });
    await expect(hello).rejects.toThrow('状态版本');
    native.emit({ type: 'state', sequence: 10, data: 'unverified' });
    expect(states).toEqual([]);
    const retry = bridge.request('hello');
    native.emit({ type: 'response', id: native.sent[1].id, ok: true, data: handshake(10) });
    await retry;
    native.emit({ type: 'state', sequence: 11, data: 'verified' });
    expect(states).toEqual(['verified']);
  });
  it('does not rewind the accepted state version when a repeated hello returns an older watermark', async () => {
    vi.useFakeTimers(); vi.resetModules();
    const native = nativeWindow(); vi.stubGlobal('window', native.window);
    const bridge = await import('./bridge');
    const states: unknown[] = []; bridge.subscribe(data => states.push(data));
    const hello = bridge.request('hello');
    native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: handshake(5) });
    await hello;
    native.emit({ type: 'state', sequence: 6, data: 'current' });
    const retry = bridge.request('hello');
    native.emit({ type: 'response', id: native.sent[1].id, ok: true, data: handshake(5) });
    await retry;
    native.emit({ type: 'state', sequence: 6, data: 'replayed' });
    native.emit({ type: 'state', sequence: 7, data: 'latest' });
    expect(states).toEqual(['current', 'latest']);
  });
});
