import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { validUpdateSnapshot } from '../ui/src/updates.ts';

const root = new URL('../', import.meta.url);
const source = name => readFile(new URL(name, root), 'utf8');

test('customer bridge exposes notices but no online update or Portable preparation', async () => {
  const desktop = await source('desktop/native/Desktop.cs');
  assert.match(desktop, /new ReleaseNoticeService\(paths\)/);
  assert.match(desktop, /github-release-notice-v1/);
  for (const old of ['github-update-v1', 'portable-setup-v1', 'download-update', 'install-update', 'prepare-portable', 'UpdateLease', 'PrepareInstallAsync', 'PortableSetupState']) {
    assert.equal(desktop.includes(old), false, `Retired bridge entry remains: ${old}`);
  }
});

test('normal startup and notice runtime have no updater installation, ownership or maintenance code', async () => {
  const host = await source('desktop/NativeHost/Program.cs');
  for (const old of ['UpdateRootLock', 'UpdateWorker', '--update-handshake', '--update-parent']) assert.equal(host.includes(old), false);
  for (const path of ['desktop/NativeUpdater/NativeUpdater.csproj', 'desktop/native/UpdateWorker.cs', 'desktop/native/UpdateApply.cs']) await assert.rejects(source(path), { code: 'ENOENT' });
  const runtime = await source('desktop/native/ReleaseNoticeService.cs');
  for (const old of ['UpdateService', 'UpdateLauncher', 'UpdateMaintenance', 'UpdatePortablePreparation', 'DownloadAsync', 'Process.Start', 'RemoveOwned']) assert.equal(runtime.includes(old), false);
});

test('actual native serializer matches the integrated UI notice contract for every phase and frequency', async () => {
  const program = await source('tests/NativeDistribution/Program.cs');
  assert.match(program, /--release-notice-contract-fixture/);
  const executable = fileURLToPath(new URL('tests/NativeDistribution/bin/Release/net10.0-windows/win-x64/NativeDistribution.Tests.exe', root));
  const snapshots = JSON.parse(execFileSync(executable, ['--release-notice-contract-fixture'], { encoding: 'utf8', windowsHide: true, timeout: 30000 }));
  assert.equal(snapshots.length, 72);
  for (const snapshot of snapshots) {
    assert.equal(validUpdateSnapshot(snapshot), true, `Native/UI contract mismatch: ${JSON.stringify(snapshot)}`);
    assert.equal(Object.keys(snapshot).length, 14);
  }
  assert.deepEqual([...new Set(snapshots.map(snapshot => snapshot.state))].sort(), ['available', 'checking', 'current', 'failed', 'idle', 'unavailable']);
  assert.deepEqual([...new Set(snapshots.map(snapshot => snapshot.checkIntervalHours))].sort((a, b) => a - b), [24, 168, 720]);
});
