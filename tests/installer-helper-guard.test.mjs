import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { copyFile, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
import { installerHelperTrust } from '../scripts/installer-helper-guard.mjs';

const root = resolve(import.meta.dirname, '..');
test('actual Inno validates its compiled helper trust before every execution and locks rename/write races', { timeout: 90000 }, async () => {
  const parent = join(root, 'artifacts', 'updater-helper-tests'), fixture = join(parent, randomUUID());
  await mkdir(fixture, { recursive: true });
  try {
    const stage = join(fixture, 'payload'); await mkdir(stage);
    // Controlled shell fixture, never a product updater or real application test.
    await copyFile(join(process.env.SystemRoot, 'System32/cmd.exe'), join(stage, 'DanmuCompanion.Updater.exe'));
    await copyFile(join(process.env.SystemRoot, 'System32/cmd.exe'), join(stage, 'MicrosoftEdgeWebview2Setup.exe'));
    await writeFile(join(stage, 'fixture.dll'), 'trusted dependency');
    await writeFile(join(stage, 'manifest.json'), 'compiled manifest sentinel');
    const files = [];
    for (const path of ['DanmuCompanion.Updater.exe', 'MicrosoftEdgeWebview2Setup.exe', 'fixture.dll', 'manifest.json']) {
      const bytes = await readFile(join(stage, path));
      files.push({ path, length: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') });
    }
    const trust = join(fixture, 'Trusted.iss'); await writeFile(trust, installerHelperTrust(files));
    const script = join(fixture, 'test.iss');
    await writeFile(script, `[Setup]
AppId=HelperGuardTest-${randomUUID()}
AppName=Trusted helper test
AppVersion=1.0
DefaultDirName={tmp}\\unused
Uninstallable=no
CreateUninstallRegKey=no
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=${fixture}
OutputBaseFilename=setup
PrivilegesRequired=lowest
Compression=lzma2/fast
[Files]
Source: "${stage}\\*"; DestDir: "{app}"; Flags: dontcopy
[Code]
#define AppVersion "1.0"
#include "${join(root, 'installer/DestinationGuard.iss')}"
#include "${join(root, 'installer/UpdateJournal.iss')}"
procedure AddIncomingPayloadFiles(Files: TStringList);
begin
${files.map(file => "  Files.Add('" + file.path + "');").join('\n')}
end;
#include "${trust}"
#include "${join(root, 'installer/HelperGuard.iss')}"
procedure DeinitializeSetup;
begin ReleaseHelperGuards; end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Stage, Mode, Target, Marker: String; Code: Integer;
begin
  Result := ''; ExtractTemporaryFiles('{app}\\*'); Stage := ExpandConstant('{tmp}') + '\\{app}';
  Mode := ExpandConstant('{param:MODE|success}'); Marker := ExpandConstant('{param:MARKER|}');
  if (Mode = 'exe') or (Mode = 'dll') or (Mode = 'manifest') or (Mode = 'bootstrapper') then begin
    if Mode = 'exe' then Target := Stage + '\\DanmuCompanion.Updater.exe'
    else if Mode = 'dll' then Target := Stage + '\\fixture.dll'
    else if Mode = 'bootstrapper' then Target := Stage + '\\MicrosoftEdgeWebview2Setup.exe' else Target := Stage + '\\manifest.json';
    DeleteFile(Target); SaveStringToFile(Target, 'untrusted replacement', False);
  end;
  AcquireHelperGuards(Stage);
  if Mode = 'locked' then begin
    if DeleteFile(Stage + '\\DanmuCompanion.Updater.exe') or
      SaveStringToFile(Stage + '\\fixture.dll', 'untrusted replacement', False) or
      RenameFile(Stage, Stage + '-replaced') then RaiseException('Execution guard allowed a write/rename race.');
  end;
  if not Exec(Stage + '\\DanmuCompanion.Updater.exe', '/d /c echo controlled-helper>"' + Marker + '"', Stage,
    SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then RaiseException('Controlled trusted helper did not run.');
  // Force the prerequisite execution branch, using a controlled trusted shell.
  if not Exec(Stage + '\\MicrosoftEdgeWebview2Setup.exe', '/d /c echo controlled-bootstrapper>"' + Marker + '.bootstrap"', Stage,
    SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then RaiseException('Controlled prerequisite did not run.');
end;
`);
    const compile = spawnSync(join(root, '.deps/inno/ISCC.exe'), [script], { cwd: root, windowsHide: true, encoding: 'utf8' });
    assert.equal(compile.status, 0, compile.stdout + compile.stderr);
    for (const mode of ['success', 'exe', 'dll', 'manifest', 'bootstrapper', 'locked']) {
      const marker = join(fixture, mode + '.marker'), log = join(fixture, mode + '.log');
      const result = spawnSync(join(fixture, 'setup.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/MODE=' + mode, '/MARKER=' + marker, '/LOG=' + log],
        { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 15000 });
      if (result.error) throw result.error;
      const didRun = await readFile(marker, 'utf8').then(text => text.includes('controlled-helper'), () => false);
      const bootstrapRan = await readFile(marker + '.bootstrap', 'utf8').then(text => text.includes('controlled-bootstrapper'), () => false);
      if (mode === 'success' || mode === 'locked') {
        assert.equal(result.status, 0, await readFile(log, 'utf8')); assert.equal(didRun, true); assert.equal(bootstrapRan, true);
      } else {
        assert.notEqual(result.status, 0); assert.equal(didRun, false, 'Untrusted extracted code must never execute: ' + mode); assert.equal(bootstrapRan, false);
      }
    }
  } finally {
    assert.ok(resolve(fixture).startsWith(resolve(parent) + sep));
    await rm(fixture, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });
  }
});
