import type { useUpdates } from '../use-updates';
import { hasUpdate, updateBusy, updateCheckIntervals } from '../updates';
import { UpdateBadge } from './UpdateBadge';

export function UpdatePanel(controller: ReturnType<typeof useUpdates>) {
  const { snapshot: state, error, pending, opening, check, cancel, save, openRelease } = controller;
  const available = hasUpdate(state);
  const summaryText = error ? '需重试' : !state ? '未连接' : {
    unavailable: '不可用', idle: state.currentVersion, checking: '检查中', current: state.currentVersion, available: state.availableVersion, failed: '需重试',
  }[state.state];
  const heading = <summary className="update-summary" aria-label={available ? `软件更新，有新版本 ${state?.availableVersion}` : '软件更新'}>
    <span id="update-heading" className={`update-title update-label${available ? ' has-update' : ''}`}>软件更新{available && <UpdateBadge />}</span>
    <span className="update-summary-state" aria-hidden="true">{summaryText}</span>
    <svg className="update-disclosure" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false"><path d="m4 2.5 3.5 3.5-3.5 3.5" /></svg>
  </summary>;
  if (!state) return <details className="update-panel" aria-labelledby="update-heading">{heading}<p className="update-muted" role={error ? 'alert' : undefined}>{error || '当前宿主不支持版本通知，或尚未连接桌面程序。'}</p></details>;
  const busy = pending || updateBusy(state.state), prefsDisabled = !state.supported || busy;
  const preferences = { autoCheck: state.autoCheck, channel: state.channel, checkIntervalHours: state.checkIntervalHours };
  const phaseText = { unavailable: '此运行方式不支持版本检查', idle: '尚未检查新版本', checking: '正在检查 GitHub 新版本…', current: '当前已是最新可用版本',
    available: `发现新版本 ${state.availableVersion}`, failed: '版本检查未完成，请重试' }[state.state];
  return <details className="update-panel" aria-labelledby="update-heading" onToggle={event => {
    const panel = event.currentTarget;
    if (panel.open) requestAnimationFrame(() => { if (panel.open) panel.scrollIntoView({ block: 'start' }); });
  }}>
    {heading}
    <div className="update-body">
      <div className="update-build"><span>当前版本 <span className="update-version">{state.currentVersion || '开发版本'}</span></span><span>{state.variant === 'includeAPI' ? '内置 API 版' : '普通版'} · {state.distribution === 'installer' ? '安装版' : '便携版'}</span></div>
      <div className="update-preferences">
        <label className="update-toggle"><span>自动检查更新</span><input type="checkbox" role="switch" checked={state.autoCheck} disabled={prefsDisabled} onChange={event => void save({ ...preferences, autoCheck: event.target.checked })} /></label>
        <label className="update-channel"><span>检查频率</span><select value={state.checkIntervalHours} disabled={prefsDisabled || !state.autoCheck} onChange={event => {
          const interval = updateCheckIntervals.find(value => String(value) === event.target.value);
          if (interval !== undefined) void save({ ...preferences, checkIntervalHours: interval });
        }}>{updateCheckIntervals.map(interval => <option key={interval} value={interval}>{{ 24: '每天', 168: '每周', 720: '每月' }[interval]}</option>)}</select></label>
        {state.checkIntervalHours === 720 && <p className="update-muted">每月按 30 天检查一次。</p>}
        <label className="update-channel"><span>更新通道</span><select value={state.channel} disabled={prefsDisabled} onChange={event => void save({ ...preferences, channel: event.target.value === 'stable' ? 'stable' : 'beta' })}><option value="stable">正式版</option><option value="beta">Beta 与正式版</option></select></label>
      </div>
      <p className="update-phase" role="status">{phaseText}</p>
      {state.checkedAt && <p className="update-muted">上次检查：{new Date(state.checkedAt).toLocaleString('zh-CN')}</p>}
      {(error || state.message) && <p className="update-message" role={error || state.state === 'failed' ? 'alert' : 'status'}>{error || state.message}</p>}
      <p className="update-muted">程序仅检查版本；请自行下载并安装或解压。更新前请退出弹幕伴侣，个人设置无需清除。</p>
      <div className="update-actions">
        <button type="button" className="primary" disabled={opening} aria-busy={opening} onClick={() => void openRelease()}>查看 Release</button>
        <button type="button" className="secondary" disabled={!state.supported || busy} onClick={() => void check()}>检查更新</button>
        {state.state === 'checking' && <button type="button" className="text-button" onClick={() => void cancel()}>取消检查</button>}
      </div>
      {state.notes && <details className="update-notes"><summary>更新内容</summary><pre>{state.notes}</pre></details>}
    </div>
  </details>;
}
