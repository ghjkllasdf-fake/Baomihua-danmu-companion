import { useEffect, useRef, useState } from 'react';
import { request, subscribe, subscribeConnection } from './bridge';
import { hasUpdate, validUpdateSnapshot, type UpdatePreferences, type UpdateSnapshot } from './updates';

export function useUpdates() {
  const [snapshot, setSnapshot] = useState<UpdateSnapshot | null>(null), [error, setError] = useState(''), [pending, setPending] = useState(false);
  const active = useRef(true), epoch = useRef(0), latest = useRef<UpdateSnapshot | null>(null), requestPending = useRef(false);
  useEffect(() => {
    active.current = true;
    function observe(value: unknown) {
      if (!validUpdateSnapshot(value)) { setError('更新状态数据无效，请重新打开面板。'); return; }
      if (latest.current && value.revision < latest.current.revision) return;
      latest.current = value; setSnapshot(value);
    }
    const connection = subscribeConnection(value => {
      epoch.current++; latest.current = null; setSnapshot(null); setError(''); requestPending.current = false; setPending(false);
      if (value.status === 'ready' && value.hello.capabilities.includes('github-update-v1')) observe(value.hello.update);
    });
    const state = subscribe(value => {
      if (!value || typeof value !== 'object' || Array.isArray(value)) return;
      const update = (value as Record<string, unknown>).update;
      if (update !== undefined && latest.current) observe(update);
    });
    return () => { active.current = false; epoch.current++; connection(); state(); };
  }, []);
  async function run(action: () => Promise<unknown>) {
    if (requestPending.current) return;
    requestPending.current = true; setPending(true); setError(''); const ticket = epoch.current;
    try {
      const result = await action();
      if (!active.current || ticket !== epoch.current) return;
      if (validUpdateSnapshot(result)) {
        if (!latest.current || result.revision >= latest.current.revision) { latest.current = result; setSnapshot(result); }
      }
    } catch (cause) { if (active.current && ticket === epoch.current) setError(cause instanceof Error ? cause.message : '更新操作未确认，请重试。'); }
    finally { if (active.current && ticket === epoch.current) { requestPending.current = false; setPending(false); } }
  }
  return { snapshot, error, pending, available: hasUpdate(snapshot),
    check: () => run(() => request('check-update')), download: () => run(() => request('download-update')),
    cancel: async () => { const ticket = epoch.current; try { await request('cancel-update'); } catch (cause) { if (active.current && ticket === epoch.current) setError(cause instanceof Error ? cause.message : '取消未确认。'); } },
    save: (preferences: UpdatePreferences) => run(() => request('set-update-preferences', preferences)),
    install: (version: string) => run(() => request('install-update', { version })),
    openRelease: () => run(() => request('open-update-page')) };
}
