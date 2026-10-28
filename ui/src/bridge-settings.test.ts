import { afterEach, expect, it, vi } from 'vitest';

afterEach(() => { vi.unstubAllGlobals(); vi.resetModules(); vi.useRealTimers(); });
it('keeps an interrupted real bridge save uncertain and recovers by read after verified hello', async () => {
  vi.useFakeTimers(); vi.resetModules();
  const callbacks: ((event: { data: unknown }) => void)[] = [];
  const sent: { id: string; type: string }[] = [];
  vi.stubGlobal('window', { chrome: { webview: {
    addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
    postMessage: (message: { id: string; type: string }) => sent.push(message),
  } } });
  const [bridge, sessionModule, model] = await Promise.all([import('./bridge'), import('./settings-session'), import('./model')]);
  const data = { apiBase: '', settings: model.defaultSettings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1, capabilities: ['media-context-v1'] };
  const emit = (value: unknown) => callbacks.forEach(callback => callback({ data: value }));
  const handshake = bridge.request('hello'); emit({ type: 'response', id: sent[0].id, ok: true, data }); await handshake;
  const session = new sessionModule.SettingsSession(model.defaultSettings,
    (type, payload) => type === 'get-settings' ? bridge.request('get-settings') : payload ? bridge.request('update-settings', payload) : Promise.reject(new Error('Missing settings.')), () => {});
  try {
    session.connect(data); session.edit({ fontSize: 40 });
    await vi.advanceTimersByTimeAsync(40);
    expect(sent.map(value => value.type)).toEqual(['hello', 'update-settings']);
    const replacement = bridge.request('hello');
    await vi.advanceTimersByTimeAsync(0);
    expect(session.view()).toMatchObject({ status: 'unknown', settings: { fontSize: 40 } });
    expect(sent.map(value => value.type)).toEqual(['hello', 'update-settings', 'hello']);
    emit({ type: 'response', id: sent[2].id, ok: true, data }); await replacement;
    session.retryRecovery();
    expect(sent.map(value => value.type)).toEqual(['hello', 'update-settings', 'hello', 'get-settings']);
    emit({ type: 'response', id: sent[3].id, ok: true, data: { settings: { ...model.defaultSettings, fontSize: 40 }, settingsRevision: 1 } });
    await vi.advanceTimersByTimeAsync(0);
    expect(session.view()).toMatchObject({ status: 'saved', settings: { fontSize: 40 } });
    expect(sent.filter(value => value.type === 'update-settings')).toHaveLength(1);
  } finally { session.dispose(); }
});
