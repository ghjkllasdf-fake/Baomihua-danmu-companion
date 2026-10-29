import { defaultSettings, validHello, validLocalApi, validLocalSources, validSettings, validSettingsSnapshot, type Hello, type LocalApi, type Settings, type SettingsSnapshot } from './model';

export interface Anime { animeId: string | number; animeTitle: string }
export interface Episode { episodeId: string | number; episodeNumber?: string | number; episodeTitle?: string }
interface ApiPayload { apiBase: string; value: string }
interface Clock { positionMs: number; playing: boolean }
interface Command<P, R> { payload: P; result: R }
export interface Commands {
  hello: Command<never, Hello>;
  'get-settings': Command<never, SettingsSnapshot>;
  'update-settings': Command<{ settings: Settings }, SettingsSnapshot>;
  'configure-api': Command<{ apiBase: string }, { configured: true }>;
  'save-api': Command<{ apiBase: string }, { apiBase: string; saved: boolean }>;
  'get-local-api': Command<Record<string, never>, LocalApi>;
  'set-local-sources': Command<{ sources: string[] }, LocalApi>;
  'get-startup': Command<never, { enabled: boolean }>;
  'set-startup': Command<{ enabled: boolean }, { enabled: boolean }>;
  search: Command<ApiPayload, { animes: Anime[] }>;
  episodes: Command<ApiPayload, { bangumi: { episodes: Episode[] } }>;
  comments: Command<ApiPayload & { expectedMediaRevision: number; sourceTitle?: string }, { count: number }>;
  'manual-clock': Command<Clock, Clock>;
  cancel: Command<never, { cancelled: true }>;
  close: Command<never, { closing: true }>;
}
export type CommandType = keyof Commands;
export type Result<K extends CommandType> = Commands[K]['result'];
export type Arguments<K extends CommandType> = [Commands[K]['payload']] extends [never] ? [] : [payload: Commands[K]['payload']];
export type HostRequest = { [K in CommandType]: [type: K, ...args: Arguments<K>] }[CommandType];
export const record = (value: unknown): value is Record<string, unknown> => Boolean(value) && typeof value === 'object' && !Array.isArray(value);
export const revision = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
const text = (value: unknown, limit: number) => typeof value === 'string' && value.trim().length > 0 && value.length <= limit;
const scalar = (value: unknown) => text(value, 500) || typeof value === 'number' && Number.isSafeInteger(value);
const clock = (value: unknown) => record(value) && typeof value.positionMs === 'number' && Number.isFinite(value.positionMs) && value.positionMs >= 0 && value.positionMs <= 86400000 && typeof value.playing === 'boolean';
const keys = (value: object, expected: string[]) => Object.keys(value).length === expected.length && expected.every(key => Object.hasOwn(value, key));
export function validPayload(type: CommandType, value: unknown): boolean {
  if (!record(value)) return false;
  switch (type) {
    case 'hello': case 'get-settings': case 'get-startup': case 'get-local-api': case 'cancel': case 'close': return keys(value, []);
    case 'set-local-sources': return keys(value, ['sources']) && validLocalSources(value.sources);
    case 'set-startup': return keys(value, ['enabled']) && typeof value.enabled === 'boolean';
    case 'update-settings': return keys(value, ['settings']) && validSettings(value.settings) && keys(value.settings, Object.keys(defaultSettings));
    case 'manual-clock': return keys(value, ['positionMs', 'playing']) && clock(value);
    case 'configure-api': return keys(value, ['apiBase']) && validPayload('search', { apiBase: value.apiBase, value: 'configuration' });
    case 'save-api': return keys(value, ['apiBase']) && (value.apiBase === '' || validPayload('configure-api', value));
    case 'search': case 'episodes': case 'comments': {
      const expected = type === 'comments' ? ['apiBase', 'value', 'expectedMediaRevision', ...(Object.hasOwn(value, 'sourceTitle') ? ['sourceTitle'] : [])] : ['apiBase', 'value'];
      if (!keys(value, expected) || !text(value.value, 500) || !text(value.apiBase, 2048)) return false;
      if (type === 'comments' && Object.hasOwn(value, 'sourceTitle') && (typeof value.sourceTitle !== 'string' || value.sourceTitle.length > 500)) return false;
      try {
        const url = new URL(String(value.apiBase));
        if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.href.includes('?') || url.href.includes('#')) return false;
      } catch { return false; }
      return type !== 'comments' || revision(value.expectedMediaRevision);
    }
    default: return false;
  }
}
export function validResult<K extends CommandType>(type: K, value: unknown): value is Result<K> {
  if (!record(value)) return false;
  switch (type) {
    case 'hello': return validHello(value);
    case 'get-local-api': case 'set-local-sources': return validLocalApi(value);
    case 'get-settings': case 'update-settings': return validSettingsSnapshot(value);
    case 'get-startup': case 'set-startup': return keys(value, ['enabled']) && typeof value.enabled === 'boolean';
    case 'configure-api': return value.configured === true;
    case 'save-api': return keys(value, ['apiBase', 'saved']) && typeof value.saved === 'boolean' &&
      typeof value.apiBase === 'string' && value.saved === Boolean(value.apiBase) && validPayload('save-api', { apiBase: value.apiBase });
    case 'search': return Array.isArray(value.animes) && value.animes.length <= 10000 && value.animes.every(item => record(item) && scalar(item.animeId) && text(item.animeTitle, 500));
    case 'episodes': return record(value.bangumi) && Array.isArray(value.bangumi.episodes) && value.bangumi.episodes.length <= 10000 && value.bangumi.episodes.every(item =>
      record(item) && scalar(item.episodeId) && (item.episodeNumber === undefined || scalar(item.episodeNumber)) &&
      (item.episodeTitle === undefined || typeof item.episodeTitle === 'string' && item.episodeTitle.length <= 500));
    case 'comments': return revision(value.count) && value.count <= 300000;
    case 'manual-clock': return clock(value);
    case 'cancel': return value.cancelled === true;
    case 'close': return value.closing === true;
    default: return false;
  }
}
