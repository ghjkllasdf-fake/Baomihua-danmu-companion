import { describe, expect, it } from 'vitest';
import { defaultSettings, validHello, validLocalApi, safeLocalApiText } from './model';
import { validPayload, validResult } from './protocol';

const availableSources = ['douban', '360', 'renren', 'hanjutv', 'tencent'].map(id => ({ id, label: id }));
const localApi = { enabled: true, state: 'ready', message: '本地服务已就绪。', sources: ['douban', '360', 'renren', 'hanjutv'], availableSources };
const hello = { settings: defaultSettings, settingsRevision: 0, apiBase: '', stateSequence: 0, protocolVersion: 1, capabilities: ['media-context-v1', 'embedded-api-v1'], localApi };

describe('embedded API contract', () => {
  it('requires a valid embedded view in a capable hello without changing legacy hello', () => {
    expect(validHello(hello)).toBe(true);
    expect(validHello({ ...hello, localApi: undefined })).toBe(false);
    expect(validHello({ ...hello, localApi: { ...localApi, state: 'unknown' } })).toBe(false);
    expect(validHello({ ...hello, capabilities: ['media-context-v1'], localApi: undefined })).toBe(true);
  });
  it('accepts known unique sources that are offered by the host and rejects secrets or invalid shapes', () => {
    expect(validLocalApi(localApi)).toBe(true);
    for (const invalid of [
      { ...localApi, enabled: false }, { ...localApi, state: 'oops' }, { ...localApi, message: 'x'.repeat(501) },
      { ...localApi, sources: [] }, { ...localApi, sources: ['douban', 'douban'] },
      { ...localApi, sources: ['unknown'] }, { ...localApi, sources: ['youku'] },
      { ...localApi, availableSources: [] }, { ...localApi, availableSources: [...availableSources, availableSources[0]] },
      { ...localApi, availableSources: [{ id: 'unknown', label: 'unknown' }] },
      { ...localApi, availableSources: [{ id: 'douban', label: '', token: 'secret' }] },
      { ...localApi, token: 'secret' }, { ...localApi, port: 5000 },
    ]) expect(validLocalApi(invalid)).toBe(false);
  });
  it('sends only an empty read or ordered, unique known sources and validates responses', () => {
    expect(validPayload('get-local-api', {})).toBe(true);
    expect(validPayload('get-local-api', { address: 'bad' })).toBe(false);
    expect(validPayload('set-local-sources', { sources: ['hanjutv', 'douban'] })).toBe(true);
    for (const sources of [[], ['douban', 'douban'], ['unknown'], ['douban', 1]])
      expect(validPayload('set-local-sources', { sources })).toBe(false);
    expect(validPayload('set-local-sources', { sources: ['douban'], token: 'bad' })).toBe(false);
    expect(validResult('get-local-api', localApi)).toBe(true);
    expect(validResult('set-local-sources', { ...localApi, sources: [] })).toBe(false);
  });
  it('does not render a host-provided address, path, credential, or token', () => {
    expect(safeLocalApiText('本地服务已就绪。', 'fallback')).toBe('本地服务已就绪。');
    for (const text of ['http://127.0.0.1:123/secret', '127.0.0.1', 'C:\\profile\\config.json', 'a'.repeat(64), 'token secret'])
      expect(safeLocalApiText(text, 'fallback')).toBe('fallback');
  });
});
