import { useEffect, useRef, useState } from 'react';
import type { useUpdates } from '../use-updates';
import { hasUpdate, updateBusy } from '../updates';
import { UpdateBadge } from './UpdateBadge';

export function UpdatePanel(controller: ReturnType<typeof useUpdates>) {
  const { snapshot: state, error, pending, check, download, cancel, save, install, openRelease } = controller;
  const [confirm, setConfirm] = useState(false);
  const confirmation = useRef<HTMLDivElement>(null);
  useEffect(() => { setConfirm(false); }, [state?.availableVersion, state?.state]);
  function showConfirmation() {
    setConfirm(true);
    requestAnimationFrame(() => confirmation.current?.scrollIntoView({ block: 'center' }));
  }
  const available = hasUpdate(state);
  const summaryText = error ? '需重试' : !state ? '未连接' : {
    unavailable: '不可用', idle: state.currentVersion, checking: '检查中', current: state.currentVersion,
    available: state.availableVersion, downloading: '下载中', verifying: '校验中', ready: '待重启', applying: '安装中', failed: '需重试',
  }[state.state];
  const heading = <summary className="update-summary" aria-label={available ? `软件更新，有新版本 ${state?.availableVersion}` : '软件更新'}>
    <span id="update-heading" className={`update-title update-label${available ? ' has-update' : ''}`}>软件更新{available && <UpdateBadge />}</span>
    <span className="update-summary-state" aria-hidden="true">{summaryText}</span>
    <svg className="update-disclosure" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false"><path d="m4 2.5 3.5 3.5-3.5 3.5" /></svg>
  </summary>;
  if (!state) return <details className="update-panel" aria-labelledby="update-heading">{heading}<p className="update-muted">当前宿主不提供自动更新，或尚未连接桌面程序。</p></details>;
  const busy = pending || updateBusy(state.state), prefsDisabled = !state.supported || busy;
  const phaseText = { unavailable: '此运行方式不支持自动更新', idle: '尚未检查更新', checking: '正在检查 GitHub 版本…', current: '当前已是最新可用版本',
    available: `发现新版本 ${state.availableVersion}`, downloading: '正在下载更新…', verifying: '正在校验更新文件…', ready: '更新已准备好', applying: '正在准备退出并安装更新…', failed: '更新未完成，请重试' }[state.state];
  const percent = state.totalBytes > 0 ? Math.min(100, Math.floor(state.downloadedBytes / state.totalBytes * 100)) : null;
  return <details className="update-panel" aria-labelledby="update-heading" onToggle={event => {
    const panel = event.currentTarget;
    if (!panel.open) setConfirm(false);
    else requestAnimationFrame(() => { if (panel.open) panel.scrollIntoView({ block: 'start' }); });
  }}>
    {heading}
    <div className="update-body">
    <div className="update-build"><span>当前版本 <span className="update-version">{state.currentVersion || '开发版本'}</span></span><span>{state.variant === 'includeAPI' ? '内置 API 版' : '普通版'} · {state.distribution === 'installer' ? '安装版' : '便携版'}</span></div>
    <div className="update-preferences">
      <label className="update-toggle"><span>自动检查更新</span><input type="checkbox" role="switch" checked={state.autoCheck} disabled={prefsDisabled} onChange={event => void save({ autoCheck: event.target.checked, autoDownload: state.autoDownload, channel: state.channel })} /></label>
      <label className="update-toggle"><span>自动下载更新</span><input type="checkbox" role="switch" checked={state.autoDownload} disabled={prefsDisabled} onChange={event => void save({ autoCheck: state.autoCheck, autoDownload: event.target.checked, channel: state.channel })} /></label>
      <label className="update-channel"><span>更新通道</span><select value={state.channel} disabled={prefsDisabled} onChange={event => void save({ autoCheck: state.autoCheck, autoDownload: state.autoDownload, channel: event.target.value === 'stable' ? 'stable' : 'beta' })}><option value="stable">正式版</option><option value="beta">Beta 与正式版</option></select></label>
    </div>
    <p className="update-phase" role="status">{phaseText}</p>
    {(state.state === 'downloading' || state.state === 'verifying') && <div className="update-progress"><progress max="100" value={percent ?? undefined} aria-label="更新下载进度" /><span>{percent === null ? '大小未知' : `${percent}%`}</span></div>}
    {state.checkedAt && <p className="update-muted">上次检查：{new Date(state.checkedAt).toLocaleString('zh-CN')}</p>}
    {(error || state.message) && <p className="update-message" role={error || state.state === 'failed' ? 'alert' : 'status'}>{error || state.message}</p>}
    {state.lastResult && <p className="update-muted">{state.lastResult}</p>}
    {state.notes && <details className="update-notes"><summary>更新内容</summary><pre>{state.notes}</pre></details>}
    <div className="update-actions">
      <button type="button" className="secondary" disabled={!state.supported || busy} onClick={() => void check()}>检查更新</button>
      {state.availableVersion && ['available', 'failed'].includes(state.state) && <button type="button" className="primary" disabled={busy} onClick={() => void download()}>下载更新</button>}
      {state.state === 'ready' && <button type="button" className="primary" disabled={!state.canInstall || busy} onClick={showConfirmation}>重启并更新</button>}
      {['checking', 'downloading', 'verifying'].includes(state.state) && <button type="button" className="text-button" onClick={() => void cancel()}>取消</button>}
      <button type="button" className="text-button" disabled={busy} onClick={() => void openRelease()}>打开 GitHub Release</button>
    </div>
    {confirm && state.state === 'ready' && <div ref={confirmation} className="update-confirm" role="group" aria-label="确认更新安装"><p>更新至 {state.availableVersion}？弹幕伴侣会退出并重新启动，播放器不受控制。</p><p className="update-muted">若更新中断，可从程序目录旁的 .DanmuCompanion-updates 文件夹打开 Recover-Danmu 恢复快捷方式。</p><div className="update-actions"><button type="button" className="primary" disabled={busy || !state.canInstall} onClick={() => void install(state.availableVersion)}>确认重启并更新</button><button type="button" className="text-button" onClick={() => setConfirm(false)}>暂不更新</button></div></div>}
    </div>
  </details>;
}
