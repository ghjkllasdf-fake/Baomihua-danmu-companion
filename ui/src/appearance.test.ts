import { describe, expect, it } from 'vitest';
import { defaultAppearance, validAppearance, validAppearanceSnapshot } from './appearance';
import { defaultSettings, validHello } from './model';
import { validPayload, validResult } from './protocol';

describe('isolated appearance contract', () => {
  it('keeps A as default and accepts only the four bundled skins', () => {
    expect(defaultAppearance).toEqual({ skin: 'fluent', reduceTransparency: false });
    for (const skin of ['fluent', 'glass', 'winui', 'studio']) expect(validAppearance({ skin, reduceTransparency: false })).toBe(true);
    for (const skin of ['', 'a', 'external', '../../style.css', null]) expect(validAppearance({ skin, reduceTransparency: false })).toBe(false);
    expect(validAppearance({ skin: 'glass', reduceTransparency: 'false' })).toBe(false);
    expect(validAppearance({ ...defaultAppearance, apiBase: 'must-not-enter-appearance' })).toBe(false);
    expect(validAppearance({ skin: 'fluent' })).toBe(false);
  });
  it('validates material, revision and generic notices without accepting arbitrary configuration', () => {
    const snapshot = { ...defaultAppearance, revision: 0, backdrop: 'solid', notice: '' };
    expect(validAppearanceSnapshot(snapshot)).toBe(true);
    expect(validAppearanceSnapshot({ ...snapshot, backdrop: 'acrylic' })).toBe(true);
    for (const value of [{ ...snapshot, backdrop: 'url(file:)' }, { ...snapshot, revision: -1 }, { ...snapshot, revision: .1 }, { ...snapshot, notice: 'x'.repeat(501) }, { ...snapshot, path: 'external' }]) expect(validAppearanceSnapshot(value)).toBe(false);
    expect(validPayload('get-appearance', {})).toBe(true);
    expect(validPayload('get-appearance', { path: 'external' })).toBe(false);
    expect(validPayload('set-appearance', defaultAppearance)).toBe(true);
    expect(validPayload('set-appearance', { ...defaultAppearance, css: 'external' })).toBe(false);
    expect(validResult('set-appearance', snapshot)).toBe(true);
    expect(validResult('get-appearance', defaultAppearance)).toBe(false);
  });
  it('accepts old hosts but requires a complete snapshot when appearance capability is advertised', () => {
    const hello = { settings: defaultSettings, settingsRevision: 0, stateSequence: 0, protocolVersion: 1, capabilities: ['media-context-v1'], apiBase: '' };
    expect(validHello(hello)).toBe(true);
    expect(validHello({ ...hello, capabilities: [...hello.capabilities, 'appearance-v1'] })).toBe(false);
    expect(validHello({ ...hello, capabilities: [...hello.capabilities, 'appearance-v1'], appearance: { ...defaultAppearance, revision: 0, backdrop: 'solid', notice: '' } })).toBe(true);
  });
});
