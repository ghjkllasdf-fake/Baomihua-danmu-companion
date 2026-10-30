export const skinIds = ['fluent', 'glass', 'winui', 'studio'] as const;
export type SkinId = typeof skinIds[number];
export interface Appearance { skin: SkinId; reduceTransparency: boolean }
export interface AppearanceSnapshot extends Appearance { revision: number; backdrop: 'solid' | 'acrylic'; notice: string }
export const defaultAppearance: Appearance = { skin: 'fluent', reduceTransparency: false };
export const defaultAppearanceSnapshot: AppearanceSnapshot = { ...defaultAppearance, revision: 0, backdrop: 'solid', notice: '' };
const object = (value: unknown): value is Record<string, unknown> => Boolean(value) && typeof value === 'object' && !Array.isArray(value);
const fields = (value: Record<string, unknown>, names: string[]) => Object.keys(value).length === names.length && names.every(name => Object.hasOwn(value, name));
const preferences = (value: Record<string, unknown>) => skinIds.includes(value.skin as SkinId) && typeof value.reduceTransparency === 'boolean';
export function validAppearance(value: unknown): value is Appearance {
  return object(value) && fields(value, ['skin', 'reduceTransparency']) && preferences(value);
}
export function validAppearanceSnapshot(value: unknown): value is AppearanceSnapshot {
  return object(value) && fields(value, ['skin', 'reduceTransparency', 'revision', 'backdrop', 'notice']) && preferences(value) &&
    Number.isSafeInteger(value.revision) && (value.revision as number) >= 0 && ['solid', 'acrylic'].includes(value.backdrop as string) &&
    typeof value.notice === 'string' && value.notice.length <= 500;
}
