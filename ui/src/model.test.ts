import { describe, expect, it } from 'vitest';
import { defaultSettings, emptyPlayer, formatTime, parseTime, playbackStatus, validPlayer, validSettings } from './model';

describe('display settings and time', () => {
  it('starts with the requested fixed-size display defaults', () => {
    expect(defaultSettings.fontSize).toBe(22);
    expect(defaultSettings.outlineWidth).toBe(0);
    expect(defaultSettings.speedMultiplier).toBe(.8);
    expect(defaultSettings.scaleWithWindow).toBe(false);
  });
  it('accepts all requested regions and rejects arbitrary values', () => {
    for (const areaFraction of [.25, .5, .75, 1]) expect(validSettings({ ...defaultSettings, areaFraction })).toBe(true);
    expect(validSettings({ ...defaultSettings, areaFraction: .3 })).toBe(false);
  });
  it('rejects invalid values rather than posting them to the host', () => {
    expect(validSettings({ ...defaultSettings, fontSize: NaN })).toBe(false);
    expect(validSettings({ ...defaultSettings, keywords: 'a'.repeat(2001) })).toBe(false);
    expect(validSettings({ ...defaultSettings, speedMultiplier: 4 })).toBe(false);
  });
  it('round-trips hours and rejects invalid manual time', () => {
    expect(formatTime(3723000)).toBe('01:02:03');
    expect(parseTime('01:02:03')).toBe(3723000);
    expect(parseTime('12:34')).toBe(754000);
    expect(parseTime('-1:00')).toBeNull();
    expect(parseTime('00:70:00')).toBeNull();
  });
});

describe('authoritative playback snapshot', () => {
  const verified = { ...emptyPlayer, sequence: 5, mediaRevision: 2, seekRevision: 3, syncMode: 'automatic', state: 'paused', title: 'movie',
    positionMs: 10000, durationMs: 60000, sampleMonotonicMs: 1000, positionResolutionMs: 200,
    videoBoundsPx: { x: -1920, y: -120, width: 1920, height: 1080 }, videoBoundsSource: 'window-client', dpiScale: 1.5 };
  it('accepts complete versioned snapshots and explicit unknown metadata', () => {
    expect(validPlayer(emptyPlayer)).toBe(true);
    expect(validPlayer(verified)).toBe(true);
    expect(validPlayer({ ...verified, dpiScale: null, windowVisible: false, windowMinimized: true, isForeground: null })).toBe(true);
  });
  it.each([null, {}, { ...verified, state: 'buffering' }, { ...verified, state: 'unknown' }, { ...verified, positionMs: null },
    { ...verified, positionMs: Infinity }, { ...verified, sequence: 1.5 }, { ...verified, mediaRevision: -1 }, { ...verified, title: '' },
    { ...verified, dpiScale: 0 }, { ...verified, windowVisible: 'true' }, { ...verified, videoBoundsPx: { x: 0, y: 0, width: 0, height: 100 } },
    { ...verified, videoBoundsSource: 'unavailable' }])('rejects malformed or contradictory playback proof: %j', value => {
    expect(validPlayer(value)).toBe(false);
  });
  it('rejects arrays as well as undefined, rather than treating test parameter rows as payloads', () => {
    expect(validPlayer([])).toBe(false);
    expect(validPlayer(undefined)).toBe(false);
  });
  it('does not describe conservative freezing as a real pause or play state', () => {
    expect(playbackStatus({ ...emptyPlayer, syncMode: 'stale', state: 'unknown' })).toBe('同步信号已中断');
    expect(playbackStatus({ ...emptyPlayer, syncMode: 'automatic', state: 'unknown' })).toBe('播放状态未确认');
    expect(playbackStatus({ ...emptyPlayer, syncMode: 'automatic', state: 'paused' })).toBe('已自动同步 · 已暂停');
    expect(playbackStatus({ ...emptyPlayer, syncMode: 'automatic', state: 'playing' })).toBe('已自动同步 · 播放中');
    expect(playbackStatus({ ...emptyPlayer, syncMode: 'manual', state: 'playing' })).toBe('手动时间模式');
  });
});
