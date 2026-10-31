import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { defaultSettings } from '../src/model';
import { skinIds } from '../src/appearance';
import type { UpdateSnapshot } from '../src/updates';

const fixture: UpdateSnapshot = {
  state: 'unavailable', currentVersion: '0.1.1-beta.7', availableVersion: '', variant: 'standard', distribution: 'portable',
  channel: 'beta', autoCheck: true, autoDownload: false, checkIntervalHours: 24, checkedAt: '', downloadedBytes: 0, totalBytes: 0,
  notes: '', message: '', revision: 1, supported: false, canInstall: false, lastResult: '', portableSetupState: 'required',
};
interface WireCommand { id: string; type: string; payload: unknown }
interface SetupFixture {
  commands: WireCommand[];
  emit: (value: unknown) => void;
  reply: (value: unknown) => void;
  finish: (state: 'required' | 'prepared', message: string, ok?: boolean) => void;
}
async function connect(page: Page, skin = 'fluent', snapshot = fixture, capability = true, acknowledgeOnly = false) {
  await page.addInitScript(({ settings, skin, initial, capability, acknowledgeOnly }) => {
    let snapshot = initial, sequence = 0, preparationId: string | undefined;
    const callbacks: ((event: { data: unknown }) => void)[] = [], commands: WireCommand[] = [];
    const emit = (data: unknown) => callbacks.forEach(callback => callback({ data }));
    const broadcast = (update: unknown) => emit({ type: 'state', sequence: ++sequence, data: { update } });
    const reply = (data: unknown) => { if (preparationId) { emit({ type: 'response', id: preparationId, ok: true, data }); preparationId = undefined; } };
    Object.defineProperty(window, 'portableFixture', { value: { commands, emit: broadcast, reply,
      finish: (state: 'required' | 'prepared', message: string, ok = true) => {
        snapshot = { ...snapshot, portableSetupState: state, message, revision: snapshot.revision + 1 };
        broadcast(snapshot);
        if (ok) reply(snapshot);
        else if (preparationId) { emit({ type: 'response', id: preparationId, ok: false, error: message }); preparationId = undefined; }
      },
    } });
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
      addEventListener: (_: string, callback: (event: { data: unknown }) => void) => callbacks.push(callback),
      postMessage: (command: WireCommand) => {
        commands.push(command);
        if (command.type === 'prepare-portable') {
          preparationId = command.id;
          snapshot = { ...snapshot, portableSetupState: 'preparing', message: '', revision: snapshot.revision + 1 };
          broadcast(snapshot);
          if (acknowledgeOnly) setTimeout(() => reply({ accepted: true }), 10);
          return;
        }
        const data = command.type === 'hello' ? {
          protocolVersion: 1, capabilities: ['media-context-v1', 'tray-flyout-v1', 'appearance-v1', 'github-update-v1', ...(capability ? ['portable-setup-v1'] : [])],
          apiBase: '', settings, settingsRevision: 0, stateSequence: sequence,
          appearance: { skin, reduceTransparency: false, revision: 0, backdrop: 'solid', notice: '' }, update: snapshot,
        } : snapshot;
        setTimeout(() => emit({ type: 'response', id: command.id, ok: true, data }), 10);
      },
    } } });
  }, { settings: defaultSettings, skin, initial: snapshot, capability, acknowledgeOnly });
  await page.goto('/');
  await expect(page.locator('.mini-app')).toBeVisible();
}
async function showMore(page: Page) {
  await page.getByRole('button', { name: '更多设置', exact: true }).click();
  await expect(page.locator('#more-settings > :last-child')).toHaveClass('update-panel');
}
async function openUpdates(page: Page) {
  await showMore(page);
  const panel = page.locator('.update-panel');
  expect(await panel.getAttribute('open')).toBeNull();
  await panel.locator(':scope > summary').click();
  await expect(panel).toHaveAttribute('open', '');
}
const commands = (page: Page) => page.evaluate(() => (window as unknown as { portableFixture: SetupFixture }).portableFixture.commands);
const emit = (page: Page, value: unknown) => page.evaluate(value => (window as unknown as { portableFixture: SetupFixture }).portableFixture.emit(value), value);
const finish = (page: Page, state: 'required' | 'prepared', message: string, ok = true) => page.evaluate(({ state, message, ok }) => (window as unknown as { portableFixture: SetupFixture }).portableFixture.finish(state, message, ok), { state, message, ok });

for (const skin of skinIds) test(`${skin}: preparation stays compact, explicit and independent of update success`, async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 540 }); await connect(page, skin);
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  await openUpdates(page);
  const panel = page.locator('.update-panel');
  await expect(panel.locator(':scope > summary')).toHaveAccessibleName('软件更新，需准备');
  await expect(panel).toContainText('弹幕仍可使用');
  await expect(panel).toContainText('原目录不改动');
  await expect(panel.locator('.update-preferences')).toHaveCount(0);
  await expect(panel.getByRole('button')).toHaveCount(1);
  expect((await commands(page)).filter(value => value.type === 'prepare-portable')).toEqual([]);
  for (const width of [380, 320]) {
    await page.setViewportSize({ width, height: 540 }); await panel.scrollIntoViewIfNeeded();
    await expect(panel.getByRole('status')).toBeInViewport({ ratio: 1 });
    await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toBeInViewport({ ratio: 1 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`required-${width}-${skin}.png`) });
  }
  await page.setViewportSize({ width: 380, height: 540 });
  await page.getByRole('button', { name: '准备免安装版', exact: true }).focus(); await page.keyboard.press('Enter');
  const busyButton = page.getByRole('button', { name: '正在准备…', exact: true });
  await expect(busyButton).toBeDisabled(); await expect(busyButton).toHaveAttribute('aria-busy', 'true');
  await page.keyboard.press('Enter');
  expect((await commands(page)).filter(value => value.type === 'prepare-portable').map(value => value.payload)).toEqual([{}]);
  await page.evaluate(value => (window as unknown as { portableFixture: SetupFixture }).portableFixture.reply(value), { ...fixture, portableSetupState: 'preparing', revision: 2 });
  await expect(busyButton).toBeDisabled();
  await page.getByRole('button', { name: '返回', exact: true }).click();
  await showMore(page); await expect(panel).toHaveAttribute('open', '');
  for (const width of [380, 320]) {
    await page.setViewportSize({ width, height: 540 }); await panel.scrollIntoViewIfNeeded();
    await expect(panel.getByRole('status')).toBeInViewport({ ratio: 1 });
    await expect(busyButton).toBeInViewport({ ratio: 1 });
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`preparing-${width}-${skin}.png`) });
  }
  await finish(page, 'prepared', '文件已准备。请退出当前程序，从所选目录启动免安装版。');
  await expect(panel.locator(':scope > summary')).toHaveAccessibleName('软件更新，文件已准备');
  await expect(page.getByRole('button', { name: '文件已准备', exact: true })).toBeDisabled();
  await expect(panel).toContainText('从所选目录启动');
  await expect(panel).not.toContainText('更新成功'); await expect(panel).not.toContainText('回滚');
  await expect(page.locator('.update-indicator')).toHaveCount(0);
  for (const width of [380, 320]) {
    await page.setViewportSize({ width, height: 540 }); await panel.scrollIntoViewIfNeeded();
    await expect(panel.getByRole('status')).toBeInViewport({ ratio: 1 });
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`prepared-${width}-${skin}.png`) });
  }
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze()).violations;
  expect(violations).toEqual([]);
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  await expect(panel.getByRole('status')).toBeVisible();
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`prepared-forced-${skin}.png`) });
  expect((await commands(page)).some(value => ['download-update', 'install-update', 'close'].includes(value.type))).toBe(false);
});

test('cancellation and partial creation failure stay truthful and permit only an explicit new attempt', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 540 }); await connect(page); await openUpdates(page);
  await page.getByRole('button', { name: '准备免安装版', exact: true }).click();
  await finish(page, 'required', '已取消，未创建或复制文件。');
  const panel = page.locator('.update-panel');
  await expect(panel.getByRole('status')).toHaveText('已取消，未创建或复制文件。');
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toBeEnabled();
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath('cancelled-380.png') });
  await page.getByRole('button', { name: '准备免安装版', exact: true }).click();
  const failure = '准备未完成，目标目录可能已有部分文件，请另选新空目录。原目录未改动。';
  await finish(page, 'required', failure, false);
  await expect(panel.getByRole('alert')).toHaveText(failure);
  await expect(panel).not.toContainText('已清理'); await expect(panel).not.toContainText('未创建或复制文件');
  const retry = page.getByRole('button', { name: '准备免安装版', exact: true });
  await expect(retry).toBeEnabled();
  for (const width of [380, 320]) {
    await page.setViewportSize({ width, height: 540 }); await panel.scrollIntoViewIfNeeded();
    await expect(panel.getByRole('alert')).toBeInViewport({ ratio: 1 }); await expect(retry).toBeInViewport({ ratio: 1 });
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath(`partial-failure-${width}.png`) });
  }
  expect((await commands(page)).filter(value => value.type === 'prepare-portable')).toHaveLength(2);
});

for (const gate of ['capability', 'field', 'none', 'installer', 'unknown'] as const) test(`${gate}: no misleading Portable preparation entry`, async ({ page }) => {
  const legacy = { ...fixture }; delete legacy.portableSetupState;
  const snapshot = gate === 'field' ? legacy : gate === 'installer' ? { ...fixture, distribution: 'installer' as const, portableSetupState: 'none' as const }
    : gate === 'none' || gate === 'unknown' ? { ...fixture, portableSetupState: 'none' as const, message: gate === 'unknown' ? '当前运行方式无法确认，自动更新不可用。' : '' } : fixture;
  await page.setViewportSize({ width: 380, height: 540 }); await connect(page, 'fluent', snapshot, gate !== 'capability'); await openUpdates(page);
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toHaveCount(0);
  await expect(page.locator('.update-panel')).not.toContainText('请在新空目录准备');
  if (gate === 'unknown') {
    await expect(page.locator('.update-build')).not.toContainText('便携版');
    await expect(page.locator('.update-build')).toContainText('更新不可用');
  }
  expect((await commands(page)).some(value => value.type === 'prepare-portable')).toBe(false);
});

test('background qualification may enable preparation only through a native push without auto-opening or auto-starting', async ({ page }) => {
  await connect(page, 'fluent', { ...fixture, portableSetupState: 'none' }); await showMore(page);
  const panel = page.locator('.update-panel');
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toHaveCount(0);
  await emit(page, { ...fixture, revision: 2 });
  expect(await panel.getAttribute('open')).toBeNull();
  await expect(panel.locator(':scope > summary')).toHaveAccessibleName('软件更新，需准备');
  await panel.locator(':scope > summary').click();
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toBeEnabled();
  await emit(page, { ...fixture, portableSetupState: 'none', revision: 3 });
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toHaveCount(0);
  expect((await commands(page)).some(value => value.type === 'prepare-portable')).toBe(false);
});

test('an unconfirmed reply blocks duplicate setup until a newer native outcome confirms only file preparation', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 380, height: 540 }); await connect(page, 'fluent', fixture, true, true); await openUpdates(page);
  await page.getByRole('button', { name: '准备免安装版', exact: true }).click();
  await expect(page.locator('.update-panel').getByRole('alert')).toContainText('结果尚未确认');
  await expect(page.getByRole('button', { name: '正在准备…', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: '文件已准备', exact: true })).toHaveCount(0);
  await finish(page, 'prepared', '文件已准备。请退出当前程序，从所选目录启动免安装版。');
  await expect(page.locator('.update-panel').getByRole('alert')).toHaveCount(0);
  await expect(page.getByRole('button', { name: '文件已准备', exact: true })).toBeDisabled();
  expect((await commands(page)).filter(value => value.type === 'prepare-portable')).toHaveLength(1);
  await page.locator('.mini-app').screenshot({ path: testInfo.outputPath('late-confirmed-380.png') });
});

test('malformed and stale setup snapshots cannot reopen an unsafe or already completed action', async ({ page }) => {
  await connect(page); await openUpdates(page);
  await emit(page, { ...fixture, portableSetupState: 'invalid', revision: 2 });
  await expect(page.locator('.update-panel').getByRole('alert')).toContainText('状态数据无效');
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toBeDisabled();
  await emit(page, { ...fixture, revision: 3 });
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: '准备免安装版', exact: true }).click();
  await emit(page, { ...fixture, portableSetupState: 'prepared', revision: 10 });
  await page.evaluate(value => (window as unknown as { portableFixture: SetupFixture }).portableFixture.reply(value), { ...fixture, revision: 9 });
  await emit(page, { ...fixture, revision: 8 });
  await expect(page.getByRole('button', { name: '文件已准备', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: '准备免安装版', exact: true })).toHaveCount(0);
  expect((await commands(page)).filter(value => value.type === 'prepare-portable')).toHaveLength(1);
});

test('preparation preserves a large touch target without adding another launch action', async ({ browser }, testInfo) => {
  const context = await browser.newContext({ hasTouch: true, viewport: { width: 380, height: 540 } });
  const page = await context.newPage();
  try {
    await connect(page, 'glass', { ...fixture, variant: 'includeAPI' }); await openUpdates(page);
    const button = page.getByRole('button', { name: '准备免安装版', exact: true });
    expect((await button.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await button.click(); await finish(page, 'prepared', '文件已准备。请退出当前程序，从所选目录启动免安装版。');
    await expect(page.locator('.update-panel').getByRole('button')).toHaveCount(1);
    await page.locator('.mini-app').screenshot({ path: testInfo.outputPath('touch-prepared-380.png') });
  } finally { await context.close(); }
});
