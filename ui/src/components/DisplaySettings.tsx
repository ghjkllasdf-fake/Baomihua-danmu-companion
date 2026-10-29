import { useId } from 'react';
import type { Settings } from '../model';

function Range({ label, value, min, max, step = 1, display, onChange }: {
  label: string; value: number; min: number; max: number; step?: number; display: string; onChange(value: number): void;
}) {
  const id = useId();
  return <div className="range-control"><span><label htmlFor={id}>{label}</label><output htmlFor={id} aria-hidden="true">{display}</output></span>
    <input id={id} type="range" min={min} max={max} step={step} value={value} aria-valuetext={display} onChange={e => onChange(Number(e.target.value))} />
  </div>;
}
export function DisplaySettings({ settings: s, update, view = 'all' }: { settings: Settings; update(patch: Partial<Settings>): void; view?: 'basic' | 'advanced' | 'all' }) {
  return <section className="display-settings" aria-labelledby={view === 'advanced' ? 'advanced-heading' : 'display-heading'}>
    <h2 id={view === 'advanced' ? 'advanced-heading' : 'display-heading'} className="sr-only">{view === 'advanced' ? '高级显示设置' : '显示设置'}</h2>
    {view !== 'advanced' && <>
    <fieldset className="region-control"><legend>显示区域 <small>从顶部向下</small></legend>
      <div className="segments">{[[.25, '1/4'], [.5, '1/2'], [.75, '3/4'], [1, '全屏']].map(([value, label]) =>
        <button key={value} type="button" aria-pressed={s.areaFraction === value} onClick={() => update({ areaFraction: Number(value) })}>
          <span className="region-icon" style={{ '--fill': `${Number(value) * 100}%` } as React.CSSProperties} />{label}
        </button>)}</div>
    </fieldset>
    <div className="range-grid">
      <Range label="弹幕字号" min={16} max={64} value={s.fontSize} display={`${s.fontSize} px`} onChange={fontSize => update({ fontSize })} />
      <Range label="滚动速度" min={.5} max={2} step={.1} value={s.speedMultiplier} display={`${s.speedMultiplier.toFixed(1)}×`} onChange={speedMultiplier => update({ speedMultiplier })} />
      <Range label="不透明度" min={.1} max={1} step={.05} value={s.opacity} display={`${Math.round(s.opacity * 100)}%`} onChange={opacity => update({ opacity })} />
      <Range label="文字描边" min={0} max={3} step={.5} value={s.outlineWidth} display={`${s.outlineWidth} px`} onChange={outlineWidth => update({ outlineWidth })} />
    </div>
    <label className="toggle-row"><span>等比缩放<small>全屏与小窗，保持相同的观看比例</small></span><input aria-label="随视频窗口等比缩放" type="checkbox" role="switch" checked={s.scaleWithWindow} onChange={e => update({ scaleWithWindow: e.target.checked })} /></label>
    <div className="setting-line"><label htmlFor="density">弹幕密度</label><select id="density" value={s.density} onChange={e => update({ density: Number(e.target.value) })}>
      <option value={30}>稀疏 · 留白更多</option><option value={60}>标准 · 刚刚好</option><option value={100}>密集 · 热闹一点</option>
    </select></div>
    </>}
    {view !== 'basic' && <>
    <fieldset className="types"><legend>来源类型 · 全部向左滚动</legend>{([['rolling', '滚动'], ['top', '顶部'], ['bottom', '底部']] as const).map(([key, label]) =>
      <label key={key}><input type="checkbox" checked={s[key]} onChange={e => update({ [key]: e.target.checked })} />{label}</label>)}</fieldset>
    <details className="advanced"><summary>过滤与时间微调 <span>高级设置</span></summary>
      <label className="toggle-row"><span>合并重复弹幕<small>同一句话，10 秒内只出现一次</small></span><input type="checkbox" role="switch" checked={s.dedupe} onChange={e => update({ dedupe: e.target.checked })} /></label>
      <label className="keywords">屏蔽关键词<textarea rows={3} maxLength={2000} value={s.keywords} placeholder="每行一个关键词，不使用正则表达式" onChange={e => update({ keywords: e.target.value })} /></label>
      <Range label="弹幕时间偏移" min={-30000} max={30000} step={250} value={s.offsetMs} display={`${s.offsetMs > 0 ? '+' : ''}${(s.offsetMs / 1000).toFixed(2)} s`} onChange={offsetMs => update({ offsetMs })} />
      <p className="muted">正值让弹幕延后，负值让弹幕提前；不改变视频进度。</p>
    </details>
    </>}
  </section>;
}
