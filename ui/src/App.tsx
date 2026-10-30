import { useEffect, useRef, useState } from 'react';
import { inDesktop, request, revokeConnection, subscribe, subscribeConnection, type Connection } from './bridge';
import { defaultSettings, emptyPlayer, formatTime, parseTime, playbackStatus, validLocalApi, validPlayer, type LocalApi, type Settings } from './model';
import { SettingsSession, settingsStatusText, type SettingsStatus } from './settings-session';
import { createNativeNoticeHandler } from './host-notices';
import { initialPlayback, observePlayer } from './player-observation';
import { DisplaySettings } from './components/DisplaySettings';
import { StartupOptions } from './components/StartupOptions';
import { SourcePanel } from './components/SourcePanel';
import { ApiConnection } from './components/ApiConnection';
import { LocalApiConnection } from './components/LocalApiConnection';
import { validAutomaticMatch, type AutomaticMatchStatus } from './automatic-match';
import { useAppearance } from './use-appearance';
import { SkinPicker } from './components/SkinPicker';

export function App() {
  const [view, setView] = useState<'display' | 'sources' | 'more' | 'appearance'>('display'), [canHide, setCanHide] = useState(false);
  const appearance = useAppearance();
  const mainHeading = useRef<HTMLHeadingElement>(null), viewHeading = useRef<HTMLHeadingElement>(null);
  const [settings, setSettings] = useState(defaultSettings), [playback, setPlayback] = useState(initialPlayback);
  const player = playback.player;
  const [connectionStatus, setConnectionStatus] = useState<Connection['status']>('checking'), [apiBase, setApiBase] = useState('');
  const connected = connectionStatus === 'ready';
  const [connectionError, setConnectionError] = useState('');
  const [automaticMatching, setAutomaticMatching] = useState(false), [automaticMatch, setAutomaticMatch] = useState<AutomaticMatchStatus>();
  const [desktopLifecycle, setDesktopLifecycle] = useState(false);
  const [apiPersistence, setApiPersistence] = useState(false), [apiSaved, setApiSaved] = useState(false), [apiNotice, setApiNotice] = useState('');
  const [localApi, setLocalApi] = useState<LocalApi>();
  const [count, setCount] = useState(0), [error, setError] = useState(''), [manual, setManual] = useState('00:00:00');
  const [settingsStatus, setSettingsStatus] = useState<SettingsStatus>('preview'), [settingsError, setSettingsError] = useState('');
  const settingsSession = useRef<SettingsSession | null>(null);
  const latestPlayback = useRef(initialPlayback);
  const localSavePending = useRef(false), localRefreshNeeded = useRef(false);
  useEffect(() => {
    let active = true, trusted = false, embeddedApi = false, localState: LocalApi['state'] | undefined;
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
          setCanHide(connection.hello.capabilities.includes('tray-flyout-v1'));
          setApiPersistence(connection.hello.capabilities.includes('api-persistence-v1'));
          setApiSaved(connection.hello.apiSaved === true); setApiNotice(connection.hello.apiNotice || '');
          embeddedApi = connection.hello.capabilities.includes('embedded-api-v1');
          localState = embeddedApi ? connection.hello.localApi?.state : undefined;
          setLocalApi(embeddedApi ? connection.hello.localApi : undefined);
        } catch { revokeConnection('宿主显示设置不一致，请重新确认连接。'); }
      } else {
        setConnectionStatus(connection.status);
        setAutomaticMatching(false); setAutomaticMatch(undefined);
        setDesktopLifecycle(false);
        setCanHide(false);
        setApiPersistence(false);
        latestPlayback.current = observePlayer(latestPlayback.current, emptyPlayer); setPlayback(latestPlayback.current); setCount(0);
        setConnectionError(connection.status === 'failed' ? connection.message : '');
      }
    });
    const stop = subscribe(data => {
      if (!active || !trusted || !data || typeof data !== 'object') return;
      const state = data as { player?: unknown; commentCount?: unknown; notice?: unknown; settings?: unknown; automaticMatch?: unknown; localApi?: unknown };
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
      if (embeddedApi && state.localApi !== undefined) {
        if (validLocalApi(state.localApi)) {
          const becameReady = localState === 'starting' && state.localApi.state === 'ready';
          localState = state.localApi.state;
          setLocalApi(state.localApi);
          if (becameReady) {
            if (localSavePending.current) localRefreshNeeded.current = true;
            else void request('hello').catch(() => undefined);
          }
        }
        else setError('本地服务状态消息无效，请重新打开工作台。');
      }
    });
    // Connection notifications handle every handshake, including rejection and later recovery.
    if (inDesktop) void request('hello').catch(() => undefined);
    return () => { active = false; stopConnection(); stop(); session.dispose(); if (settingsSession.current === session) settingsSession.current = null; };
  }, []);
  function update(patch: Partial<Settings>) {
    settingsSession.current?.edit(patch);
  }
  async function applyLocalSources(sources: string[]) {
    localSavePending.current = true;
    try {
      await request('set-local-sources', { sources });
      // Only a new handshake can provide the restarted child's current address.
      await request('hello');
      localRefreshNeeded.current = false;
    } finally {
      localSavePending.current = false;
      if (localRefreshNeeded.current) {
        localRefreshNeeded.current = false;
        void request('hello').catch(() => undefined);
      }
    }
  }
  async function manualClock(playing: boolean) {
    const positionMs = parseTime(manual);
    if (positionMs === null) { setError('时间格式为 时:分:秒 或 分:秒。'); return; }
    try { await request('manual-clock', { positionMs, playing }); setError(''); }
    catch (e) { setError(e instanceof Error ? e.message : '校时失败。'); }
  }
  const auto = player.syncMode === 'automatic';
  const shownError = connectionError || settingsError || error;
  const matchNotice = automaticMatch?.state === 'loaded' ? `已自动匹配${automaticMatch.sourceTitle ? ` · 来源：${automaticMatch.sourceTitle}` : ''}` :
    automaticMatch && !['waiting', 'manual'].includes(automaticMatch.state) ? automaticMatch.message : '';
  async function hide() {
    if (!canHide) return;
    try { await request('hide'); } catch { setError('面板收起失败，请重试。'); }
  }
  function navigate(next: typeof view) {
    setView(next);
    requestAnimationFrame(() => (next === 'display' ? mainHeading.current : viewHeading.current)?.focus());
  }
  return <div className="app-shell mini-app" onKeyDown={event => {
    if (event.key === 'Escape' && !event.defaultPrevented && !event.nativeEvent.isComposing) { event.preventDefault(); void hide(); }
  }}>
    <header className="mini-toolbar">
      <div className="master-switch"><h1 className="mini-heading" ref={mainHeading} tabIndex={-1} aria-label="爆米花弹幕面板"><label htmlFor="master-enabled">弹幕</label></h1><input id="master-enabled" aria-label="显示弹幕" type="checkbox" role="switch" checked={settings.enabled} onChange={e => update({ enabled: e.target.checked })} /></div>
      <StartupOptions available={connected && desktopLifecycle} />
      <button className="skin-trigger" aria-label="切换皮肤" title="切换皮肤" aria-pressed={view === 'appearance'} onClick={() => navigate(view === 'appearance' ? 'display' : 'appearance')}><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="m14 4 6 6-7 7-6-6zM14 4l2-2 6 6-2 2M7 11l-2 2 6 6 2-2M6 17c-4 0-5 2-4 5 3 1 5 0 5-4" /></svg></button>
      <button className="hide-panel text-button" aria-label="收起面板" disabled={!canHide} onClick={() => void hide()}>收起</button>
    </header>
    {!inDesktop && <div className="preview-banner" role="status">浏览器预览模式：未连接桌面宿主或播放器。</div>}
    {shownError && <div className="app-error" role="alert">{shownError}<button aria-label="关闭提示" onClick={() => { setConnectionError(''); setError(''); settingsSession.current?.dismissMessage(); }}>×</button></div>}
    <main id="main" className="panel-main">
      {view !== 'display' && <nav className="panel-navigation" aria-label="面板导航"><button className="text-button" onClick={() => navigate('display')} aria-label="返回设置">← 返回设置</button><h2 ref={viewHeading} tabIndex={-1}>{view === 'sources' ? '手动搜索' : view === 'appearance' ? '外观与皮肤' : '更多设置'}</h2></nav>}
      <div hidden={view !== 'appearance'}><SkinPicker {...appearance} /></div>
      <div hidden={view !== 'display'} className="display-view">
        {!localApi && (!inDesktop || connected) && <ApiConnection apiBase={apiBase} saved={apiSaved} available={connected && apiPersistence} notice={apiNotice} onSaved={(base, saved) => { setApiBase(base); setApiSaved(saved); setApiNotice(''); }} />}
        {localApi && localApi.state !== 'ready' && <p className="local-api-summary" role="status">{localApi.state === 'starting' ? '本地弹幕服务正在启动…' : '本地服务未就绪，请在更多设置中重试。'}</p>}
        <DisplaySettings settings={settings} update={update} view="basic" />
      </div>
      <div hidden={view !== 'sources'} className="source-view"><SourcePanel apiBase={localApi && localApi.state !== 'ready' ? '' : apiBase} connected={connected && (!localApi || localApi.state === 'ready')} playback={playback} getPlayback={() => latestPlayback.current} onLoaded={setCount} automaticMatching={automaticMatching} automaticStatus={automaticMatch} active={view === 'sources'} /></div>
      <div hidden={view !== 'more'} className="more-view">
        <DisplaySettings settings={settings} update={update} view="advanced" />
        {localApi && <LocalApiConnection localApi={localApi} available={connected} onApply={applyLocalSources} />}
        <details className="manual-sync"><summary>手动时间校准 <span>{auto ? '自动同步中，无需手动校准' : '播放器未公开时间时使用'}</span></summary><div className="manual-controls"><label htmlFor="manual-time">视频当前时间</label><input id="manual-time" value={manual} onChange={e => setManual(e.target.value)} disabled={auto} /><button className="secondary" disabled={!connected || auto} onClick={() => void manualClock(true)}>校时并播放</button><button className="text-button" disabled={!connected || auto} onClick={() => void manualClock(false)}>校时并暂停</button></div></details>
      </div>
    </main>
    <footer className="mini-footer">
      <section className="playback-strip" aria-label="播放器连接状态"><div className="playback-title"><strong>{player.title || '等待爆米花开始播放'}</strong>{player.title && <small>{playbackStatus(player)}{player.episodeNumber ? ` · 第 ${player.episodeNumber} 集` : ''}</small>}</div>{player.positionMs !== null && <span className="timecode">{formatTime(player.positionMs)}</span>}</section>
      <p className="match-status" role="status">{matchNotice}</p>
      <div className="footer-actions"><button className="more-link" onClick={() => navigate('more')}>更多设置 <span aria-hidden="true">›</span></button><button className="manual-source-link" onClick={() => navigate(view === 'sources' ? 'display' : 'sources')}>手动搜索 / 换源</button></div>
      <div className="footer-meta"><span className="footer-status">{connected ? '桌面宿主已连接' : inDesktop ? connectionStatus === 'failed' ? '宿主连接不可用' : '正在连接桌面宿主…' : '仅界面预览'}</span><span>{count ? `${count.toLocaleString()} 条弹幕已准备` : settingsStatusText[settingsStatus]}</span></div>
      {settingsStatus === 'unknown' && <button className="text-button" onClick={() => settingsSession.current?.retryRecovery()}>重新同步设置</button>}
    </footer>
  </div>;
}
