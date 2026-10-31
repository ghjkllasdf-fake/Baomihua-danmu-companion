import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdirSync, existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { mkdir, writeFile, readFile, copyFile, readdir, rm } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { randomUUID, createHash } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

const root = resolve(import.meta.dirname, '..');
const native = join(root, 'tests/NativeDistribution/bin/Release/net10.0-windows/win-x64/NativeDistribution.Tests.exe');
function run(exe, args, success = true) {
  const target = args.find(arg => arg.startsWith('/DIR='));
  const log = target && join(resolve(target.slice(5), '..'), 'setup-' + randomUUID() + '.log');
  if (log) { mkdirSync(resolve(log, '..'), { recursive: true });args = [...args, '/LOG=' + log]; }
  const result = spawnSync(exe, args, { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 60000 });
  if (result.error) throw result.error;
  if (success) assert.equal(result.status, 0, result.stdout + result.stderr + (log && existsSync(log) ? readFileSync(log, 'utf8').slice(-16000) : ''));
  else assert.notEqual(result.status, 0, 'Injected or guarded setup must fail.');
}
async function payload(directory, version, files) {
  await mkdir(directory, { recursive: true });
  for (const [name, bytes] of Object.entries(files)) await writeFile(join(directory, name), bytes);
  await writeFile(join(directory, 'manifest.json'), JSON.stringify({ schemaVersion: 1, version, variant: 'standard', sourceCommit: 'a'.repeat(40), sourceDirty: false,
    files: Object.entries(files).map(([path, content]) => ({ path, length: Buffer.byteLength(content), sha256: createHash('sha256').update(content).digest('hex') })) }));
}
test('actual Inno direct overwrite, missing-file repair and identity-owned uninstall across generations', { timeout: 240000 }, async () => {
  const parent = join(root, 'artifacts', 'updater-installer-tests'), fixture = join(parent, randomUUID()); await mkdir(fixture, { recursive: true });
  try {
    const id = randomUUID(), versions = ['0.1.1-beta.6', '0.1.1-beta.7', '0.1.1-beta.8'];
    const stages = [];
    for (const [index, version] of versions.entries()) {
      const stage = join(fixture, 'stage' + index); stages.push(stage);
      const files = { 'DanmuCompanion.exe': 'controlled executable ' + index, 'install-files.txt': 'DanmuCompanion.exe\r\ninstall-files.txt\r\nmanifest.json\r\nstable.dll\r\n' + (index ? 'new.dll\r\n' : ''), 'stable.dll': 'owned stable ' + index, ...(index ? { 'new.dll': 'incoming dll ' + index } : {}) };
      await payload(stage, version, files);
      const names = [...Object.keys(files), 'manifest.json'];
      const script = join(fixture, 'setup' + index + '.iss');
      await writeFile(script, `[Setup]
AppId=DanmuDirectTest-${id}
AppName=Direct overwrite test
AppVersion=${version}
DefaultDirName={tmp}\\unused
Uninstallable=yes
UninstallLogMode=${index ? 'overwrite' : 'append'}
CreateUninstallRegKey=no
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=${fixture}
OutputBaseFilename=setup${index}
PrivilegesRequired=lowest
SetupLogging=yes
[Files]
Source: "${stage}\\*"; DestDir: "{app}"; Flags: ignoreversion ${index ? 'uninsneveruninstall' : ''}; Check: CheckFile
[Code]
#include "${join(root, 'installer', 'DestinationGuard.iss')}"
#include "${join(root, 'installer', 'UpdateJournal.iss')}"
procedure AddIncoming(Files: TStringList);
begin
${names.map(name => "  Files.Add('" + name + "');").join('\n')}
end;
function CheckFile: Boolean;
var Target: String;
begin
  Target := ExpandConstant(CurrentFileName);
  if (ExpandConstant('{param:RACE|0}') = '1') and (ExtractFileName(Target) = 'new.dll') then begin
    DeleteFile(Target); SaveStringToFile(Target, 'foreign replacement', False);
  end;
  if ${index} = 0 then Result := True else Result := AdmitUpdateFile;
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Incoming: TStringList; Registered, Arguments, Output: String; Code: Integer; Applied: AnsiString;
begin
  Result := ''; if ${index} = 0 then Exit;
  PrepareUpdateJournal;
  Registered := ''; if ExpandConstant('{param:REGISTERED|0}') = '1' then Registered := WizardDirValue;
  Incoming := TStringList.Create;
  try
    AddIncoming(Incoming); PrepareDirectRepair(Registered, '${version}', 'standard', Incoming);
    Result := DestinationProblemForDrive(WizardDirValue, Registered, 3);
  finally Incoming.Free; end;
  if Result <> '' then Exit;
  if ExpandConstant('{param:UPDATEJOURNAL|}') = '' then begin
    Output := ExpandConstant('{tmp}\\native-result.txt');
    if DirectRepairDirectory <> '' then Arguments := '--installer-update-fixture repair-current "' + WizardDirValue + '" "' + DirectRepairDirectory + '" "${stage}"'
    else Arguments := '--installer-update-fixture install-current "' + WizardDirValue + '" "${stage}" "' + Output + '"';
    if not Exec('${native}', Arguments, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then begin Result := 'Native install/repair failed; foreign files were preserved.'; Exit; end;
    if DirectRepairDirectory <> '' then ReloadUpdateJournal(DirectRepairDirectory)
    else begin if not LoadStringFromFile(Output, Applied) then RaiseException('Missing native result'); ReloadUpdateJournal(UTF8Decode(Applied)); end;
  end;
end;
procedure CurStepChanged(Step: TSetupStep);
var Code: Integer;
begin
  if (Step = ssPostInstall) and (${index} > 0) then begin
    if not Exec('${native}', '--installer-update-fixture accept "' + UpdateDirectory + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then RaiseException('Ownership verification failed.');
  end;
  if (Step = ssDone) and (${index} > 0) and (ExpandConstant('{param:UPDATEJOURNAL|}') = '') then begin
      if not Exec('${native}', '--installer-update-fixture finish "' + UpdateDirectory + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then RaiseException('Metadata finalization failed.');
  end;
end;
procedure CurUninstallStepChanged(Step: TUninstallStep);
var Allowed: TStringList;
begin
  if Step <> usPostUninstall then Exit;
  Allowed := TStringList.Create;
  try AddIncoming(Allowed); UpdateDeleteOwnedFiles(Allowed); finally Allowed.Free; end;
end;
`);
      run(join(root, '.deps/inno/ISCC.exe'), [script]);
    }
    async function prepare(base, stage = stages[1]) {
      const application = join(base, 'app');
      run(join(fixture, 'setup0.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/DIR=' + application]);
      await writeFile(join(application, 'foreign.txt'), 'foreign sentinel');
      const operation = join(base, '.DanmuCompanion-updates', randomUUID().replaceAll('-', ''));await mkdir(operation, { recursive: true });
      await writeFile(join(operation, 'owner.json'), JSON.stringify({ schemaVersion: 1, component: 'github-updater', id: operation.split(/[\\/]/).at(-1), purpose: 'direct-update' }));
      run(native, ['--installer-update-fixture', 'prepare', application, stage, operation]);return { application, operation };
    }
    for (const gap of ['DanmuCompanion.exe', 'manifest.json', 'install-files.txt']) {
      const { application, operation } = await prepare(join(fixture, 'repair-' + gap));
      run(native, ['--installer-update-fixture', 'delete-gap', operation, gap]);
      assert.equal((await readdir(application)).includes(gap), false);
      assert.equal((await readdir(operation)).includes('backup'), false);
      run(join(fixture, 'setup1.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/REGISTERED=1', '/DIR=' + application]);
      assert.equal(await readFile(join(application, gap), 'utf8'), await readFile(join(stages[1], gap), 'utf8'));
      assert.equal(await readFile(join(application, 'foreign.txt'), 'utf8'), 'foreign sentinel');
    }
    const foreign = await prepare(join(fixture, 'repair-foreign'));
    run(native, ['--installer-update-fixture', 'delete-gap', foreign.operation, 'DanmuCompanion.exe']);
    await copyFile(join(stages[1], 'new.dll'), join(foreign.application, 'new.dll'));
    run(join(fixture, 'setup1.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/REGISTERED=1', '/DIR=' + foreign.application], false);
    assert.equal(await readFile(join(foreign.application, 'new.dll'), 'utf8'), 'incoming dll 1');
    const interrupted = await prepare(join(fixture, 'repeated-repair'));
    run(native, ['--installer-update-fixture', 'partial', interrupted.operation]);
    for (let i = 0; i < 3; i++) {
      run(native, ['--installer-update-fixture', 'repair-proof-gap', interrupted.operation, 'DanmuCompanion.exe']);
      assert.equal(await readFile(join(interrupted.application, 'DanmuCompanion.exe'), 'utf8'), 'controlled executable 1');
    }
    run(join(fixture, 'setup1.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/REGISTERED=1', '/DIR=' + interrupted.application]);
    assert.equal(await readFile(join(interrupted.application, 'manifest.json'), 'utf8'), await readFile(join(stages[1], 'manifest.json'), 'utf8'));
    assert.equal(await readFile(join(interrupted.application, 'foreign.txt'), 'utf8'), 'foreign sentinel');
    for (const scenario of ['owned', 'foreign-identical', 'foreign-different', 'late-race', '中文 Ωé']) {
      const { application, operation } = await prepare(join(fixture, scenario));
      run(native, ['--installer-update-fixture', 'create-new', operation]);
      assert.equal((await readdir(operation)).includes('backup'), false);
      run(join(fixture, 'setup1.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/REGISTERED=1', '/DIR=' + application, '/UPDATEJOURNAL=' + operation, ...(scenario === 'late-race' ? ['/RACE=1'] : [])], scenario !== 'late-race');
      if (scenario === 'late-race') { assert.equal(await readFile(join(application, 'new.dll'), 'utf8'), 'foreign replacement');continue; }
      const second = join(resolve(operation, '..'), randomUUID().replaceAll('-', ''));await mkdir(second);
      await writeFile(join(second, 'owner.json'), JSON.stringify({ schemaVersion: 1, component: 'github-updater', id: second.split(/[\\/]/).at(-1), purpose: 'direct-update' }));
      run(native, ['--installer-update-fixture', 'prepare', application, stages[2], second]);run(native, ['--installer-update-fixture', 'create-new', second]);
      run(join(fixture, 'setup2.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/REGISTERED=1', '/DIR=' + application, '/UPDATEJOURNAL=' + second]);
      const foreignBytes = scenario === 'foreign-identical' ? 'owned stable 2' : 'foreign changed';
      if (scenario.startsWith('foreign')) for (const name of ['stable.dll', 'new.dll']) { await rm(join(application, name));await writeFile(join(application, name), foreignBytes); }
      const log = join(resolve(application, '..'), 'uninstall.log');run(join(application, 'unins000.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/LOG=' + log]);
      for (let i = 0; i < 150; i++) { if ((await readFile(log, 'utf8')).includes('Log closed.')) break;await delay(200); }
      assert.equal(await readFile(join(application, 'foreign.txt'), 'utf8'), 'foreign sentinel');
      for (const name of ['stable.dll', 'new.dll']) {
        if (scenario.startsWith('foreign')) assert.equal(await readFile(join(application, name), 'utf8'), foreignBytes);
        else assert.equal((await readdir(application)).includes(name), false, 'Owned native path must be removed by actual uninstaller, including formerly Inno-owned paths.');
      }
    }
  } finally {
    assert.ok(resolve(fixture).startsWith(resolve(parent) + sep));await rm(fixture, { recursive: true, force: true, maxRetries: 20, retryDelay: 200 });
  }
});
