import { useEffect, useRef, useState } from 'react';
import { request, subscribe, subscribeConnection } from './bridge';
import { hasUpdate, validUpdateSnapshot, type UpdatePreferences, type UpdateSnapshot } from './updates';

export function useUpdates() {
  const [snapshot, setSnapshot] = useState<UpdateSnapshot | null>(null), [error, setError] = useState(''), [pending, setPending] = useState(false), [opening, setOpening] = useState(false);
  const active = useRef(true), epoch = useRef(0), latest = useRef<UpdateSnapshot | null>(null), requestPending = useRef(false), openPending = useRef(false), capable = useRef(false);
  function observe(value: unknown) {
    if (!validUpdateSnapshot(value)) { setSnapshot(null); setError('版本检查状态数据无效，请重新打开面板。'); return; }
    if (latest.current && value.revision < latest.current.revision) return;
    if (!latest.current || value.revision > latest.current.revision) setError('');
    latest.current = value; setSnapshot(value);
  }
  useEffect(() => {
    active.current = true;
    const connection = subscribeConnection(value => {
      epoch.current++; latest.current = null; setSnapshot(null); setError(''); requestPending.current = false; setPending(false); openPending.current = false; setOpening(false);
      capable.current = value.status === 'ready' && value.hello.capabilities.includes('github-release-notice-v1');
      if (value.status === 'ready' && capable.current) observe(value.hello.update);
    });
    const state = subscribe(value => {
      if (!capable.current || !value || typeof value !== 'object' || Array.isArray(value)) return;
      const update = (value as Record<string, unknown>).update;
      if (update !== undefined) observe(update);
    });
    return () => { active.current = false; epoch.current++; connection(); state(); };
  }, []);
  async function run(action: () => Promise<unknown>) {
    if (requestPending.current) return;
    requestPending.current = true; setPending(true); setError(''); const ticket = epoch.current;
    try { const result = await action(); if (active.current && ticket === epoch.current) observe(result); }
    catch (cause) { if (active.current && ticket === epoch.current) setError(cause instanceof Error ? cause.message : '版本检查未完成，请重试。'); }
    finally { if (active.current && ticket === epoch.current) { requestPending.current = false; setPending(false); } }
  }
  return { snapshot, error, pending, opening, available: hasUpdate(snapshot),
    check: () => run(() => request('check-update')),
    cancel: async () => {
      const ticket = epoch.current;
      try { const result = await request('cancel-update'); if (active.current && ticket === epoch.current) observe(result); }
      catch (cause) { if (active.current && ticket === epoch.current) setError(cause instanceof Error ? cause.message : '取消检查未确认。'); }
    },
    save: (preferences: UpdatePreferences) => run(() => request('set-update-preferences', preferences)),
    openRelease: async () => {
      if (openPending.current || !snapshot) return;
      openPending.current = true; setOpening(true); setError(''); const ticket = epoch.current;
      try { await request('open-update-page'); }
      catch (cause) { if (active.current && ticket === epoch.current) setError(cause instanceof Error ? cause.message : '未能打开 Release 页面，请重试。'); }
      finally { if (active.current && ticket === epoch.current) { openPending.current = false; setOpening(false); } }
    } };
}
