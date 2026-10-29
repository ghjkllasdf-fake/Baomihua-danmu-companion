import { useEffect, useRef, useState } from 'react';
import { inDesktop, request, revokeConnection, subscribe, subscribeConnection, type Connection } from './bridge';
import { defaultSettings, emptyPlayer, formatTime, parseTime, playbackStatus, validPlayer, type Settings } from './model';
import { SettingsSession, settingsStatusText, type SettingsStatus } from './settings-session';
import { createNativeNoticeHandler } from './host-notices';
import { initialPlayback, observePlayer } from './player-observation';
import { DisplaySettings } from './components/DisplaySettings';
import { StartupOptions } from './components/StartupOptions';
import { Preview } from './components/Preview';
import { SourcePanel } from './components/SourcePanel';
import { validAutomaticMatch, type AutomaticMatchStatus } from './automatic-match';

export function App() {
  const [settings, setSettings] = useState(defaultSettings), [playback, setPlayback] = useState(initialPlayback);
  const player = playback.player;
  const [connectionStatus, setConnectionStatus] = useState<Connection['status']>('checking'), [apiBase, setApiBase] = useState('');
  const connected = connectionStatus === 'ready';
  const [connectionError, setConnectionError] = useState('');
  const [automaticMatching, setAutomaticMatching] = useState(false), [automaticMatch, setAutomaticMatch] = useState<AutomaticMatchStatus>();
  const [desktopLifecycle, setDesktopLifecycle] = useState(false);
  const [count, setCount] = useState(0), [error, setError] = useState(''), [manual, setManual] = useState('00:00:00');
  const [settingsStatus, setSettingsStatus] = useState<SettingsStatus>('preview'), [settingsError, setSettingsError] = useState('');
  const settingsSession = useRef<SettingsSession | null>(null);
  const latestPlayback = useRef(initialPlayback);
  useEffect(() => {
    let active = true, trusted = false;
    const session = new SettingsSession(defaultSettings, (type, payload) => type === 'get-settings' ? request('get-settings') :
      payload ? request('update-settings', payload) : Promise.reject(new Error('缺少完整显示设置。')), view => {
      if (!active) return;
      setSettings(view.settings); setSettingsStatus(view.status); setSettingsError(view.message);
    });
    settingsSession.current = session;
    const nativeNotice = createNativeNoticeHandler(setError);
    const stopConnection = subscribeConnection(connection => {
      if (!active) return;
      trusted = false; setConnectionStatus('checking');
      if (connection.status === 'ready') {
        try {
          session.connect(connection.hello); session.retryRecovery(); trusted = true;
          setConnectionStatus('ready'); setApiBase(connection.hello.apiBase); setConnectionError('');
          setAutomaticMatching(connection.hello.capabilities.includes('automatic-match-v1'));
          setDesktopLifecycle(connection.hello.capabilities.includes('desktop-lifecycle-v1'));
        } catch { revokeConnection('宿主显示设置不一致，请重新确认连接。'); }
      } else {
        setConnectionStatus(connection.status);
        setAutomaticMatching(false); setAutomaticMatch(undefined);
        setDesktopLifecycle(false);
        latestPlayback.current = observePlayer(latestPlayback.current, emptyPlayer); setPlayback(latestPlayback.current); setCount(0);
        setConnectionError(connection.status === 'failed' ? connection.message : '');
      }
    });
    const stop = subscribe(data => {
      if (!active || !trusted || !data || typeof data !== 'object') return;
      const state = data as { player?: unknown; commentCount?: unknown; notice?: unknown; settings?: unknown; automaticMatch?: unknown };
      if (state.settings !== undefined) session.observe(data);
      if (state.player !== undefined) {
        const incoming = state.player, valid = validPlayer(incoming);
        latestPlayback.current = observePlayer(latestPlayback.current, valid ? incoming : emptyPlayer);
        setPlayback(latestPlayback.current);
        if (!valid) setError('播放器状态消息无效，已停止显示自动同步。');
      }
      if (typeof state.commentCount === 'number' && Number.isSafeInteger(state.commentCount) && state.commentCount >= 0) setCount(state.commentCount);
      nativeNotice(state.notice);
      if (validAutomaticMatch(state.automaticMatch)) setAutomaticMatch(state.automaticMatch);
    });
    // Connection notifications handle every handshake, including rejection and later recovery.
    if (inDesktop) void request('hello').catch(() => undefined);
    return () => { active = false; stopConnection(); stop(); session.dispose(); if (settingsSession.current === session) settingsSession.current = null; };
  }, []);
  function update(patch: Partial<Settings>) {
    settingsSession.current?.edit(patch);
  }
  async function manualClock(playing: boolean) {
    const positionMs = parseTime(manual);
    if (positionMs === null) { setError('时间格式为 时:分:秒 或 分:秒。'); return; }
    try { await request('manual-clock', { positionMs, playing }); setError(''); }
    catch (e) { setError(e instanceof Error ? e.message : '校时失败。'); }
  }
  const auto = player.syncMode === 'automatic';
  const shownError = connectionError || settingsError || error;
  return <div className="app-shell">
    <header className="app-header"><a className="brand" href="#main" aria-label="爆米花弹幕工作台"><span className="brand-mark"><img src="./favicon.svg" alt="" aria-hidden="true" /></span><span><strong>爆米花</strong><small>POPCORN / DANMU</small></span></a>
      <span className="edition">给好故事，加一点共鸣。 <span>01 / DESKTOP</span></span>
      <label className="master-switch"><span>显示弹幕</span><input type="checkbox" role="switch" checked={settings.enabled} onChange={e => update({ enabled: e.target.checked })} /></label>
    </header>
    <section className="playback-strip" aria-label="播放器连接状态"><span className={`connection-light ${auto ? 'online' : ''}`} /><div className="playback-title"><span className="eyebrow">NOW WATCHING</span><strong>{player.title || '等待爆米花开始播放'}</strong><small>{player.detail || (player.title ? (player.episodeNumber ? `第 ${player.episodeNumber} 集` : '已识别播放窗口') : '打开爆米花视频窗口，弹幕会跟随它。')}</small></div>
      <div className="sync-status"><span>{playbackStatus(player)}</span><strong className="timecode">{player.positionMs === null ? '--:--:--' : formatTime(player.positionMs)}</strong></div>
    </section>
    {!inDesktop && <div className="preview-banner" role="status">浏览器预览模式：可调整演示外观，尚未连接桌面宿主或播放器。</div>}
    {shownError && <div className="app-error" role="alert">{shownError}<button aria-label="关闭提示" onClick={() => { setConnectionError(''); setError(''); settingsSession.current?.dismissMessage(); }}>×</button></div>}
    <h1 className="sr-only">爆米花弹幕工作台</h1>
    <main id="main" className="workspace"><SourcePanel apiBase={apiBase} connected={connected} playback={playback} getPlayback={() => latestPlayback.current} onLoaded={setCount} automaticMatching={automaticMatching} automaticStatus={automaticMatch} />
      <div className="main-controls"><Preview settings={settings} /><DisplaySettings settings={settings} update={update} /><StartupOptions available={connected && desktopLifecycle} />
        <details className="manual-sync"><summary>手动时间校准 <span>{auto ? '自动同步中，无需手动校准' : '播放器未公开时间时使用'}</span></summary><div className="manual-controls"><label htmlFor="manual-time">视频当前时间</label><input id="manual-time" value={manual} onChange={e => setManual(e.target.value)} disabled={auto} /><button className="secondary" disabled={!connected || auto} onClick={() => void manualClock(true)}>校时并播放</button><button className="text-button" disabled={!connected || auto} onClick={() => void manualClock(false)}>校时并暂停</button></div></details>
      </div>
    </main>
    <footer className="app-footer"><span className="footer-status"><i className={connected ? 'connected' : ''} />{connected ? '桌面宿主已连接' : inDesktop ? connectionStatus === 'failed' ? '宿主连接不可用' : '正在连接桌面宿主…' : '仅界面预览'}</span><span>{count ? `${count.toLocaleString()} 条弹幕已准备` : '尚未载入弹幕'}</span><span>PS7 · 原生渲染 <span className="footer-divider">/</span> {settingsStatusText[settingsStatus]} {settingsStatus === 'unknown' && <button className="text-button" onClick={() => settingsSession.current?.retryRecovery()}>重新同步设置</button>}</span></footer>
  </div>;
}
