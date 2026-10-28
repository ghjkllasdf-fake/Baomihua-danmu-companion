import { afterEach, expect, it, vi } from 'vitest';
import { defaultSettings } from './model';
type Bridge = typeof import('./bridge');
const api = { apiBase: 'http://fixture.invalid/token', value: '10' };
const helloData = { apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1, capabilities: ['media-context-v1'] };
async function fixture() {
  vi.resetModules();
  const callbacks: ((event: { data: unknown }) => void)[] = [], sent: { id: string; type: string }[] = [];
  vi.stubGlobal('window', { chrome: { webview: {
    addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
    postMessage: (message: { id: string; type: string }) => sent.push(message),
  } } });
  const bridge = await import('./bridge');
  const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
  const hello = bridge.request('hello'); emit({ type: 'response', id: sent[0].id, ok: true, data: helloData }); await hello;
  sent.length = 0;
  return { bridge, sent, emit };
}
afterEach(() => { vi.unstubAllGlobals(); vi.resetModules(); });
it.each([
  { name: 'search', issue: (b: Bridge) => b.request('search', api), data: { animes: [{}] } },
  { name: 'episodes', issue: (b: Bridge) => b.request('episodes', api), data: { bangumi: null } },
  { name: 'comments', issue: (b: Bridge) => b.request('comments', { ...api, expectedMediaRevision: 0 }), data: { count: -1 } },
  { name: 'settings read', issue: (b: Bridge) => b.request('get-settings'), data: defaultSettings },
  { name: 'settings write', issue: (b: Bridge) => b.request('update-settings', { settings: defaultSettings }), data: { fontSize: 999 } },
  { name: 'manual clock', issue: (b: Bridge) => b.request('manual-clock', { positionMs: 0, playing: true }), data: { positionMs: -1, playing: true } },
  { name: 'cancel', issue: (b: Bridge) => b.request('cancel'), data: { cancelled: false } },
  { name: 'close', issue: (b: Bridge) => b.request('close'), data: {} },
])('rejects a malformed $name success at the real bridge boundary', async ({ issue, data }) => {
  const { bridge, sent, emit } = await fixture();
  const promise = issue(bridge);
  emit({ type: 'response', id: sent[0].id, ok: true, data, settingsRevision: 0 });
  await expect(promise).rejects.toMatchObject({ code: 'invalid-response' });
});
it('does not post malformed dynamic calls, even if they bypass TypeScript', async () => {
  const { bridge, sent } = await fixture();
  for (const args of [['comments', api], ['search', { ...api, path: 'arbitrary' }], ['execute', {}], ['manual-clock', { positionMs: -1, playing: true }]])
    await expect(Reflect.apply(bridge.request, undefined, args)).rejects.toMatchObject({ code: 'invalid-payload' });
  expect(sent).toEqual([]);
});
it('revokes pending work and state trust when a replacement handshake is incompatible, then permits a verified retry', async () => {
  const { bridge, sent, emit } = await fixture();
  const states: unknown[] = []; bridge.subscribe(data => states.push(data));
  const old = bridge.request('search', api); const outcome = old.catch(error => error);
  const hello = bridge.request('hello');
  await expect(outcome).resolves.toMatchObject({ code: 'host-not-ready' });
  emit({ type: 'response', id: sent[1].id, ok: true, data: { ...helloData, capabilities: [] } });
  await expect(hello).rejects.toMatchObject({ code: 'host-incompatible' });
  emit({ type: 'state', sequence: 20, data: 'untrusted' });
  await expect(bridge.request('search', api)).rejects.toMatchObject({ code: 'host-not-ready' });
  expect(states).toEqual([]); expect(sent).toHaveLength(2);
  const retry = bridge.request('hello'); emit({ type: 'response', id: sent[2].id, ok: true, data: helloData }); await retry;
  emit({ type: 'state', sequence: 1, data: 'verified' }); expect(states).toEqual(['verified']);
});
it('does not deliver an obsolete ready notification after a subscriber rejects the connection', async () => {
  const { bridge, sent, emit } = await fixture();
  let rejectReady = false;
  const stopValidator = bridge.subscribeConnection(connection => {
    if (rejectReady && connection.status === 'ready') bridge.revokeConnection('Conflicting settings.');
  });
  const statuses: string[] = [];
  const stopObserver = bridge.subscribeConnection(connection => statuses.push(connection.status));
  rejectReady = true;
  const hello = bridge.request('hello');
  emit({ type: 'response', id: sent[0].id, ok: true, data: helloData });
  await expect(hello).rejects.toMatchObject({ code: 'host-incompatible' });
  expect(statuses).toEqual(['ready', 'checking', 'failed']);
  stopValidator(); stopObserver();
  const before = statuses.length;
  bridge.revokeConnection('Still unavailable.');
  expect(statuses).toHaveLength(before);
});
