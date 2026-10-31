import { useEffect, useState } from 'react';
import type { useUpdates } from '../use-updates';
import { updateBusy } from '../updates';

export function UpdatePanel(controller: ReturnType<typeof useUpdates>) {
  const { snapshot: state, error, pending, check, download, cancel, save, install, openRelease } = controller;
  const [confirm, setConfirm] = useState(false);
  useEffect(() => { setConfirm(false); }, [state?.availableVersion, state?.state]);
  if (!state) return <section className="update-panel" aria-label="软件更新"><h3>软件更新</h3><p className="update-muted">当前宿主不提供自动更新，或尚未连接桌面程序。</p></section>;
  const busy = pending || updateBusy(state.state), prefsDisabled = !state.supported || busy;
  const phaseText = { unavailable: '此运行方式不支持自动更新', idle: '尚未检查更新', checking: '正在检查 GitHub 版本…', current: '当前已是最新可用版本',
    available: `发现新版本 ${state.availableVersion}`, downloading: '正在下载更新…', verifying: '正在校验更新文件…', ready: '更新已准备好', applying: '正在准备退出并安装更新…', failed: '更新未完成，请重试' }[state.state];
  const percent = state.totalBytes > 0 ? Math.min(100, Math.floor(state.downloadedBytes / state.totalBytes * 100)) : null;
  return <section className="update-panel" aria-labelledby="update-heading">
    <div className="update-heading"><h3 id="update-heading">软件更新</h3><span className="update-version">{state.currentVersion || '开发版本'}</span></div>
    <p className="update-muted">{state.variant === 'includeAPI' ? '内置 API 版' : '普通版'} · {state.distribution === 'installer' ? '安装版' : '便携版'} · 来源 GitHub</p>
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
      {state.state === 'ready' && <button type="button" className="primary" disabled={!state.canInstall || busy} onClick={() => setConfirm(true)}>重启并更新</button>}
      {['checking', 'downloading', 'verifying'].includes(state.state) && <button type="button" className="text-button" onClick={() => void cancel()}>取消</button>}
      <button type="button" className="text-button" disabled={busy} onClick={() => void openRelease()}>打开 GitHub Release</button>
    </div>
    {confirm && state.state === 'ready' && <div className="update-confirm" role="group" aria-label="确认更新安装"><p>更新至 {state.availableVersion}？弹幕伴侣会退出并重新启动，播放器不受控制。</p><p className="update-muted">若更新中断，可从程序目录旁的 .DanmuCompanion-updates 文件夹打开 Recover-Danmu 恢复快捷方式。</p><div className="update-actions"><button type="button" className="primary" disabled={busy || !state.canInstall} onClick={() => void install(state.availableVersion)}>确认重启并更新</button><button type="button" className="text-button" onClick={() => setConfirm(false)}>暂不更新</button></div></div>}
  </section>;
}
