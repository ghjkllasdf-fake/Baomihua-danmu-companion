import { skinIds, type Appearance, type AppearanceSnapshot } from '../appearance';
import type { AppearanceStatus } from '../use-appearance';

const skins = {
  fluent: { name: 'A · 原生清爽', description: '保留原方案 · 蓝灰框架与白色设置区' },
  glass: { name: 'B · 流光玻璃', description: '浮动工具层 · 透光材质与胶囊控件' },
  winui: { name: 'C · WinUI', description: '系统设置行 · 焦点边线与 Acrylic' },
  studio: { name: 'D · 石墨工作室', description: '内嵌推子 · 方形拨块与独立读数窗' },
};
export function SkinPicker({ snapshot, status, error, available, choose, reload }: {
  snapshot: AppearanceSnapshot; status: AppearanceStatus; error: string; available: boolean;
  choose(value: Appearance): Promise<void>; reload(): Promise<void>;
}) {
  const disabled = ['saving', 'recovering', 'unknown'].includes(status);
  return <section className="skin-picker" aria-label="皮肤设置">
    <p className="appearance-status" role="status">{status === 'saving' ? '正在保存外观…' : status === 'recovering' ? '正在重新确认外观…' : status === 'saved' ? '已保存 · 下次打开沿用此皮肤' : status === 'preview' ? '仅本次预览 · 未连接支持换肤的桌面宿主，不保存到浏览器' : '请核对当前外观状态'}</p>
    {snapshot.notice && <p className="skin-notice" role="status">{snapshot.notice}</p>}
    {error && <p className="skin-error" role="alert">{error}</p>}
    {status === 'unknown' && <button className="secondary" onClick={() => void reload()}>重新读取外观</button>}
    <div className="skin-choices">{skinIds.map(id => <button key={id} type="button" className={`skin-choice skin-choice-${id}`} aria-label={skins[id].name} aria-pressed={snapshot.skin === id} disabled={disabled} onClick={() => void choose({ skin: id, reduceTransparency: snapshot.reduceTransparency })}>
      <span className={`skin-swatch skin-swatch-${id}`} aria-hidden="true"><i className="swatch-toolbar" /><i className="swatch-segment" /><i className="swatch-track"><b /></i><i className="swatch-track"><b /></i><i className="swatch-footer" /></span>
      <strong>{skins[id].name}<span aria-hidden="true">{snapshot.skin === id ? '✓' : ''}</span></strong><small>{skins[id].description}</small>
    </button>)}</div>
    <label className="toggle-row skin-transparency"><span>减少透明效果<small>使用实色背景，优先保证文字清晰</small></span><input type="checkbox" role="switch" aria-label="减少透明效果" checked={snapshot.reduceTransparency} disabled={disabled} onChange={event => void choose({ skin: snapshot.skin, reduceTransparency: event.target.checked })} /></label>
    {['glass', 'winui'].includes(snapshot.skin) && <p className="skin-material-note">{snapshot.reduceTransparency ? '已关闭透明材质。' : snapshot.backdrop === 'acrylic' && available ? '已请求 Windows 桌面毛玻璃；失焦、节能或系统关闭透明时可回退实色。' : '当前使用实色回退；需支持的 Windows 11 宿主且系统允许透明效果。'}{snapshot.skin === 'glass' ? ' 纯色背景下变化较轻；借鉴 Liquid Glass，非 Apple 原生组件。' : ''}</p>}
  </section>;
}
