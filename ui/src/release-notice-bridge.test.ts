import { afterEach, describe, expect, it, vi } from 'vitest';
import { defaultSettings } from './model';

const snapshot = { state: 'available', currentVersion: '0.1.1-beta.7', availableVersion: '0.1.1-beta.10', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, checkIntervalHours: 24, checkedAt: '', notes: '', message: '', revision: 1, supported: true,
  releasePage: 'https://github.com/ghjkllasdf-fake/Baomihua-danmu-companion/releases' };
function nativeWindow() {
  const callbacks: ((event: { data: unknown }) => void)[] = [], sent: { id: string; type: string; payload: unknown }[] = [];
  return { sent, emit: (data: unknown) => callbacks.forEach(callback => callback({ data })), window: { chrome: { webview: {
    addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
    postMessage: (message: { id: string; type: string; payload: unknown }) => sent.push(message),
  } } } };
}
async function connect(native: ReturnType<typeof nativeWindow>, capability = 'github-release-notice-v1') {
  vi.useFakeTimers(); vi.resetModules(); vi.stubGlobal('window', native.window);
  const bridge = await import('./bridge'), hello = bridge.request('hello');
  native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: { protocolVersion: 1, capabilities: ['media-context-v1', capability],
    apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, update: snapshot } });
  await hello; native.sent.length = 0; return bridge;
}
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); vi.resetModules(); });
describe('release notice bridge', () => {
  it.each(['github-update-v1', 'portable-setup-v1', 'unrelated-v1'])('does not authorize notice commands from old capability %s', async capability => {
    const native = nativeWindow(), bridge = await connect(native, capability);
    await expect(bridge.request('check-update')).rejects.toMatchObject({ code: 'unsupported-capability' });
    await expect(bridge.request('open-update-page')).rejects.toMatchObject({ code: 'unsupported-capability' });
    expect(native.sent).toEqual([]);
  });
  it.each(['download-update', 'install-update', 'prepare-portable'])('never sends retired %s even after a valid new handshake', async command => {
    const native = nativeWindow(), bridge = await connect(native);
    await expect(Reflect.apply(bridge.request, null, [command, {}])).rejects.toMatchObject({ code: 'invalid-payload' });
    expect(native.sent).toEqual([]);
  });
  it('sends an empty opening command and rejects a response-provided URL without navigating', async () => {
    const native = nativeWindow(), bridge = await connect(native);
    const open = bridge.request('open-update-page');
    const rejected = expect(open).rejects.toMatchObject({ code: 'invalid-response' });
    try {
      expect(native.sent[0]).toMatchObject({ type: 'open-update-page', payload: {} });
      native.emit({ type: 'response', id: native.sent[0].id, ok: true, data: { opened: true, url: 'https://evil.example' } });
    } finally { await rejected; }
  });
  it('revokes a pending check when a replacement handshake lacks the new capability', async () => {
    const native = nativeWindow(), bridge = await connect(native);
    const check = bridge.request('check-update'), rejected = expect(check).rejects.toMatchObject({ code: 'host-not-ready' });
    try {
      const hello = bridge.request('hello');
      native.emit({ type: 'response', id: native.sent[1].id, ok: true, data: { protocolVersion: 1, capabilities: ['media-context-v1', 'github-update-v1'],
        apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, update: snapshot } });
      await hello;
    } finally { await rejected; }
    await expect(bridge.request('check-update')).rejects.toMatchObject({ code: 'unsupported-capability' });
    expect(native.sent.map(value => value.type)).toEqual(['check-update', 'hello']);
  });
});
