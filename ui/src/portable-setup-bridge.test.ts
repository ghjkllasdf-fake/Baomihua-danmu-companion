import { afterEach, describe, expect, it, vi } from 'vitest';
import { defaultSettings } from './model';

const snapshot = {
  state: 'unavailable', currentVersion: '0.1.1-beta.7', availableVersion: '', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, autoDownload: false, checkIntervalHours: 24, checkedAt: '', downloadedBytes: 0, totalBytes: 0,
  notes: '', message: '', revision: 1, supported: false, canInstall: false, lastResult: '', portableSetupState: 'required',
};
function nativeWindow() {
  const callbacks: ((event: { data: unknown }) => void)[] = [];
  const sent: { id: string; type: string; payload: unknown }[] = [];
  return { sent, emit: (data: unknown) => callbacks.forEach(callback => callback({ data })), window: { chrome: { webview: {
    addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
    postMessage: (message: { id: string; type: string; payload: unknown }) => sent.push(message),
  } } } };
}
async function connect(native: ReturnType<typeof nativeWindow>, setupCapability: boolean) {
  vi.useFakeTimers(); vi.resetModules(); vi.stubGlobal('window', native.window);
  const bridge = await import('./bridge');
  const hello = bridge.request('hello');
  native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: {
    protocolVersion: 1, capabilities: ['media-context-v1', 'github-update-v1', ...(setupCapability ? ['portable-setup-v1'] : [])],
    apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, update: snapshot,
  } });
  await hello; native.sent.length = 0;
  return bridge;
}
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); vi.resetModules(); });

describe('Portable preparation bridge', () => {
  it('does not send preparation without its capability', async () => {
    const native = nativeWindow(), bridge = await connect(native, false);
    await expect(bridge.request('prepare-portable', {})).rejects.toMatchObject({ code: 'unsupported-capability' });
    expect(native.sent).toEqual([]);
  });
  it('sends only an empty payload and accepts the full prepared snapshot', async () => {
    const native = nativeWindow(), bridge = await connect(native, true);
    const prepare = bridge.request('prepare-portable', {});
    expect(native.sent[0]).toMatchObject({ type: 'prepare-portable', payload: {} });
    const result = { ...snapshot, portableSetupState: 'prepared', revision: 2 };
    native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: result });
    await expect(prepare).resolves.toEqual(result);
  });
  it('rejects a mere accepted acknowledgement instead of claiming preparation completed', async () => {
    const native = nativeWindow(), bridge = await connect(native, true);
    const prepare = bridge.request('prepare-portable', {});
    native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: { accepted: true } });
    await expect(prepare).rejects.toMatchObject({ code: 'invalid-response' });
  });
  it('revokes pending preparation after a new handshake loses the capability', async () => {
    const native = nativeWindow(), bridge = await connect(native, true);
    const prepare = bridge.request('prepare-portable', {});
    const rejected = expect(prepare).rejects.toMatchObject({ code: 'host-not-ready' });
    const hello = bridge.request('hello');
    native.emit({ type: 'response', id: native.sent[1].id, ok: true, data: {
      protocolVersion: 1, capabilities: ['media-context-v1', 'github-update-v1'], apiBase: '', settings: defaultSettings,
      settingsRevision: 0, stateSequence: 0, update: { ...snapshot, portableSetupState: 'none' },
    } });
    await hello; await rejected;
    await expect(bridge.request('prepare-portable', {})).rejects.toMatchObject({ code: 'unsupported-capability' });
    expect(native.sent.map(value => value.type)).toEqual(['prepare-portable', 'hello']);
  });
});
