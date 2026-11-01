export type UpdatePhase = 'unavailable' | 'idle' | 'checking' | 'current' | 'available' | 'failed';
export const releasePageRoot = 'https://github.com/ghjkllasdf-fake/Baomihua-danmu-companion/releases';
export const updateCheckIntervals = [24, 168, 720] as const;
export type CheckIntervalHours = typeof updateCheckIntervals[number];
export const defaultCheckIntervalHours: CheckIntervalHours = 24;
export interface UpdatePreferences {
  autoCheck: boolean; channel: 'beta' | 'stable'; checkIntervalHours: CheckIntervalHours;
}
export interface UpdateSnapshot extends UpdatePreferences {
  state: UpdatePhase; currentVersion: string; availableVersion: string; variant: 'standard' | 'includeAPI'; distribution: 'portable' | 'installer';
  checkedAt: string; notes: string; message: string; revision: number; supported: boolean; releasePage: string;
}
const object = (v: unknown): v is Record<string, unknown> => Boolean(v) && typeof v === 'object' && !Array.isArray(v);
const exact = (v: object, keys: string[]) => Object.keys(v).length === keys.length && keys.every(k => Object.hasOwn(v, k));
const integer = (v: unknown, max = Number.MAX_SAFE_INTEGER) => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0 && v <= max;
function version(value: unknown): { core: number[]; pre: string[] } | null {
  if (typeof value !== 'string' || value.length > 96 || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?$/.test(value)) return null;
  const split = value.indexOf('-'), core = (split < 0 ? value : value.slice(0, split)).split('.').map(Number), pre = split < 0 ? [] : value.slice(split + 1).split('.');
  return core.every(n => integer(n, 4294967295)) && pre.every(p => !/^0\d+$/.test(p)) ? { core, pre } : null;
}
function newer(a: string, b: string): boolean {
  const x = version(a), y = version(b); if (!x || !y) return false;
  for (let i = 0; i < 3; i++) if (x.core[i] !== y.core[i]) return x.core[i] > y.core[i];
  if (!x.pre.length || !y.pre.length) return !x.pre.length && Boolean(y.pre.length);
  for (let i = 0; i < Math.min(x.pre.length, y.pre.length); i++) {
    const m = x.pre[i], n = y.pre[i]; if (m === n) continue;
    const numericM = /^\d+$/.test(m), numericN = /^\d+$/.test(n);
    return numericM && numericN ? m.length !== n.length ? m.length > n.length : m > n : numericM !== numericN ? !numericM : m > n;
  }
  return x.pre.length > y.pre.length;
}
export function validUpdatePreferences(v: unknown): v is UpdatePreferences {
  return object(v) && exact(v, ['autoCheck', 'channel', 'checkIntervalHours']) &&
    updateCheckIntervals.some(hours => v.checkIntervalHours === hours) && typeof v.autoCheck === 'boolean' &&
    (v.channel === 'beta' || v.channel === 'stable');
}
export function validReleasePage(value: unknown, targetVersion: string): value is string {
  if (value === releasePageRoot) return true;
  if (!version(targetVersion)) return false;
  return ['', 'v'].some(prefix => value === `${releasePageRoot}/tag/${encodeURIComponent(prefix + targetVersion)}`);
}
export function validUpdateSnapshot(v: unknown): v is UpdateSnapshot {
  if (!object(v)) return false;
  if (!exact(v, ['state', 'currentVersion', 'availableVersion', 'variant', 'distribution', 'channel', 'autoCheck', 'checkedAt', 'notes', 'message', 'revision', 'supported', 'checkIntervalHours', 'releasePage'])) return false;
  if (typeof v.state !== 'string' || !['unavailable', 'idle', 'checking', 'current', 'available', 'failed'].includes(v.state) ||
    (v.variant !== 'standard' && v.variant !== 'includeAPI') || (v.distribution !== 'portable' && v.distribution !== 'installer') ||
    !validUpdatePreferences({ autoCheck: v.autoCheck, channel: v.channel, checkIntervalHours: v.checkIntervalHours }) ||
    typeof v.supported !== 'boolean' || typeof v.notes !== 'string' || v.notes.length > 16000 || typeof v.message !== 'string' || v.message.length > 500 || !integer(v.revision) ||
    typeof v.checkedAt !== 'string' || v.checkedAt.length > 64 || v.checkedAt !== '' && !Number.isFinite(Date.parse(v.checkedAt)) ||
    typeof v.currentVersion !== 'string' || !(version(v.currentVersion) || v.state === 'unavailable' && v.currentVersion === '') ||
    typeof v.availableVersion !== 'string' || v.availableVersion !== '' && !newer(v.availableVersion, v.currentVersion)) return false;
  const available = Boolean(v.availableVersion);
  if (v.state === 'available' && !available || available && !v.supported) return false;
  if (['idle', 'current', 'unavailable'].includes(v.state) && available) return false;
  return validReleasePage(v.releasePage, v.availableVersion);
}
export function hasUpdate(value: unknown): boolean { return validUpdateSnapshot(value) && Boolean(value.availableVersion); }
export const updateBusy = (state: UpdatePhase) => state === 'checking';
