import { describe, expect, it } from 'vitest';
import { defaultCheckIntervalHours, hasUpdate, supportsUpdateCheckInterval, updateCheckIntervals, validUpdatePreferences, validUpdateSnapshot, type UpdateSnapshot } from './updates';
import { defaultSettings, validHello } from './model';
import { validPayload, validResult } from './protocol';

export const updateFixture: UpdateSnapshot = {
  state: 'available', currentVersion: '0.1.1-beta.6', availableVersion: '0.1.1-beta.10', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, autoDownload: false, checkedAt: '2026-10-31T04:00:00Z', downloadedBytes: 0, totalBytes: 100,
  notes: '更新说明', message: '', revision: 1, supported: true, canInstall: false, lastResult: '',
};
describe('update state boundary', () => {
  it('offers only daily, weekly and fixed thirty-day monthly checks with daily as default', () => {
    expect(updateCheckIntervals).toEqual([24, 168, 720]);
    expect(defaultCheckIntervalHours).toBe(24);
    for (const checkIntervalHours of [1, 6, 12]) {
      expect(validUpdatePreferences({ autoCheck: true, autoDownload: false, channel: 'beta', checkIntervalHours })).toBe(false);
    }
  });
  it('accepts checked interval values and legacy snapshots without rejecting the whole hello', () => {
    for (const checkIntervalHours of updateCheckIntervals) {
      const current = { ...updateFixture, checkIntervalHours };
      expect(validUpdateSnapshot(current)).toBe(true);
      expect(supportsUpdateCheckInterval(current)).toBe(true);
      expect(validResult('get-update', current)).toBe(true);
      expect(validPayload('set-update-preferences', { autoCheck: true, autoDownload: false, channel: 'beta', checkIntervalHours })).toBe(true);
    }
    expect(validUpdateSnapshot(updateFixture)).toBe(true);
    expect(supportsUpdateCheckInterval(updateFixture)).toBe(false);
    expect(hasUpdate(updateFixture)).toBe(true);
    expect(validHello({ protocolVersion: 1, capabilities: ['media-context-v1', 'github-update-v1'],
      apiBase: '', settings: defaultSettings, settingsRevision: 0, stateSequence: 0, update: updateFixture })).toBe(true);
    expect(validPayload('set-update-preferences', { autoCheck: false, autoDownload: false, channel: 'stable' })).toBe(true);
  });
  it('rejects present but invalid intervals and unexpected preference/snapshot fields', () => {
    for (const checkIntervalHours of [0, 2, 48, -1, 1.5, NaN, Infinity, '6', null, undefined]) {
      expect(validUpdateSnapshot({ ...updateFixture, checkIntervalHours })).toBe(false);
      expect(validUpdatePreferences({ autoCheck: true, autoDownload: false, channel: 'beta', checkIntervalHours })).toBe(false);
    }
    expect(validUpdatePreferences({ autoCheck: true, autoDownload: false, channel: 'beta', checkIntervalHours: 6, extra: true })).toBe(false);
    expect(validUpdateSnapshot({ ...updateFixture, checkIntervalHours: 6, extra: true })).toBe(false);
  });
  it('recognizes only a validated newer release for the home indicator', () => {
    expect(validUpdateSnapshot(updateFixture)).toBe(true);
    expect(hasUpdate(updateFixture)).toBe(true);
    expect(validUpdateSnapshot({ ...updateFixture, availableVersion: '0.1.1-beta.5' })).toBe(false);
    expect(hasUpdate({ ...updateFixture, state: 'current', availableVersion: '' })).toBe(false);
  });
  it('rejects forged readiness, malformed data and incompatible channels', () => {
    for (const patch of [{ state: 'ready', downloadedBytes: 1, canInstall: true }, { canInstall: true }, { totalBytes: -1 },
      { downloadedBytes: 101 }, { revision: Infinity }, { checkedAt: 'invalid' }, { channel: 'preview' }, { source: 'https://evil.example' }])
      expect(validUpdateSnapshot({ ...updateFixture, ...patch })).toBe(false);
    expect(validUpdateSnapshot({ ...updateFixture, state: 'ready', downloadedBytes: 100, canInstall: true })).toBe(true);
  });
  it('accepts only the complete preference contract, never an arbitrary source or path', () => {
    expect(validUpdatePreferences({ autoCheck: true, autoDownload: false, channel: 'beta' })).toBe(true);
    expect(validUpdatePreferences({ autoCheck: true, autoDownload: false, channel: 'beta', url: 'https://evil.example' })).toBe(false);
    expect(validUpdatePreferences({ autoCheck: true, channel: 'beta' })).toBe(false);
  });
});
