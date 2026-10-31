import { describe, expect, it } from 'vitest';
import { hasUpdate, validUpdatePreferences, validUpdateSnapshot } from './updates';

export const updateFixture = {
  state: 'available', currentVersion: '0.1.1-beta.6', availableVersion: '0.1.1-beta.10', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, autoDownload: false, checkedAt: '2026-10-31T04:00:00Z', downloadedBytes: 0, totalBytes: 100,
  notes: '更新说明', message: '', revision: 1, supported: true, canInstall: false, lastResult: '',
};
describe('update state boundary', () => {
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
