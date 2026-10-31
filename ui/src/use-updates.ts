import { useEffect, useRef, useState } from 'react';
import { request, subscribe, subscribeConnection } from './bridge';
import { HostCommandError } from './bridge-errors';
import { hasUpdate, validUpdateSnapshot, type UpdatePreferences, type UpdateSnapshot } from './updates';

export function useUpdates() {
  const [snapshot, setSnapshot] = useState<UpdateSnapshot | null>(null), [error, setError] = useState(''), [pending, setPending] = useState(false);
  const [portableSetupSupported, setPortableSetupSupported] = useState(false);
  const setupCapability = useRef(false);
  const setupSnapshotValid = useRef(false);
  const active = useRef(true), epoch = useRef(0), latest = useRef<UpdateSnapshot | null>(null), requestPending = useRef(false);
  useEffect(() => {
    active.current = true;
    function observe(value: unknown) {
      if (!validUpdateSnapshot(value)) { setupSnapshotValid.current = false; setError('更新状态数据无效，请重新打开面板。'); return; }
      if (latest.current && value.revision < latest.current.revision) return;
      setupSnapshotValid.current = true;
      // Fresh native state can confirm progress after an uncertain command reply.
      if ((!latest.current || value.revision > latest.current.revision) && value.portableSetupState && value.portableSetupState !== 'none') setError('');
      latest.current = value; setSnapshot(value);
    }
    const connection = subscribeConnection(value => {
      epoch.current++; latest.current = null; setSnapshot(null); setError(''); requestPending.current = false; setPending(false);
      setupSnapshotValid.current = false;
      setupCapability.current = value.status === 'ready' && value.hello.capabilities.includes('github-update-v1') && value.hello.capabilities.includes('portable-setup-v1');
      setPortableSetupSupported(setupCapability.current);
      if (value.status === 'ready' && value.hello.capabilities.includes('github-update-v1')) observe(value.hello.update);
    });
    const state = subscribe(value => {
      if (!value || typeof value !== 'object' || Array.isArray(value)) return;
      const update = (value as Record<string, unknown>).update;
      if (update !== undefined && latest.current) observe(update);
    });
    return () => { active.current = false; epoch.current++; connection(); state(); };
  }, []);
  async function run(action: () => Promise<unknown>, portableSetup = false) {
    if (requestPending.current) return;
    requestPending.current = true; setPending(true); setError(''); const ticket = epoch.current;
    const startingRevision = latest.current?.revision ?? -1;
    try {
      const result = await action();
      if (!active.current || ticket !== epoch.current) return;
      if (validUpdateSnapshot(result)) {
        if (!latest.current || result.revision >= latest.current.revision) { setupSnapshotValid.current = true; latest.current = result; setSnapshot(result); }
      }
    } catch (cause) {
      if (active.current && ticket === epoch.current) {
        if (portableSetup && latest.current?.portableSetupState === 'prepared' && latest.current.revision > startingRevision) return;
        if (portableSetup && cause instanceof HostCommandError && ['timeout', 'invalid-response', 'transport-failed'].includes(cause.code)) setupSnapshotValid.current = false;
        setError(cause instanceof Error ? cause.message : '更新操作未确认，请重试。');
      }
    }
    finally { if (active.current && ticket === epoch.current) { requestPending.current = false; setPending(false); } }
  }
  return { snapshot, error, pending, portableSetupSupported, portableSetupSnapshotValid: setupSnapshotValid.current, available: hasUpdate(snapshot),
    preparePortable: () => {
      if (!setupCapability.current || !setupSnapshotValid.current || latest.current?.distribution !== 'portable' || latest.current.portableSetupState !== 'required') return Promise.resolve();
      return run(() => request('prepare-portable', {}), true);
    },
    check: () => run(() => request('check-update')), download: () => run(() => request('download-update')),
    cancel: async () => { const ticket = epoch.current; try { await request('cancel-update'); } catch (cause) { if (active.current && ticket === epoch.current) setError(cause instanceof Error ? cause.message : '取消未确认。'); } },
    save: (preferences: UpdatePreferences) => run(() => request('set-update-preferences', preferences)),
    install: (version: string) => run(() => request('install-update', { version })),
    openRelease: () => run(() => request('open-update-page')) };
}
