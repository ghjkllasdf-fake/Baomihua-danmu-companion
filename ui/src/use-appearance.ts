import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { defaultAppearanceSnapshot, validAppearanceSnapshot, type Appearance } from './appearance';
import { request, subscribe, subscribeConnection } from './bridge';
import { HostCommandError } from './bridge-errors';

export type AppearanceStatus = 'preview' | 'saved' | 'saving' | 'recovering' | 'error' | 'unknown';
export function useAppearance() {
  const [snapshot, setSnapshot] = useState(defaultAppearanceSnapshot), [status, setStatus] = useState<AppearanceStatus>('preview');
  const [error, setError] = useState(''), [available, setAvailable] = useState(false);
  const confirmed = useRef(snapshot), busy = useRef(false), epoch = useRef(0), supported = useRef(false);
  useLayoutEffect(() => {
    const root = document.documentElement;
    root.dataset.skin = snapshot.skin;
    root.dataset.backdrop = snapshot.backdrop;
    root.dataset.transparency = snapshot.reduceTransparency ? 'reduced' : 'regular';
    root.style.colorScheme = snapshot.skin === 'studio' ? 'dark' : 'light';
  }, [snapshot]);
  useEffect(() => {
    const stopConnection = subscribeConnection(connection => {
      epoch.current++; busy.current = false;
      const state = connection.status === 'ready' && connection.hello.capabilities.includes('appearance-v1') ? connection.hello.appearance : undefined;
      supported.current = Boolean(state); setAvailable(Boolean(state)); setError('');
      if (state) { confirmed.current = state; setSnapshot(state); setStatus('saved'); }
      else setStatus('preview');
    });
    const stopState = subscribe(data => {
      if (!supported.current || busy.current || !data || typeof data !== 'object' || !('appearance' in data)) return;
      if (!validAppearanceSnapshot(data.appearance)) { busy.current = true; setStatus('unknown'); setError('外观状态无效，请重新读取。'); return; }
      const state = data.appearance;
      if (state.revision < confirmed.current.revision) return;
      confirmed.current = state;
      setSnapshot(current => current.skin === state.skin && current.reduceTransparency === state.reduceTransparency && current.backdrop === state.backdrop && current.revision === state.revision && current.notice === state.notice ? current : state);
    });
    return () => { epoch.current++; stopConnection(); stopState(); };
  }, []);
  async function recover(generation = epoch.current) {
    if (!supported.current) return;
    busy.current = true; setStatus('recovering');
    try {
      const state = await request('get-appearance');
      if (generation !== epoch.current) return;
      confirmed.current = state; setSnapshot(state); busy.current = false;
      setStatus('error'); setError('保存结果已重新确认，请核对当前选中项；需要时重新选择。');
    } catch {
      if (generation !== epoch.current) return;
      setStatus('unknown'); setError('外观保存结果未确认，请重新读取后再修改。');
    }
  }
  async function choose(next: Appearance) {
    if (busy.current) return;
    const generation = epoch.current, previous = confirmed.current;
    setSnapshot({ ...snapshot, ...next }); setError('');
    if (!supported.current) { setStatus('preview'); return; }
    busy.current = true; setStatus('saving');
    try {
      const state = await request('set-appearance', next);
      if (generation !== epoch.current) return;
      confirmed.current = state; setSnapshot(state); setStatus('saved'); busy.current = false;
    } catch (failure) {
      if (generation !== epoch.current) return;
      setSnapshot(previous);
      if (failure instanceof HostCommandError && failure.code === 'appearance-not-applied') {
        busy.current = false; setStatus('error'); setError('外观保存失败，本次设置未应用，请重试。');
      } else await recover(generation);
    }
  }
  return { snapshot, status, error, available, choose, reload: () => recover() };
}
