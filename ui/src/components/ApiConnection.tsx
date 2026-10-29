import { useEffect, useState } from 'react';
import { request } from '../bridge';
import { validPayload } from '../protocol';

export function ApiConnection({ apiBase, saved, available, notice, onSaved }: {
  apiBase: string; saved: boolean; available: boolean; notice: string;
  onSaved(apiBase: string, saved: boolean): void;
}) {
  const [draft, setDraft] = useState(apiBase), [visible, setVisible] = useState(false);
  const [busy, setBusy] = useState(false), [error, setError] = useState('');
  useEffect(() => { setDraft(apiBase); setError(''); }, [apiBase]);
  const value = draft.trim(), dirty = value !== apiBase || Boolean(value) && !saved;
  const valid = validPayload('save-api', { apiBase: value });
  async function save() {
    if (!available || busy || !dirty || !valid) return;
    setBusy(true); setError('');
    try { const result = await request('save-api', { apiBase: value }); setDraft(result.apiBase); onSaved(result.apiBase, result.saved); }
    catch (e) { setError(e instanceof Error ? e.message : 'API 地址保存失败，请重试。'); }
    finally { setBusy(false); }
  }
  return <section className="api-connection" aria-labelledby="api-heading">
    <div className="api-heading"><h2 id="api-heading">连接弹幕服务</h2><p>保存一次，下次打开自动连接。</p></div>
    <form onSubmit={e => { e.preventDefault(); void save(); }}>
      <label className="field-label" htmlFor="api-base">API 地址</label>
      <div className="api-entry"><div className="api-input"><input id="api-base" type={visible ? 'text' : 'password'} autoComplete="off" spellCheck={false} maxLength={2048} placeholder="输入你的弹幕 API 服务地址" value={draft} disabled={busy} onChange={e => { setDraft(e.target.value); setError(''); }} aria-describedby="api-help" /><button type="button" className="text-button" aria-label={visible ? '隐藏服务地址' : '显示服务地址'} onClick={() => setVisible(!visible)}>{visible ? '隐藏' : '显示'}</button></div>
        <button type="submit" className="primary api-save" disabled={!available || busy || !dirty || !valid}>{busy ? '保存中…' : !dirty && saved ? '已保存' : !value && apiBase ? '清除保存' : '保存地址'}</button></div>
      <p id="api-help" className="muted">{dirty && value && !valid ? '请输入完整的 HTTP/HTTPS 服务地址，不含查询参数或用户名密码。' : '地址在此电脑当前 Windows 用户下加密保存；清空后保存可移除。'}</p>
      <p className={error ? 'api-status error' : 'api-status'} role={error ? 'alert' : 'status'}>{error || notice || (!available ? '请从新版桌面入口打开，以保存和连接服务。' : dirty ? '有未保存的修改，点击保存后生效。' : saved ? '已保存，重启后无需重新填写。' : '填入自己的服务地址，保存后自动匹配弹幕。')}</p>
    </form>
  </section>;
}
