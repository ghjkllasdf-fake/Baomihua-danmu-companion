import { describe, it, expect } from 'vitest';
import { validPayload, validResult } from './protocol';
import { defaultSettings, validHello } from './model';

describe('saved API bridge boundary', () => {
  it('only accepts a bounded HTTP address or explicit removal and never a storage path', () => {
    expect(validPayload('save-api', { apiBase: 'https://danmu.example.com/EXAMPLE' })).toBe(true);
    expect(validPayload('save-api', { apiBase: '' })).toBe(true);
    for (const apiBase of ['file:///private', ['https://user:password', 'example.com'].join('@'), 'https://danmu.example.com?token=EXAMPLE', 'x'.repeat(2049), null])
      expect(validPayload('save-api', { apiBase })).toBe(false);
    expect(validPayload('save-api', { apiBase: '', path: 'arbitrary' })).toBe(false);
  });
  it('requires an acknowledged address and consistent persisted flag', () => {
    expect(validResult('save-api', { apiBase: 'https://danmu.example.com', saved: true })).toBe(true);
    expect(validResult('save-api', { apiBase: '', saved: false })).toBe(true);
    for (const result of [{ configured: true }, { apiBase: '', saved: true }, { apiBase: 'file:///private', saved: true }])
      expect(validResult('save-api', result)).toBe(false);
  });
  it('requires persistence state when the host advertises its capability', () => {
    const hello = { apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1, capabilities: ['media-context-v1', 'api-persistence-v1'] };
    expect(validHello(hello)).toBe(false);
    expect(validHello({ ...hello, apiSaved: false, apiNotice: '' })).toBe(true);
  });
});
