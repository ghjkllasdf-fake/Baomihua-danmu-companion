import { afterEach, expect, it, vi } from 'vitest';
import { defaultSettings } from './model';

afterEach(() => { vi.unstubAllGlobals(); vi.resetModules(); });
it.each([
  {},
  { protocolVersion: 1, capabilities: [] },
  { protocolVersion: 2, capabilities: ['media-context-v1'] },
  { protocolVersion: 1, capabilities: 'media-context-v1' },
])('does not trust an old or incompatible host handshake: %j', async compatibility => {
  const callbacks: ((event: { data: unknown }) => void)[] = [];
  const sent: { id: string }[] = [];
  vi.stubGlobal('window', { chrome: { webview: {
    addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
    postMessage: (message: { id: string }) => sent.push(message),
  } } });
  const bridge = await import('./bridge');
  const hello = bridge.request('hello');
  callbacks.forEach(callback => callback({ data: { type: 'response', id: sent[0].id, ok: true, data: {
    apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, ...compatibility,
  } } }));
  await expect(hello).rejects.toMatchObject({ code: 'host-incompatible' });
  await expect(bridge.request('comments', { apiBase: 'http://fixture.invalid/token', value: '10', expectedMediaRevision: 0 })).rejects.toMatchObject({ code: 'host-not-ready' });
  expect(sent).toHaveLength(1);
});
