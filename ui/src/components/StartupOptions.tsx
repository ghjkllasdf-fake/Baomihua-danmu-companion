import { useEffect, useRef, useState } from 'react';
import { request } from '../bridge';

export function StartupOptions({ available }: { available: boolean }) {
  const [enabled, setEnabled] = useState(false), [busy, setBusy] = useState(false), [ready, setReady] = useState(false);
  const [error, setError] = useState(''), [retry, setRetry] = useState(0);
  const epoch = useRef(0);
  useEffect(() => {
    const current = ++epoch.current;
    if (!available) return;
    let active = true;
    setBusy(true); setReady(false); setError('');
    void request('get-startup').then(value => {
      if (active) { setEnabled(value.enabled); setReady(true); }
    }).catch(() => {
      if (active) setError('无法读取开机启动状态，请重试。');
    }).finally(() => { if (active) setBusy(false); });
    return () => { active = false; if (epoch.current === current) epoch.current++; };
  }, [available, retry]);
  async function change(next: boolean) {
    if (!available || busy || !ready) return;
    const current = epoch.current;
    setBusy(true); setError('');
    try {
      const result = await request('set-startup', { enabled: next });
      if (current === epoch.current) setEnabled(result.enabled);
    } catch {
      if (current === epoch.current) { setReady(false); setError('开机启动结果未确认，请重新读取状态。'); }
    } finally { if (current === epoch.current) setBusy(false); }
  }
  return <section className="startup-options compact-startup" aria-label="开机启动设置">
    <label className="toggle-row"><span>开机启动</span><input type="checkbox" role="switch" aria-label="开机自动启动" checked={available && enabled} disabled={!available || busy || !ready} onChange={e => void change(e.target.checked)} /></label>
    {error && available && <p className="error" role="alert">{error} <button className="text-button" disabled={busy} onClick={() => setRetry(value => value + 1)}>重新读取状态</button></p>}
    {busy && available && <p className="sr-only" role="status">正在确认开机启动设置…</p>}
  </section>;
}
