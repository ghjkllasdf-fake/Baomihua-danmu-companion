import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

test('customer updater has no old-payload backup, rollback or recovery entry', async () => {
  for (const path of ['desktop/native/UpdateWorker.cs', 'desktop/native/UpdateLauncher.cs', 'desktop/NativeUpdater/Program.cs', 'desktop/NativeHost/Program.cs']) {
    const source = await readFile(new URL(`../${path}`, import.meta.url), 'utf8');
    assert.doesNotMatch(source, /UpdateTransaction|UpdateInstallerTransaction|UpdateRecoveryEntry|RecoverAsync|FindRecovery|\.Restore\(/, path);
  }
  const ui = await readFile(new URL('../ui/src/components/UpdatePanel.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(ui, /Recover-Danmu|恢复快捷方式/);
  assert.match(ui, /不备份/);
  assert.match(ui, /state\.distribution === 'installer'/);
  assert.match(ui, /同版本完整安装器原位修复/);
  assert.match(ui, /同版本完整 ZIP 解压到新空目录/);
  assert.match(ui, /仅在覆盖失败或新版未正常启动/);
  const updater = await readFile(new URL('../desktop/NativeUpdater/Program.cs', import.meta.url), 'utf8');
  assert.doesNotMatch(updater, /--record-installed-ownership/);
  const installer = await readFile(new URL('../installer/DanmuCompanion.iss', import.meta.url), 'utf8');
  assert.doesNotMatch(installer, /Exec\(ExpandConstant\('\{tmp\}\\MicrosoftEdgeWebview2Setup\.exe'/);
  assert.match(installer, /Exec\(Stage \+ '\\MicrosoftEdgeWebview2Setup\.exe'/);
  const runSection = installer.split('[Run]')[1].split('[Code]')[0];
  assert.match(runSection, /--launch-installed/);
  assert.doesNotMatch(runSection, /nowait/);
  assert.doesNotMatch(installer, /\[UninstallRun\]|Parameters: "--remove-startup"/);
  assert.match(installer, /usUninstall then RemoveOwnedStartupShortcut/);
  const worker = await readFile(new URL('../desktop/native/UpdateWorker.cs', import.meta.url), 'utf8');
  assert.match(worker, /new UpdatePayloadGuard\(paths.Root,expected\)/);
});
