import { useEffect, useRef, useState } from 'react';
import { request } from '../bridge';
import type { PlayerObservation } from '../player-observation';
import type { Anime, Episode } from '../protocol';
import type { AutomaticMatchStatus } from '../automatic-match';

export function SourcePanel({ apiBase, connected, playback, getPlayback, onLoaded, automaticMatching = false, automaticStatus }: { apiBase: string; connected: boolean; playback: PlayerObservation; getPlayback(): PlayerObservation; onLoaded(count: number): void; automaticMatching?: boolean; automaticStatus?: AutomaticMatchStatus }) {
  const { player, identityEpoch } = playback;
  const base = apiBase;
  const [keyword, setKeyword] = useState(''), [results, setResults] = useState<Anime[]>([]), [episodes, setEpisodes] = useState<Episode[]>([]);
  const [animeId, setAnimeId] = useState(''), [episodeId, setEpisodeId] = useState('');
  const [busy, setBusy] = useState(''), [error, setError] = useState(''), [notice, setNotice] = useState('先找到正在看的作品，再选择正确来源。');
  const [followPlayer, setFollowPlayer] = useState(true);
  const version = useRef(0);
  const searchedTitle = useRef('');
  const boundTitle = useRef(''), observedMedia = useRef(playback);
  const isCurrent = (current: number, epoch: number) => current === version.current && epoch === getPlayback().identityEpoch;
  useEffect(() => {
    version.current++; boundTitle.current = ''; setResults([]); setEpisodes([]); setEpisodeId(''); setBusy(''); setError('');
  }, [apiBase]);
  useEffect(() => {
    if (identityEpoch === observedMedia.current.identityEpoch) return;
    const hadMedia = Boolean(observedMedia.current.player.title.trim());
    const lostBinding = Boolean(boundTitle.current) && playback.bindingEpoch !== observedMedia.current.bindingEpoch;
    observedMedia.current = playback;
    version.current++; setBusy(''); setError('');
    if (lostBinding) { boundTitle.current = ''; setEpisodes([]); setEpisodeId(''); }
    if (!player.title) { setNotice('播放器信息暂不可用，请在恢复后核对分集；也可手动选择来源。'); return; }
    if (lostBinding) { setNotice('播放器已切换作品或重新识别，请重新匹配弹幕来源。'); return; }
    if (!boundTitle.current) { setNotice(hadMedia ? '播放器已切换，原弹幕已清除，请重新选择并加载。' : '已识别播放器，请核对当前分集并加载弹幕；先前下载不会自动沿用。'); return; }
    if (boundTitle.current !== player.title.trim()) {
      boundTitle.current = ''; setEpisodes([]); setEpisodeId('');
      setNotice('播放器已切换作品，请重新匹配弹幕来源。'); return;
    }
    if (!followPlayer) { setNotice(hadMedia ? '播放器已换集，自动跟随已关闭，请手动选择并加载。' : '播放器信息已恢复，自动跟随已关闭，请手动选择并加载。'); return; }
    const chosen = episodes.find(e => Number(e.episodeNumber) === player.episodeNumber);
    if (chosen) { setEpisodeId(String(chosen.episodeId)); void loadComments(String(chosen.episodeId)); }
    else { setEpisodeId(''); setNotice('当前来源没有这一集，请选择其他来源。'); }
  }, [identityEpoch, followPlayer]);
  async function loadComments(id: string) {
    if (identityEpoch !== getPlayback().identityEpoch) return;
    const current = ++version.current, media = identityEpoch; setBusy('正在下载弹幕…'); setError('');
    try {
      const result = await request('comments', { apiBase: base, value: id, expectedMediaRevision: player.mediaRevision, sourceTitle: results.find(anime => String(anime.animeId) === animeId)?.animeTitle || '手动选择的来源' });
      if (!isCurrent(current, media)) return;
      boundTitle.current = searchedTitle.current === player.title.trim() && episodes.some(e => String(e.episodeId) === id && Number(e.episodeNumber) === player.episodeNumber) ? player.title.trim() : '';
      onLoaded(result.count); setNotice(result.count ? player.title ? `已载入 ${result.count.toLocaleString()} 条弹幕，请留意播放器同步状态。` : `已下载 ${result.count.toLocaleString()} 条弹幕。打开视频后，请核对分集并重新载入。` : '这个来源没有弹幕，可切换其他版本。');
    } catch (e) { if (isCurrent(current, media)) setError(e instanceof Error ? e.message : '加载失败。'); }
    finally { if (isCurrent(current, media)) setBusy(''); }
  }
  async function loadEpisodes(id: string) {
    if (identityEpoch !== getPlayback().identityEpoch) return;
    boundTitle.current = '';
    const current = ++version.current, media = identityEpoch; setBusy('正在读取分集…'); setError(''); setEpisodes([]); setEpisodeId('');
    try {
      const result = await request('episodes', { apiBase: base, value: id });
      if (!isCurrent(current, media)) return;
      const list = result.bangumi?.episodes ?? []; setEpisodes(list);
      const matchesPlayer = Boolean(player.title) && searchedTitle.current === player.title.trim() && (player.episodeNumber ?? 0) > 0;
      const chosen = matchesPlayer ? list.find(e => Number(e.episodeNumber) === player.episodeNumber) : list[0];
      setEpisodeId(chosen ? String(chosen.episodeId) : '');
      setNotice(matchesPlayer && list.length && !chosen ? `当前来源没有第 ${player.episodeNumber} 集，请换源或手动选择。` : list.length ? `找到 ${list.length} 个分集，请选择当前播放的一集。` : '该来源没有可用分集。');
    } catch (e) { if (isCurrent(current, media)) setError(e instanceof Error ? e.message : '读取失败。'); }
    finally { if (isCurrent(current, media)) setBusy(''); }
  }
  async function search(title = keyword) {
    if (identityEpoch !== getPlayback().identityEpoch) return;
    if (!title.trim()) { setError('请输入片名，或从播放器读取。'); return; }
    searchedTitle.current = title.trim();
    boundTitle.current = '';
    const current = ++version.current, media = identityEpoch; setBusy('正在寻找弹幕来源…'); setError(''); setResults([]); setEpisodes([]); setAnimeId(''); setEpisodeId('');
    try {
      const response = await request('search', { apiBase: base, value: title.trim() });
      if (!isCurrent(current, media)) return;
      const list = response.animes ?? []; setResults(list);
      if (list.length) { setAnimeId(String(list[0].animeId)); setNotice(`找到 ${list.length} 个匹配，选对年份和来源，弹幕才对味。`); }
      else setNotice('暂时没有找到，试试作品原名或去掉年份。');
      if (list.length === 1) { await loadEpisodes(String(list[0].animeId)); }
    } catch (e) { if (isCurrent(current, media)) setError(e instanceof Error ? e.message : '搜索失败。'); }
    finally { if (isCurrent(current, media)) setBusy(''); }
  }
  return <aside className="source-panel" aria-labelledby="source-heading">
    <div className="section-heading"><h2 id="source-heading">{automaticMatching ? '自动匹配弹幕' : '找到你的弹幕'}</h2><span className="eyebrow">SOURCE</span></div>
    {automaticMatching && <p className="muted">识别视频后自动搜索、选择分集并加载。匹配失败时会通过 Windows 通知提醒你。</p>}
    <form onSubmit={e => { e.preventDefault(); void search(); }}>
      <label className="field-label" htmlFor="keyword">正在看什么？</label>
      <div className="search-box"><input id="keyword" value={keyword} placeholder="输入片名或剧集名称" onChange={e => setKeyword(e.target.value)} /><button disabled={!connected || Boolean(busy)} aria-label="搜索弹幕来源" type="submit">↗</button></div>
      <button type="button" className="text-button detect-button" disabled={!connected || !player.title || Boolean(busy)} onClick={() => { setKeyword(player.title); void search(player.title); }}>↳ 使用播放器片名</button>
    </form>
    <div className="source-results" aria-busy={Boolean(busy)}>
      <div className="list-heading"><span>匹配作品</span><span>{results.length ? `${results.length} 个来源` : '等待搜索'}</span></div>
      {!results.length && <div className="empty-source"><span className="empty-mark">✳</span><p>下一场好戏<br /><strong>从这里开始。</strong></p></div>}
      <div className="result-list">{results.map(anime => <button key={anime.animeId} className={`result-item ${animeId === String(anime.animeId) ? 'selected' : ''}`} disabled={Boolean(busy)} onClick={() => { boundTitle.current = ''; setAnimeId(String(anime.animeId)); setEpisodes([]); setEpisodeId(''); }}>
        <span className="selection-dot" /><span>{anime.animeTitle}</span>
      </button>)}</div>
      {results.length > 0 && <button className="secondary full" disabled={!animeId || Boolean(busy)} onClick={() => void loadEpisodes(animeId)}>载入分集 <span>→</span></button>}
    </div>
    {episodes.length > 0 && <div className="episodes"><label htmlFor="episode">当前分集</label><select id="episode" value={episodeId} onChange={e => setEpisodeId(e.target.value)}><option value="" disabled>请选择当前播放分集</option>{episodes.map((e, i) => <option key={e.episodeId} value={String(e.episodeId)}>{e.episodeNumber ?? i + 1}. {e.episodeTitle ?? '未命名'}</option>)}</select><button className="primary full" disabled={!episodeId || Boolean(busy)} onClick={() => void loadComments(episodeId)}>加载这一集的弹幕</button></div>}
    {!automaticMatching && <label className="toggle-row"><span>跟随播放器换集<small>载入匹配分集后，沿用同一来源</small></span><input type="checkbox" role="switch" checked={followPlayer} onChange={e => setFollowPlayer(e.target.checked)} /></label>}
    <p className={`source-notice ${error || automaticStatus?.state === 'failed' ? 'error' : ''}`} role={error ? 'alert' : 'status'}>{error || busy || (automaticStatus?.state !== 'manual' ? automaticStatus?.message : '') || notice}{!error && !busy && automaticStatus?.state === 'loaded' && automaticStatus.sourceTitle && <small>来源：{automaticStatus.sourceTitle}</small>}</p>
    {busy && <button className="text-button" onClick={() => { version.current++; setBusy(''); setNotice('已取消当前请求。'); void request('cancel').catch(e => setError(e.message)); }}>取消当前请求</button>}
  </aside>;
}
