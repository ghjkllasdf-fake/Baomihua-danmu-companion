import { afterEach, describe, expect, it, vi } from 'vitest';
import { HostCommandError, HostTimeoutError } from './bridge-errors';
import { defaultSettings, type Settings } from './model';
import { SettingsSession, type SettingsView } from './settings-session';

const sessions: SettingsSession[] = [];
const saved = (revision: number, patch: Partial<Settings> = {}) => ({ settings: { ...defaultSettings, ...patch }, settingsRevision: revision });
function fixture(connect = true) {
  vi.useFakeTimers();
  const calls: { type: string; payload: unknown; resolve(value: unknown): void; reject(error: Error): void }[] = [];
  const views: SettingsView[] = [];
  const session = new SettingsSession(defaultSettings, (type, payload) => new Promise((resolve, reject) => calls.push({ type, payload, resolve, reject })), view => views.push(view));
  sessions.push(session);
  if (connect) session.connect(saved(0));
  return { session, calls, views };
}
const flush = () => vi.advanceTimersByTimeAsync(0);
afterEach(() => { sessions.splice(0).forEach(session => session.dispose()); vi.useRealTimers(); });

describe('settings save authority and recovery', () => {
  it('delivers current settings during sustained input instead of waiting for input to stop', async () => {
    const { session, calls } = fixture();
    for (let index = 0; index < 20; index++) {
      session.edit({ fontSize: 28 + index });
      await vi.advanceTimersByTimeAsync(8);
      if (index === 9) expect(calls.length).toBeGreaterThan(0);
    }
    await vi.advanceTimersByTimeAsync(16);
    expect(calls.length).toBeGreaterThan(1);
    expect(calls.at(-1)?.payload).toEqual({ settings: { ...defaultSettings, fontSize: 47 } });
  });
  it('keeps preview local and coalesces real edits into the final complete setting object', async () => {
    const { session, calls } = fixture(false);
    session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(100);
    expect(calls).toEqual([]); expect(session.view().status).toBe('preview');
    session.connect(saved(0)); session.edit({ fontSize: 40 }); session.edit({ speedMultiplier: 2 });
    await vi.advanceTimersByTimeAsync(15); expect(calls).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1); expect(calls).toHaveLength(1);
    expect(calls[0].payload).toEqual({ settings: { ...defaultSettings, fontSize: 40, speedMultiplier: 2 } });
    calls[0].resolve(saved(1, { fontSize: 40, speedMultiplier: 2 })); await flush();
    expect(session.view()).toMatchObject({ status: 'saved', settings: { fontSize: 40, speedMultiplier: 2 }, message: '' });
  });
  it('rolls a confirmed rejection back without pretending it was an uncertain timeout', async () => {
    const { session, calls } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostCommandError('storage failure', 'settings-not-applied')); await flush();
    expect(session.view()).toMatchObject({ status: 'error', settings: { fontSize: defaultSettings.fontSize }, message: 'storage failure' });
    expect(calls).toHaveLength(1);
  });
  it('preserves a newer pending edit while retaining a late success as its rollback baseline', async () => {
    const { session, calls } = fixture(); session.edit({ areaFraction: .25 }); await vi.advanceTimersByTimeAsync(40);
    session.edit({ areaFraction: .75 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].resolve(saved(1, { areaFraction: .25 })); await flush();
    expect(session.view().settings.areaFraction).toBe(.75);
    calls[1].reject(new HostCommandError('latest failed', 'settings-not-applied')); await flush();
    expect(session.view().settings.areaFraction).toBe(.25);
  });
  it('accepts an older success after the latest known failure, without rewinding a newer native revision', async () => {
    const { session, calls } = fixture(); session.edit({ areaFraction: .25 }); await vi.advanceTimersByTimeAsync(40);
    session.edit({ areaFraction: .75 }); await vi.advanceTimersByTimeAsync(40);
    calls[1].reject(new HostCommandError('latest failed', 'settings-not-applied')); await flush();
    calls[0].resolve(saved(1, { areaFraction: .25 })); await flush();
    expect(session.view().settings.areaFraction).toBe(.25);
    session.observe(saved(3, { areaFraction: 1 })); session.observe(saved(2, { areaFraction: .5 }));
    expect(session.view().settings.areaFraction).toBe(1);
  });
  it.each([true, false])('reads authoritative saved settings after a timeout, without resending the write (applied=%s)', async applied => {
    const { session, calls } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostTimeoutError()); await flush();
    expect(calls.map(c => c.type)).toEqual(['update-settings', 'get-settings']);
    expect(session.view()).toMatchObject({ status: 'recovering', settings: { fontSize: 40 } });
    calls[1].resolve(applied ? saved(1, { fontSize: 40 }) : saved(0)); await flush();
    expect(session.view()).toMatchObject({ status: 'saved', settings: { fontSize: applied ? 40 : defaultSettings.fontSize }, message: '' });
  });
  it('keeps failed recovery visibly unknown and retries only a read when asked', async () => {
    const { session, calls } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostTimeoutError()); await flush(); calls[1].reject(new HostTimeoutError()); await flush();
    expect(session.view().status).toBe('unknown'); expect(session.view().message).toContain('无法确认');
    expect(calls).toHaveLength(2);
    session.retryRecovery(); expect(calls.map(c => c.type)).toEqual(['update-settings', 'get-settings', 'get-settings']);
    calls[2].resolve(saved(1, { fontSize: 40 })); await flush(); expect(session.view().status).toBe('saved');
  });
  it('does not replace new user intent or a newer state event with an old recovery reply', async () => {
    const { session, calls } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostTimeoutError()); await flush();
    session.edit({ fontSize: 52 }); await vi.advanceTimersByTimeAsync(40);
    session.observe(saved(2, { fontSize: 52 }));
    calls[1].resolve(saved(1, { fontSize: 40 })); await flush();
    expect(session.view().settings.fontSize).toBe(52);
    calls[2].resolve(saved(2, { fontSize: 52 })); await flush(); expect(session.view().status).toBe('saved');
  });
  it('coalesces simultaneous unknown outcomes behind one read barrier', async () => {
    const { session, calls } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    session.edit({ fontSize: 52 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostTimeoutError()); await flush(); calls[1].reject(new HostTimeoutError()); await flush();
    expect(calls.map(c => c.type)).toEqual(['update-settings', 'update-settings', 'get-settings']);
    calls[2].resolve(saved(2, { fontSize: 52 })); await flush(); expect(session.view().status).toBe('saved');
  });
  it('issues a newer read when an unknown write was dispatched after the old read barrier', async () => {
    const { session, calls } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostTimeoutError()); await flush();
    session.edit({ fontSize: 52 }); await vi.advanceTimersByTimeAsync(40);
    calls[2].reject(new HostCommandError('send failed', 'transport-failed')); await flush();
    calls[1].resolve(saved(1, { fontSize: 40 })); await flush();
    expect(calls.map(c => c.type)).toEqual(['update-settings', 'get-settings', 'update-settings', 'get-settings']);
    calls[3].resolve(saved(2, { fontSize: 52 })); await flush(); expect(session.view()).toMatchObject({ status: 'saved', settings: { fontSize: 52 } });
  });
  it('does not let an obsolete read failure undo a newer confirmed save', async () => {
    const { session, calls, views } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostTimeoutError()); await flush(); session.edit({ fontSize: 52 }); await vi.advanceTimersByTimeAsync(40);
    calls[2].resolve(saved(2, { fontSize: 52 })); await flush();
    expect(views.at(-1)).toMatchObject({ status: 'saved', settings: { fontSize: 52 } });
    calls[1].reject(new HostTimeoutError()); await flush();
    expect(session.view()).toMatchObject({ status: 'saved', settings: { fontSize: 52 }, message: '' });
  });
  it('publishes the settled view when an already-covered write times out after the read barrier', async () => {
    const { session, calls, views } = fixture(); session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    session.edit({ fontSize: 52 }); await vi.advanceTimersByTimeAsync(40);
    calls[0].reject(new HostTimeoutError()); await flush(); calls[2].resolve(saved(2, { fontSize: 52 })); await flush();
    calls[1].reject(new HostTimeoutError()); await flush();
    expect(views.at(-1)).toMatchObject({ status: 'saved', settings: { fontSize: 52 }, message: '' });
    expect(calls).toHaveLength(3);
  });
  it('validates recovery replies and never forwards unexpected response properties as settings', async () => {
    const { session, calls } = fixture(false);
    session.connect({ settings: { ...defaultSettings, unexpected: 'do-not-forward' }, settingsRevision: 0 });
    session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    expect(calls[0].payload).toEqual({ settings: { ...defaultSettings, fontSize: 40 } });
    calls[0].resolve({ settings: null, settingsRevision: 1 }); await flush();
    calls[1].resolve({ settings: { ...defaultSettings, fontSize: NaN }, settingsRevision: 1 }); await flush();
    expect(session.view().status).toBe('unknown');
  });
  it('does not rewind a pre-handshake state or run network work after disposal', async () => {
    const { session, calls, views } = fixture(false); session.observe(saved(2, { fontSize: 52 })); session.connect(saved(0));
    expect(session.view().settings.fontSize).toBe(52);
    session.edit({ fontSize: 40 }); session.dispose(); await vi.advanceTimersByTimeAsync(40);
    expect(calls).toHaveLength(0);
    const next = fixture(); next.session.edit({ fontSize: 40 }); await vi.advanceTimersByTimeAsync(40);
    next.session.dispose(); const before = next.views.length; next.calls[0].reject(new HostTimeoutError()); await flush();
    expect(next.calls).toHaveLength(1); expect(next.views).toHaveLength(before); expect(views.length).toBeGreaterThan(0);
  });
});
