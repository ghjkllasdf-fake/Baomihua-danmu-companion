import { useState, useSyncExternalStore, type CSSProperties } from 'react';
import type { Settings } from '../model';

const motionPreference = window.matchMedia('(prefers-reduced-motion: reduce)');
// Stable browser subscription with React-managed cleanup:
// https://react.dev/reference/react/useSyncExternalStore#subscribing-to-a-browser-api
function subscribeMotionPreference(changed: () => void) {
  motionPreference.addEventListener('change', changed);
  return () => motionPreference.removeEventListener('change', changed);
}
const reducedMotionSnapshot = () => motionPreference.matches;

export function Preview({ settings: s }: { settings: Settings }) {
  const [playing, setPlaying] = useState(true);
  const reducedMotion = useSyncExternalStore(subscribeMotionPreference, reducedMotionSnapshot);
  const rowHeight = Math.max(23, s.fontSize * .65 * 1.4 + 3);
  const rows = Math.max(1, Math.floor(212 * s.areaFraction / rowHeight));
  const rolling = s.rolling || s.top || s.bottom ? Math.min(3, rows) : 0;
  const samples = ['这一幕值得再看一遍', '和大家一起看，才够热闹', '前方高能！'];
  const outline = s.outlineWidth * .65;
  const shadow = outline ? `${-outline}px 0 #314034, 0 ${outline}px #314034, ${outline}px 0 #314034, 0 ${-outline}px #314034` : 'none';
  const style = { '--area': `${s.areaFraction * 100}%`, '--duration': `${8 / s.speedMultiplier}s`, '--bullet-size': `${s.fontSize * .65}px`, '--bullet-opacity': s.opacity,
    '--bullet-shadow': shadow, '--play-state': playing && s.enabled && !reducedMotion ? 'running' : 'paused' } as CSSProperties;
  return <section className="preview-section" aria-label="弹幕演示预览">
    <div className="section-heading"><h2>先看看效果</h2><span className="demo-label">DEMO · 非播放器画面</span></div>
    <div className={`preview ${s.enabled ? '' : 'preview-off'}`} style={style}>
      <div className="preview-orbit" /><div className="preview-mountain" /><span className="preview-caption">留一点空白<br /><strong>给故事本身。</strong></span>
      <div className="preview-area"><span className="area-label">{Math.round(s.areaFraction * 100)}% 显示区域</span>
        {samples.slice(0, rolling).map((text, i) => <span key={text} className="preview-bullet" style={{ top: 4 + i * rowHeight, animationDelay: `${-2 - i * 2}s` }}>{text}</span>)}
      </div>
      <button className="preview-play" disabled={reducedMotion} onClick={() => setPlaying(!playing)} aria-label={reducedMotion ? '减少动态效果已开启，演示保持静止' : playing ? '暂停演示预览' : '播放演示预览'}>{reducedMotion ? '—' : playing ? 'Ⅱ' : '▶'}</button>
      <span className="preview-note">{reducedMotion ? '仅演示外观 · 减少动态，已静止' : '仅演示外观，不控制视频播放'}</span>
    </div>
  </section>;
}
