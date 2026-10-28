import { describe, expect, it } from 'vitest';
import { defaultSettings } from './model';
import { validPayload, validResult, type CommandType } from './protocol';

const api = { apiBase: 'http://fixture.invalid/token', value: '10' };
const snapshot = { settings: defaultSettings, settingsRevision: 0 };
const hello = { ...snapshot, apiBase: '', stateSequence: 4, protocolVersion: 1, capabilities: ['future-addition', 'media-context-v1'] };
describe('command contract boundary', () => {
  it.each<[CommandType, unknown, unknown]>([
    ['hello', {}, hello], ['get-settings', {}, snapshot], ['update-settings', { settings: defaultSettings }, snapshot],
    ['configure-api', { apiBase: api.apiBase }, { configured: true }],
    ['search', api, { animes: [{ animeId: '1', animeTitle: 'work' }] }],
    ['search', { ...api, apiBase: 'http://fixture.invalid/token%3F%23' }, { animes: [] }],
    ['episodes', api, { bangumi: { episodes: [{ episodeId: '10', episodeNumber: '1', episodeTitle: '' }] } }],
    ['comments', { ...api, expectedMediaRevision: 0 }, { count: 300000 }],
    ['manual-clock', { positionMs: 0, playing: false }, { positionMs: 0, playing: false }],
    ['cancel', {}, { cancelled: true }], ['close', {}, { closing: true }],
  ])('accepts the complete %s command and response', (type, payload, response) => {
    expect(validPayload(type, payload)).toBe(true); expect(validResult(type, response)).toBe(true);
  });
  it.each<[CommandType, unknown]>([
    ['comments', api], ['comments', { ...api, expectedMediaRevision: -1 }], ['comments', { ...api, expectedMediaRevision: Number.MAX_SAFE_INTEGER + 1 }],
    ['configure-api', { apiBase: 'file:///local' }], ['configure-api', { apiBase: api.apiBase, path: 'arbitrary' }],
    ['comments', { ...api, expectedMediaRevision: '0' }], ['comments', { ...api, expectedMediaRevision: .5 }],
    ['search', { ...api, script: 'arbitrary' }], ['search', { ...api, apiBase: 'file:///local' }],
    ['search', { ...api, apiBase: 'http://user:password@fixture.invalid/' }], ['search', { ...api, value: '' }],
    ['search', { ...api, apiBase: 'http://fixture.invalid/token?' }], ['search', { ...api, apiBase: 'http://fixture.invalid/token#' }],
    ['episodes', { ...api, value: 'x'.repeat(501) }], ['hello', { path: 'arbitrary' }],
    ['update-settings', {}], ['update-settings', { settings: { ...defaultSettings, fontSize: 999 } }],
    ['update-settings', { settings: { ...defaultSettings, apiToken: 'must-not-follow-style' } }],
    ['manual-clock', { positionMs: Infinity, playing: false }], ['manual-clock', { positionMs: 1, playing: 'true' }], ['cancel', null],
  ])('rejects invalid %s payload: %j', (type, payload) => expect(validPayload(type, payload)).toBe(false));
  it.each<[CommandType, unknown]>([
    ['hello', { ...hello, capabilities: [] }], ['hello', { ...hello, settings: null }],
    ['configure-api', { configured: false }],
    ['search', { animes: [{}] }], ['search', { animes: [{ animeId: {}, animeTitle: 'work' }] }], ['search', { animes: [{ animeId: 1, animeTitle: ' ' }] }],
    ['episodes', { bangumi: null }], ['episodes', { bangumi: { episodes: [{ episodeId: 1, episodeNumber: {} }] } }],
    ['comments', { count: -1 }], ['comments', { count: 300001 }], ['comments', { count: NaN }],
    ['get-settings', defaultSettings], ['update-settings', { ...snapshot, settingsRevision: .5 }],
    ['manual-clock', { positionMs: -1, playing: true }], ['cancel', { cancelled: false }], ['close', []],
  ])('rejects malformed %s success: %j', (type, response) => expect(validResult(type, response)).toBe(false));
});
