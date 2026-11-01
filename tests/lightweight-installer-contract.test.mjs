import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { allowedEntry, releaseNames } from '../scripts/release-lib.mjs';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('ordinary Inno installation writes and uninstalls its own logged program files directly', async () => {
  const installer = await source('installer/DanmuCompanion.iss');
  assert.match(installer, /Source: "\{#PayloadDir\}\\\*"; DestDir: "\{app\}"; Flags: ignoreversion recursesubdirs createallsubdirs\s/);
  assert.match(installer, /UninstallLogMode=append/);
  assert.match(installer, /Filename: "\{app\}\\DanmuCompanion\.exe";.*nowait postinstall skipifsilent/);
  for (const retired of ['uninsneveruninstall', 'AdmitUpdateFile', 'UpdateJournal', 'HelperGuard', 'ResourceRetirement', 'NativeUpdater', '--install-current', '--launch-installed']) {
    assert.equal(installer.includes(retired), false, `Retired installer mechanism remains: ${retired}`);
  }
  assert.doesNotMatch(installer, /Type:\s*filesandordirs|DelTree\(/i);
  assert.match(installer, /RemoveStartupShortcut/);
});

test('release build has only main/probe runtimes and two download artifacts per edition', async () => {
  const build = await source('scripts/build-release.mjs');
  assert.equal(build.includes('NativeUpdater'), false);
  assert.equal(build.includes('installerHelperTrust'), false);
  assert.equal(build.includes('names.inventory'), false);
  for (const path of ['DanmuCompanion.Updater.exe', 'DanmuCompanion.Updater.deps.json', 'DanmuCompanion.Updater.runtimeconfig.json']) assert.equal(allowedEntry(path), false);
  assert.deepEqual(Object.keys(releaseNames('0.1.1-beta.7')).sort(), ['base', 'installer', 'portable']);
});

test('normal application and release notice have no updater ownership or health-handoff dependency', async () => {
  const host = await source('desktop/NativeHost/Program.cs');
  for (const retired of ['UpdateRootLock', 'UpdateWorker', 'updateHandshake', '--update-parent']) assert.equal(host.includes(retired), false);
  for (const path of ['desktop/native/ReleaseNoticeCatalog.cs', 'desktop/native/ReleaseNoticeService.cs', 'desktop/native/ReleaseNoticeTransport.cs']) {
    assert.doesNotMatch(await source(path), /Update(Package|Storage|Version|Retry|Worker|Apply|FileIdentity|Directories)/);
  }
});
