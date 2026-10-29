import { afterEach, expect, it, vi } from 'vitest';
import { defaultSettings } from './model';

const view = { enabled: true, state: 'failed', message: '启动失败。', sources: ['douban'], availableSources: [{ id: 'douban', label: '豆瓣' }] };
afterEach(() => { vi.unstubAllGlobals(); vi.resetModules(); });
async function fixture(embedded: boolean) {
  vi.resetModules();
  const callbacks: ((event: { data: unknown }) => void)[] = [], sent: { id: string; type: string; payload: unknown }[] = [];
  vi.stubGlobal('window', { chrome: { webview: {
    addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
    postMessage: (message: { id: string; type: string; payload: unknown }) => sent.push(message),
  } } });
  const bridge = await import('./bridge');
  const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
  const handshake = bridge.request('hello');
  emit({ type: 'response', id: sent[0].id, ok: true, data: { apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1,
    capabilities: ['media-context-v1', ...(embedded ? ['embedded-api-v1'] : [])], ...(embedded ? { localApi: view } : {}) } });
  await handshake; sent.length = 0;
  return { bridge, sent, emit };
}
it('gates both local API commands on the handshake capability', async () => {
  const { bridge, sent } = await fixture(false);
  await expect(bridge.request('get-local-api', {})).rejects.toMatchObject({ code: 'unsupported-capability' });
  await expect(bridge.request('set-local-sources', { sources: ['douban'] })).rejects.toMatchObject({ code: 'unsupported-capability' });
  expect(sent).toEqual([]);
});
it('posts valid source changes and rejects invalid responses after a capable handshake', async () => {
  const { bridge, sent, emit } = await fixture(true);
  const write = bridge.request('set-local-sources', { sources: ['douban'] });
  expect(sent[0]).toMatchObject({ type: 'set-local-sources', payload: { sources: ['douban'] } });
  emit({ type: 'response', id: sent[0].id, ok: true, data: { ...view, token: 'secret' } });
  await expect(write).rejects.toMatchObject({ code: 'invalid-response' });
});
