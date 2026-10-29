export interface Settings {
  areaFraction: number; fontSize: number; scaleWithWindow: boolean; speedMultiplier: number;
  opacity: number; outlineWidth: number; density: number; rolling: boolean; top: boolean;
  bottom: boolean; dedupe: boolean; keywords: string; offsetMs: number; enabled: boolean;
}
export const defaultSettings: Settings = {
  areaFraction: .5, fontSize: 22, scaleWithWindow: false, speedMultiplier: .8,
  opacity: .85, outlineWidth: 0, density: 60, rolling: true, top: true, bottom: true,
  dedupe: true, keywords: '', offsetMs: 0, enabled: true,
};
export function validSettings(value: unknown): value is Settings {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const s = value as Settings;
  const range = (n: unknown, min: number, max: number) => typeof n === 'number' && Number.isFinite(n) && n >= min && n <= max;
  return [.25, .5, .75, 1].includes(s.areaFraction) && range(s.fontSize, 16, 64) &&
    range(s.speedMultiplier, .5, 2) && range(s.opacity, .1, 1) && range(s.outlineWidth, 0, 3) &&
    range(s.offsetMs, -30000, 30000) && [30, 60, 100].includes(s.density) &&
    typeof s.keywords === 'string' && s.keywords.length <= 2000 &&
    [s.enabled, s.scaleWithWindow, s.rolling, s.top, s.bottom, s.dedupe].every(v => typeof v === 'boolean');
}
export interface SettingsSnapshot { settings: Settings; settingsRevision: number }
export function validSettingsSnapshot(value: unknown): value is SettingsSnapshot {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const s = value as Record<string, unknown>;
  return typeof s.settingsRevision === 'number' && Number.isSafeInteger(s.settingsRevision) && s.settingsRevision >= 0 && validSettings(s.settings);
}
export interface Hello extends SettingsSnapshot {
  apiBase: string; stateSequence: number; protocolVersion: 1; capabilities: string[];
  apiSaved?: boolean; apiNotice?: string;
}
export function validHello(value: unknown): value is Hello {
  if (!validSettingsSnapshot(value)) return false;
  const hello = value as SettingsSnapshot & Partial<Hello>;
  return typeof hello.apiBase === 'string' && hello.apiBase.length <= 2048 &&
    typeof hello.stateSequence === 'number' && Number.isSafeInteger(hello.stateSequence) && hello.stateSequence >= 0 &&
    hello.protocolVersion === 1 && Array.isArray(hello.capabilities) && hello.capabilities.length <= 32 &&
    hello.capabilities.every(capability => typeof capability === 'string' && capability.length <= 64) &&
    hello.capabilities.includes('media-context-v1') &&
    (!hello.capabilities.includes('api-persistence-v1') || typeof hello.apiSaved === 'boolean' &&
      typeof hello.apiNotice === 'string' && hello.apiNotice.length <= 500);
}
export function formatTime(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  return [Math.floor(total / 3600), Math.floor(total % 3600 / 60), total % 60].map(n => String(n).padStart(2, '0')).join(':');
}
export function parseTime(text: string): number | null {
  const parts = text.trim().split(':');
  if (parts.length < 2 || parts.length > 3 || parts.some(p => !/^\d{1,3}$/.test(p))) return null;
  const values = parts.map(Number);
  if (values.at(-1)! > 59 || (parts.length === 3 && values[1] > 59)) return null;
  return values.reduce((n, v) => n * 60 + v, 0) * 1000;
}
export interface Player {
  sequence: number; mediaRevision: number; seekRevision: number;
  syncMode: 'automatic' | 'manual' | 'unavailable' | 'stale'; state: 'playing' | 'paused' | 'unknown' | 'unavailable'; title: string;
  positionMs: number | null; episodeNumber: number | null; source: string; detail: string;
  durationMs: number | null; sampleMonotonicMs: number | null; positionResolutionMs: number | null;
  videoBoundsPx: { x: number; y: number; width: number; height: number } | null;
  videoBoundsSource: 'window-client' | 'unavailable'; dpiScale: number | null;
  windowVisible: boolean | null; windowMinimized: boolean | null; isForeground: boolean | null;
}
export const emptyPlayer: Player = {
  sequence: 0, mediaRevision: 0, seekRevision: 0, syncMode: 'unavailable', state: 'unavailable', title: '', positionMs: null,
  episodeNumber: null, source: '', detail: '', durationMs: null, sampleMonotonicMs: null, positionResolutionMs: null,
  videoBoundsPx: null, videoBoundsSource: 'unavailable', dpiScale: null, windowVisible: null, windowMinimized: null, isForeground: null,
};
export function validPlayer(value: unknown): value is Player {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const p = value as Record<string, unknown>;
  const revision = (v: unknown) => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0;
  const number = (v: unknown, positive = false) => v === null || typeof v === 'number' && Number.isFinite(v) && (positive ? v > 0 : v >= 0);
  if (![p.sequence, p.mediaRevision, p.seekRevision].every(revision) ||
    typeof p.syncMode !== 'string' || !['automatic', 'manual', 'unavailable', 'stale'].includes(p.syncMode) ||
    typeof p.state !== 'string' || !['playing', 'paused', 'unknown', 'unavailable'].includes(p.state) ||
    ![p.title, p.source, p.detail].every(v => typeof v === 'string') ||
    !number(p.positionMs) || !number(p.durationMs, true) || !number(p.sampleMonotonicMs) || !number(p.positionResolutionMs, true) || !number(p.dpiScale, true) ||
    !(p.episodeNumber === null || revision(p.episodeNumber) && p.episodeNumber !== 0) ||
    ![p.windowVisible, p.windowMinimized, p.isForeground].every(v => v === null || typeof v === 'boolean')) return false;
  const bounds = p.videoBoundsPx;
  if (bounds === null) { if (p.videoBoundsSource !== 'unavailable') return false; }
  else {
    if (!bounds || typeof bounds !== 'object' || Array.isArray(bounds) || p.videoBoundsSource !== 'window-client') return false;
    const b = bounds as Record<string, unknown>;
    if (![b.x, b.y].every(v => typeof v === 'number' && Number.isSafeInteger(v)) || !revision(b.width) || b.width === 0 || !revision(b.height) || b.height === 0) return false;
  }
  const known = p.state === 'playing' || p.state === 'paused';
  if (p.syncMode === 'automatic') return known && typeof p.title === 'string' && p.title.trim().length > 0 &&
    p.positionMs !== null && p.sampleMonotonicMs !== null && p.positionResolutionMs !== null;
  if (p.syncMode === 'manual') return known && p.positionMs !== null;
  return !known && (p.state !== 'unavailable' || p.positionMs === null);
}
export function playbackStatus(player: Player): string {
  if (player.syncMode === 'automatic') return player.state === 'paused' ? '已自动同步 · 已暂停' : player.state === 'playing' ? '已自动同步 · 播放中' : '播放状态未确认';
  return { manual: '手动时间模式', unavailable: '等待播放器', stale: '同步信号已中断' }[player.syncMode];
}
