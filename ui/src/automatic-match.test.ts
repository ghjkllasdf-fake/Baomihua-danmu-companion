import { describe, expect, it } from 'vitest';
import { validAutomaticMatch } from './automatic-match';

describe('automatic match status boundary', () => {
  it('accepts native progress, success and actionable failure without a fabricated playback proof', () => {
    for (const state of ['waiting', 'matching', 'loading', 'loaded', 'failed', 'manual']) {
      expect(validAutomaticMatch({ state, message: '当前状态', sourceTitle: '' })).toBe(true);
    }
  });
  it.each([null, {}, { state: 'loaded', message: 1, sourceTitle: '' },
    { state: 'invented', message: '状态', sourceTitle: '' },
    { state: 'loaded', message: 'x'.repeat(1001), sourceTitle: '' },
    { state: 'loaded', message: '状态', sourceTitle: 'x'.repeat(501) }])('rejects malformed or oversized native status: %j', value => {
    expect(validAutomaticMatch(value)).toBe(false);
  });
});
