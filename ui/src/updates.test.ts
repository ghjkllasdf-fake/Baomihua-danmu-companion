import { describe, expect, it } from 'vitest';
import { defaultCheckIntervalHours, hasUpdate, releasePageRoot, updateCheckIntervals, validReleasePage, validUpdatePreferences, validUpdateSnapshot, type UpdateSnapshot } from './updates';
import { defaultSettings, validHello } from './model';
import { validPayload, validResult, type CommandType } from './protocol';

export const updateFixture: UpdateSnapshot = {
  state: 'available', currentVersion: '0.1.1-beta.7', availableVersion: '0.1.1-beta.10', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, checkIntervalHours: 24, checkedAt: '2026-11-01T02:00:00Z', notes: '更新说明', message: '', revision: 1,
  supported: true, releasePage: `${releasePageRoot}/tag/v0.1.1-beta.10`,
};
describe('release notification boundary', () => {
  it('requires exactly the fourteen notification fields and the three preference fields', () => {
    expect(Object.keys(updateFixture)).toHaveLength(14);
    expect(validUpdateSnapshot(updateFixture)).toBe(true);
    for (const key of Object.keys(updateFixture)) {
      const incomplete = { ...updateFixture } as Record<string, unknown>; delete incomplete[key];
      expect(validUpdateSnapshot(incomplete)).toBe(false);
    }
    expect(validUpdatePreferences({ autoCheck: true, channel: 'beta', checkIntervalHours: 24 })).toBe(true);
    expect(validUpdatePreferences({ autoCheck: true, channel: 'beta' })).toBe(false);
    expect(validUpdatePreferences({ autoCheck: true, channel: 'beta', checkIntervalHours: 24, autoDownload: false })).toBe(false);
  });
  it('keeps daily, weekly and thirty-day monthly checks, with daily as default', () => {
    expect(updateCheckIntervals).toEqual([24, 168, 720]); expect(defaultCheckIntervalHours).toBe(24);
    for (const checkIntervalHours of updateCheckIntervals) {
      expect(validUpdateSnapshot({ ...updateFixture, checkIntervalHours })).toBe(true);
      expect(validPayload('set-update-preferences', { autoCheck: false, channel: 'stable', checkIntervalHours })).toBe(true);
    }
    for (const checkIntervalHours of [0, 1, 6, 12, 48, -1, 1.5, NaN, Infinity, '24', null, undefined])
      expect(validUpdateSnapshot({ ...updateFixture, checkIntervalHours })).toBe(false);
  });
  it.each(['autoDownload', 'downloadedBytes', 'totalBytes', 'canInstall', 'lastResult', 'portableSetupState', 'targetPath'])('rejects retired or arbitrary snapshot field %s', field => {
    expect(validUpdateSnapshot({ ...updateFixture, [field]: false })).toBe(false);
  });
  it.each(['downloading', 'verifying', 'ready', 'applying', 'prepared'])('rejects retired state %s', state => {
    expect(validUpdateSnapshot({ ...updateFixture, state })).toBe(false);
  });
  it('accepts the six notice states without inventing availability or readiness', () => {
    for (const state of ['unavailable', 'idle', 'checking', 'current', 'failed'])
      expect(validUpdateSnapshot({ ...updateFixture, state, availableVersion: '', releasePage: releasePageRoot })).toBe(true);
    expect(hasUpdate(updateFixture)).toBe(true);
    expect(hasUpdate({ ...updateFixture, state: 'current', availableVersion: '', releasePage: releasePageRoot })).toBe(false);
    for (const patch of [{ state: 'available', availableVersion: '' }, { state: 'current' }, { supported: false }, { availableVersion: '0.1.1-beta.5' },
      { revision: Infinity }, { checkedAt: 'invalid' }, { channel: ['beta'] }, { variant: ['standard'] }, { distribution: 'unknown' }, { message: 'x'.repeat(501) }])
      expect(validUpdateSnapshot({ ...updateFixture, ...patch })).toBe(false);
  });
  it('allows only the canonical project releases page or the matching available version tag', () => {
    expect(validReleasePage(releasePageRoot, '')).toBe(true);
    for (const prefix of ['', 'v']) expect(validReleasePage(`${releasePageRoot}/tag/${prefix}0.1.1-beta.10`, '0.1.1-beta.10')).toBe(true);
    expect(validUpdateSnapshot({ ...updateFixture, releasePage: releasePageRoot })).toBe(true);
    expect(validUpdateSnapshot({ ...updateFixture, state: 'current', availableVersion: '', releasePage: `${releasePageRoot}/tag/v0.1.1-beta.7` })).toBe(false);
  });
  it.each([
    'https://evil.example/releases', 'http://github.com/ghjkllasdf-fake/Baomihua-danmu-companion/releases',
    'https://github.com/another/project/releases', 'https://github.com.evil.example/ghjkllasdf-fake/Baomihua-danmu-companion/releases',
    `${releasePageRoot}?next=https://evil.example`, `${releasePageRoot}#fragment`, `${releasePageRoot}/`,
    `${releasePageRoot}/tag/v0.1.1-beta.9`, `${releasePageRoot}/tag/V0.1.1-beta.10`, `${releasePageRoot}/tag/v0.1.1-beta.10+build`,
    `${releasePageRoot}/tag/v0.1.1-beta.10/extra`, `${releasePageRoot}/tag/v0.1.1-beta.10%2Fextra`, `${releasePageRoot}/tag/%76%30.1.1-beta.10`,
    'javascript:alert(1)', 'file:///private', '', null,
  ])('rejects foreign, noncanonical or mismatched release page %s', releasePage => {
    expect(validUpdateSnapshot({ ...updateFixture, releasePage })).toBe(false);
  });
  it('validates new notice handshakes and does not require legacy update data for unrelated capabilities', () => {
    const hello = { protocolVersion: 1, capabilities: ['media-context-v1', 'github-release-notice-v1'], apiBase: '', settings: defaultSettings,
      settingsRevision: 0, stateSequence: 0, update: updateFixture };
    expect(validHello(hello)).toBe(true);
    expect(validHello({ ...hello, update: { ...updateFixture, autoDownload: false } })).toBe(false);
    expect(validHello({ ...hello, update: { ...updateFixture, releasePage: 'https://evil.example' } })).toBe(false);
  });
  it.each(['download-update', 'install-update', 'prepare-portable'])('rejects retired %s payloads and responses at runtime', command => {
    for (const payload of [{}, { version: '0.1.1-beta.10' }, { path: 'untrusted' }]) expect(validPayload(command as CommandType, payload)).toBe(false);
    expect(validResult(command as CommandType, updateFixture)).toBe(false);
    expect(validResult(command as CommandType, { accepted: true })).toBe(false);
  });
  it('never accepts an arbitrary URL on commands or browser-opening replies', () => {
    expect(validPayload('open-update-page', {})).toBe(true);
    expect(validPayload('open-update-page', { url: releasePageRoot })).toBe(false);
    expect(validResult('open-update-page', { opened: true })).toBe(true);
    expect(validResult('open-update-page', { opened: true, url: 'https://evil.example' })).toBe(false);
    expect(validResult('check-update', updateFixture)).toBe(true);
  });
});
