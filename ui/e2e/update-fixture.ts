import { expect, type Page } from '@playwright/test';
import { defaultSettings } from '../src/model';
import { releasePageRoot, type UpdateSnapshot } from '../src/updates';

export const fixture: UpdateSnapshot = { state: 'available', currentVersion: '0.1.1-beta.7', availableVersion: '0.1.1-beta.10', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, checkIntervalHours: 24, checkedAt: '2026-11-01T02:00:00Z', notes: '保留个人设置，改进版本提示。\n<script>not executed</script>', message: '', revision: 1,
  supported: true, releasePage: releasePageRoot };
export interface WireCommand { id: string; type: string; payload: unknown }
interface Options { capability?: string; holdCheck?: boolean; checkFailure?: string; badOpenReply?: boolean }
interface FixtureWindow { commands: string[]; requests: WireCommand[]; preferenceWrites: unknown[]; emit: (value: unknown) => void }

export async function connect(page: Page, skin = 'fluent', initial = fixture, failPreferences = false, options: Options = {}) {
  await page.addInitScript(({ settings, skin, initial, failPreferences, options, releasePageRoot }) => {
    let snapshot = initial, sequence = 0, heldCheck: string | undefined;
    const callbacks: ((event: { data: unknown }) => void)[] = [], commands: string[] = [], requests: WireCommand[] = [], preferenceWrites: unknown[] = [];
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    const broadcast = (update: unknown) => emit({ type: 'state', sequence: ++sequence, data: { update } });
    Object.defineProperty(window, 'updateFixture', { value: { commands, requests, preferenceWrites, emit: broadcast } });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (message: { id: string; type: string; payload: Record<string, unknown> }) => {
        commands.push(message.type); requests.push(message);
        if (message.type === 'set-update-preferences') {
          preferenceWrites.push(message.payload);
          if (failPreferences) { setTimeout(() => emit({ type: 'response', id: message.id, ok: false, error: '更新偏好保存失败' }), 10); return; }
          const changed = message.payload.channel !== snapshot.channel;
          snapshot = { ...snapshot, ...message.payload, ...(changed ? { state: 'idle', availableVersion: '', notes: '', releasePage: releasePageRoot } : {}), revision: snapshot.revision + 1 };
        }
        if (message.type === 'check-update') {
          if (options.holdCheck) { heldCheck = message.id; snapshot = { ...snapshot, state: 'checking', revision: snapshot.revision + 1 }; broadcast(snapshot); return; }
          snapshot = options.checkFailure ? { ...snapshot, state: 'failed', availableVersion: '', releasePage: releasePageRoot, notes: '', message: options.checkFailure, revision: snapshot.revision + 1 }
            : { ...snapshot, state: 'available', availableVersion: '0.1.1-beta.10', releasePage: releasePageRoot, message: '', revision: snapshot.revision + 1 };
          broadcast(snapshot);
        }
        if (message.type === 'cancel-update') {
          snapshot = { ...snapshot, state: 'idle', availableVersion: '', notes: '', releasePage: releasePageRoot, message: '已取消版本检查。', revision: snapshot.revision + 1 };
          broadcast(snapshot);
          if (heldCheck) { emit({ type: 'response', id: heldCheck, ok: true, data: snapshot }); heldCheck = undefined; }
        }
        const data = message.type === 'hello' ? { protocolVersion: 1, capabilities: ['media-context-v1', 'tray-flyout-v1', 'appearance-v1', options.capability ?? 'github-release-notice-v1'],
          apiBase: '', settings, settingsRevision: 0, stateSequence: sequence, appearance: { skin, reduceTransparency: false, revision: 0, backdrop: 'solid', notice: '' }, update: snapshot }
          : message.type === 'open-update-page' ? { opened: true, ...(options.badOpenReply ? { url: 'https://evil.example' } : {}) } : snapshot;
        setTimeout(() => emit({ type: 'response', id: message.id, ok: true, data }), 10);
      },
    } } });
  }, { settings: defaultSettings, skin, initial, failPreferences, options, releasePageRoot });
  await page.goto('/'); await expect(page.locator('.mini-app')).toBeVisible();
}
export const commands = (page: Page) => page.evaluate(() => (window as unknown as { updateFixture: FixtureWindow }).updateFixture.commands);
export const requests = (page: Page) => page.evaluate(() => (window as unknown as { updateFixture: FixtureWindow }).updateFixture.requests);
export const preferences = (page: Page) => page.evaluate(() => (window as unknown as { updateFixture: FixtureWindow }).updateFixture.preferenceWrites);
export const emit = (page: Page, value: unknown) => page.evaluate(value => (window as unknown as { updateFixture: FixtureWindow }).updateFixture.emit(value), value);
export async function openUpdates(page: Page) {
  const panel = page.locator('.update-panel');
  if (await panel.getAttribute('open') === null) await panel.locator(':scope > summary').click();
  await expect(panel).toHaveAttribute('open', '');
}
