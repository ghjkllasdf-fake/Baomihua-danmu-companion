export type UpdatePhase = 'unavailable' | 'idle' | 'checking' | 'current' | 'available' | 'downloading' | 'verifying' | 'ready' | 'applying' | 'failed';
export const updateCheckIntervals = [24, 168, 720] as const;
export type CheckIntervalHours = typeof updateCheckIntervals[number];
export const defaultCheckIntervalHours: CheckIntervalHours = 24;
export interface UpdatePreferences {
  autoCheck: boolean; autoDownload: boolean; channel: 'beta' | 'stable';
  // Omission preserves the legacy github-update-v1 host/payload contract.
  checkIntervalHours?: CheckIntervalHours;
}
export interface UpdateSnapshot extends UpdatePreferences {
  state: UpdatePhase; currentVersion: string; availableVersion: string; variant: 'standard' | 'includeAPI'; distribution: 'portable' | 'installer';
  checkedAt: string; downloadedBytes: number; totalBytes: number; notes: string; message: string; revision: number; supported: boolean; canInstall: boolean; lastResult: string;
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
  if (!object(v)) return false;
  const interval = Object.hasOwn(v, 'checkIntervalHours');
  return exact(v, ['autoCheck', 'autoDownload', 'channel', ...(interval ? ['checkIntervalHours'] : [])]) &&
    (!interval || updateCheckIntervals.some(hours => v.checkIntervalHours === hours)) &&
    typeof v.autoCheck === 'boolean' && typeof v.autoDownload === 'boolean' && ['beta', 'stable'].includes(String(v.channel));
}
export function validUpdateSnapshot(v: unknown): v is UpdateSnapshot {
  if (!object(v)) return false;
  const interval = Object.hasOwn(v, 'checkIntervalHours');
  if (!exact(v, ['state', 'currentVersion', 'availableVersion', 'variant', 'distribution', 'channel', 'autoCheck', 'autoDownload', 'checkedAt', 'downloadedBytes', 'totalBytes', 'notes', 'message', 'revision', 'supported', 'canInstall', 'lastResult', ...(interval ? ['checkIntervalHours'] : [])])) return false;
  if (!['unavailable', 'idle', 'checking', 'current', 'available', 'downloading', 'verifying', 'ready', 'applying', 'failed'].includes(String(v.state)) ||
    !['standard', 'includeAPI'].includes(String(v.variant)) || !['portable', 'installer'].includes(String(v.distribution)) ||
    !validUpdatePreferences({ autoCheck: v.autoCheck, autoDownload: v.autoDownload, channel: v.channel, ...(interval ? { checkIntervalHours: v.checkIntervalHours } : {}) }) ||
    typeof v.supported !== 'boolean' || typeof v.canInstall !== 'boolean' || typeof v.notes !== 'string' || v.notes.length > 16000 || typeof v.message !== 'string' || v.message.length > 500 || typeof v.lastResult !== 'string' || v.lastResult.length > 500 ||
    !integer(v.revision) || !integer(v.downloadedBytes, 1024 * 1024 * 1024) || !integer(v.totalBytes, 1024 * 1024 * 1024) || Number(v.downloadedBytes) > Number(v.totalBytes) ||
    typeof v.checkedAt !== 'string' || v.checkedAt.length > 64 || v.checkedAt !== '' && !Number.isFinite(Date.parse(v.checkedAt)) ||
    typeof v.currentVersion !== 'string' || !(version(v.currentVersion) || v.state === 'unavailable' && v.currentVersion === '') ||
    typeof v.availableVersion !== 'string' || v.availableVersion !== '' && !newer(v.availableVersion, v.currentVersion)) return false;
  const available = Boolean(v.availableVersion);
  if (['available', 'downloading', 'verifying', 'ready', 'applying'].includes(String(v.state)) && (!available || !v.supported)) return false;
  if (['idle', 'current', 'unavailable'].includes(String(v.state)) && available) return false;
  if (v.state === 'ready' && (!v.totalBytes || v.downloadedBytes !== v.totalBytes)) return false;
  return !v.canInstall || v.state === 'ready' && v.supported === true;
}
export function hasUpdate(value: unknown): boolean { return validUpdateSnapshot(value) && Boolean(value.availableVersion); }
export const supportsUpdateCheckInterval = (snapshot: UpdateSnapshot) => snapshot.checkIntervalHours !== undefined;
export const updateBusy = (state: UpdatePhase) => ['checking', 'downloading', 'verifying', 'applying'].includes(state);
