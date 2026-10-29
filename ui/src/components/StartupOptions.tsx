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
  return <section className="startup-options" aria-labelledby="startup-heading">
    <h2 id="startup-heading">启动与托盘</h2>
    <label className="toggle-row"><span>开机自动启动<small>默认关闭；登录 Windows 后驻留托盘</small></span><input type="checkbox" role="switch" aria-label="开机自动启动" checked={available && enabled} disabled={!available || busy || !ready} onChange={e => void change(e.target.checked)} /></label>
    <p className="muted">点击托盘图标打开工作台，关闭窗口后仍在后台运行。右键托盘可退出；悬停查看剧集、来源和弹幕数。</p>
    <p className="muted">从旧预览版升级且曾开启自启动：请先关闭上方开关，再按需开启，即可将旧 PowerShell 启动项迁移为本版本 EXE；未操作时旧启动项保持不变。</p>
    {!available && <p className="muted">连接支持此功能的桌面宿主后可设置。</p>}
    {error && available && <p className="error" role="alert">{error} <button className="text-button" disabled={busy} onClick={() => setRetry(value => value + 1)}>重新读取状态</button></p>}
    {busy && available && <p className="muted" role="status">正在确认开机启动设置…</p>}
  </section>;
}
