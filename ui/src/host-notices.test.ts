import { describe, expect, it } from 'vitest';
import { createNativeNoticeHandler } from './host-notices';

describe('host notice recovery', () => {
  it('ignores malformed and repeated notices', () => {
    let error = '', updates = 0;
    const notice = createNativeNoticeHandler(update => { error = update(error); updates++; });
    notice(undefined); notice(null); notice({}); notice('');
    expect(updates).toBe(0);
    notice('warning'); notice('warning');
    expect(error).toBe('warning'); expect(updates).toBe(1);
  });
  it('clears a repaired host warning', () => {
    let error = '';
    const notice = createNativeNoticeHandler(update => { error = update(error); });
    notice('warning'); notice('');
    expect(error).toBe('');
  });
  it('does not clear an unrelated error when the host warning disappears', () => {
    let error = '';
    const notice = createNativeNoticeHandler(update => { error = update(error); });
    notice('warning'); error = 'manual clock failed'; notice('');
    expect(error).toBe('manual clock failed');
  });
  it('can show the same warning again after it was resolved', () => {
    let error = '', updates = 0;
    const notice = createNativeNoticeHandler(update => { error = update(error); updates++; });
    notice('warning'); notice(''); notice('warning');
    expect(error).toBe('warning'); expect(updates).toBe(3);
  });
  it('keeps a dismissed warning quiet until its native state changes', () => {
    let error = '';
    const notice = createNativeNoticeHandler(update => { error = update(error); });
    notice('warning'); error = ''; notice('warning');
    expect(error).toBe('');
  });
  it('captures each transition for queued functional state updates', () => {
    const queued: ((current: string) => string)[] = [];
    const notice = createNativeNoticeHandler(update => queued.push(update));
    notice('warning'); notice('');
    expect(queued.reduce((error, update) => update(error), '')).toBe('');
  });
});
