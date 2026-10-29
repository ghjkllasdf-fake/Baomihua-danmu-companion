import { useEffect, useState } from 'react';
import { defaultLocalSources, safeLocalApiText, type LocalApi } from '../model';

export function LocalApiConnection({ localApi, available, onApply }: { localApi: LocalApi; available: boolean; onApply(sources: string[]): Promise<void> }) {
  const [draft, setDraft] = useState(localApi.sources), [busy, setBusy] = useState(false), [error, setError] = useState('');
  const sourceKey = localApi.sources.join(',');
  const availableKey = localApi.availableSources.map(source => source.id).join(',');
  useEffect(() => { setDraft(sourceKey.split(',')); }, [sourceKey, availableKey]);
  const chosen = new Set(draft), listed = [...draft, ...localApi.availableSources.map(source => source.id).filter(id => !chosen.has(id))];
  const labels = new Map(localApi.availableSources.map(source => [source.id, safeLocalApiText(source.label, source.id)]));
  const defaults = defaultLocalSources.filter(id => labels.has(id));
  const dirty = draft.join(',') !== sourceKey;
  const retry = !dirty && localApi.state === 'failed';
  const status = { starting: '本地服务正在启动。', ready: '本地服务已就绪。', failed: '本地服务启动失败，请重试。' }[localApi.state];
  function toggle(id: string) {
    setDraft(current => current.includes(id) ? current.filter(source => source !== id) : [...current, id]);
    setError('');
  }
  function move(id: string, delta: number) {
    setDraft(current => {
      const index = current.indexOf(id), next = index + delta;
      if (index < 0 || next < 0 || next >= current.length) return current;
      const result = [...current]; [result[index], result[next]] = [result[next], result[index]]; return result;
    });
    setError('');
  }
  async function apply() {
    if (!available || busy || !draft.length || (!dirty && !retry)) return;
    setBusy(true); setError('');
    try {
      await onApply(draft);
    } catch { setError('来源设置未确认，请检查本地服务后重试。'); }
    finally { setBusy(false); }
  }
  return <section className="api-connection local-api" aria-labelledby="local-api-heading">
    <div className="api-heading"><h2 id="local-api-heading">本地弹幕服务</h2><p>自动连接，无需填写地址。</p></div>
    <div>
      <p className={localApi.state === 'failed' ? 'local-api-status error' : 'local-api-status'} role="status">{safeLocalApiText(localApi.message, status)}</p>
      <details className="local-sources"><summary>选择弹幕来源 <span>已启用 {draft.length} 个</span></summary>
        <p className="muted">按顺序启用来源；实际可用性取决于来源服务。</p>
        <ul className="local-source-list">{listed.map(id => {
          const label = labels.get(id) || id, index = draft.indexOf(id), selected = index !== -1;
          return <li key={id}><label><input type="checkbox" aria-label={`启用 ${label}`} checked={selected} disabled={!available || busy} onChange={() => toggle(id)} /><span>{label}</span></label>
            <div className="local-source-order"><button type="button" className="secondary" aria-label={`上移 ${label}`} disabled={!available || busy || !selected || index === 0} onClick={() => move(id, -1)}>↑</button>
              <button type="button" className="secondary" aria-label={`下移 ${label}`} disabled={!available || busy || !selected || index === draft.length - 1} onClick={() => move(id, 1)}>↓</button></div></li>;
        })}</ul>
        <div className="local-source-actions"><button type="button" className="secondary" disabled={!available || busy || defaults.length === 0} onClick={() => { setDraft(defaults); setError(''); }}>恢复默认</button>
          <button type="button" className="primary api-save" disabled={!available || busy || !draft.length || (!dirty && !retry)} onClick={() => void apply()}>{busy ? '应用中…' : retry ? '重试启动' : dirty ? '应用来源' : '已应用'}</button></div>
        {!draft.length && <p className="api-status error" role="status">请至少启用一个来源。</p>}
        {error && <p className="api-status error" role="alert">{error}</p>}
      </details>
    </div>
  </section>;
}
