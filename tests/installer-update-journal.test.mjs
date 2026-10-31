import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdir, writeFile, readFile, copyFile, readdir, rm } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { randomUUID, createHash } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

const root = resolve(import.meta.dirname, '..');
const native = join(root, 'tests/NativeDistribution/bin/Release/net10.0-windows/win-x64/NativeDistribution.Tests.exe');
function run(exe, args, success = true) {
  const result = spawnSync(exe, args, { cwd: root, windowsHide: true, encoding: 'utf8', timeout: 60000 });
  if (result.error) throw result.error;
  if (success) assert.equal(result.status, 0, result.stdout + result.stderr);
  else assert.notEqual(result.status, 0, 'Injected or guarded setup must fail.');
}
async function payload(directory, version, files) {
  await mkdir(directory, { recursive: true });
  for (const [name, bytes] of Object.entries(files)) await writeFile(join(directory, name), bytes);
  await writeFile(join(directory, 'manifest.json'), JSON.stringify({ schemaVersion: 1, version, variant: 'standard', sourceCommit: 'a'.repeat(40), sourceDirty: false,
    files: Object.entries(files).map(([path, content]) => ({ path, length: Buffer.byteLength(content), sha256: createHash('sha256').update(content).digest('hex') })) }));
}
test('actual Inno creation journals protect identical foreign files and recover interrupted new-file writes', { timeout: 120000 }, async () => {
  const parent = join(root, 'artifacts', 'updater-installer-tests');
  const fixture = join(parent, randomUUID()); await mkdir(fixture, { recursive: true });
  try {
    const incoming = join(fixture, 'incoming'); await mkdir(incoming);
    await writeFile(join(incoming, 'new.dll'), 'verified new library');
    const script = join(fixture, 'fixture.iss');
    await writeFile(script, `[Setup]
AppId=DanmuUpdateJournalTest-${randomUUID()}
AppName=Update journal test
AppVersion=1
DefaultDirName={tmp}\\not-used
Uninstallable=yes
CreateUninstallRegKey=no
DisableDirPage=yes
OutputDir=${fixture}
OutputBaseFilename=fixture
PrivilegesRequired=lowest
[Files]
Source: "${incoming}\\new.dll"; DestDir: "{app}"; Flags: ignoreversion; Check: CheckFile
[Code]
#include "${join(root, 'installer', 'DestinationGuard.iss')}"
#include "${join(root, 'installer', 'UpdateJournal.iss')}"
procedure CurUninstallStepChanged(Step: TUninstallStep);
var Allowed: TStringList;
begin
  if Step <> usPostUninstall then Exit;
  Allowed := TStringList.Create;
  try Allowed.Add('new.dll'); UpdateDeleteOwnedFiles(Allowed);
  finally Allowed.Free; end;
end;
function CheckFile: Boolean;
begin
  Result := AdmitUpdateFile;
  if (UpdateDirectory <> '') and not Result and (ExpandConstant('{param:RACE|0}') = '1') then begin
    DeleteFile(ExpandConstant(CurrentFileName));
    SaveStringToFile(ExpandConstant(CurrentFileName), 'foreign replacement', False);
  end;
  if (UpdateDirectory <> '') and not Result and (ExpandConstant('{param:FAILBEFORE|0}') = '1') then begin
    SaveStringToFile(ExpandConstant(CurrentFileName), 'partially written new file', False);
    UpdateFailed := True;
  end;
end;
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Incoming: TStringList;
begin
  PrepareUpdateJournal;
  Incoming := TStringList.Create;
  try
    if not UpdateOwnsNewFile('new.dll') then Incoming.Add('new.dll');
    Result := PayloadCollisionProblem(WizardDirValue, Incoming);
  finally Incoming.Free; end;
end;
`);
    run(join(root, '.deps/inno/ISCC.exe'), [script]);
    for (const scenario of ['success', 'protected-race', 'interrupted', 'foreign-identical']) {
      const base = join(fixture, scenario), application = join(base, 'app'), stage = join(base, 'stage');
      await payload(application, '0.1.1-beta.6', { 'DanmuCompanion.exe': 'controlled old executable', 'install-files.txt': 'DanmuCompanion.exe\r\ninstall-files.txt\r\nmanifest.json\r\n' });
      await payload(stage, '0.1.1-beta.7', { 'DanmuCompanion.exe': 'controlled new executable', 'new.dll': 'verified new library' });
      const id = randomUUID().replaceAll('-', ''), transaction = join(base, '.DanmuCompanion-updates', id); await mkdir(transaction, { recursive: true });
      await writeFile(join(transaction, 'owner.json'), JSON.stringify({ schemaVersion: 1, component: 'github-updater', id, purpose: 'transaction' }));
      run(native, ['--installer-update-fixture', 'prepare', application, stage, transaction]);
      if (scenario === 'foreign-identical') await copyFile(join(incoming, 'new.dll'), join(application, 'new.dll'));
      run(native, ['--installer-update-fixture', 'create-new', transaction], scenario !== 'foreign-identical');
      const log = join(transaction, 'setup.log');
      try {
        run(join(fixture, 'fixture.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', `/DIR=${application}`, `/UPDATEJOURNAL=${transaction}`, `/LOG=${log}`,
          ...(scenario === 'interrupted' ? ['/FAILBEFORE=1'] : []), ...(scenario === 'protected-race' ? ['/RACE=1'] : [])], scenario === 'success');
      } catch (error) { throw new Error(error.message + '\n' + await readFile(log, 'utf8'), { cause: error }); }
      const records = (await readdir(transaction)).filter(name => name.endsWith('.file-id'));
      assert.equal(records.length, scenario === 'foreign-identical' ? 0 : 1);
      if (records.length) assert.match((await readFile(join(transaction, records[0]))).toString('utf8'), /^new\.dll\|[A-F0-9]{48}$/, 'Inno record must be interoperable UTF-8: ' + (await readFile(log, 'utf8')));
      run(native, ['--installer-update-fixture', 'restore', transaction]);
      assert.equal(await readFile(join(application, 'DanmuCompanion.exe'), 'utf8'), 'controlled old executable');
      if (scenario === 'foreign-identical') assert.equal(await readFile(join(application, 'new.dll'), 'utf8'), 'verified new library');
      else if (scenario === 'protected-race') assert.equal(await readFile(join(application, 'new.dll'), 'utf8'), 'foreign replacement');
      else assert.equal((await readdir(application)).includes('new.dll'), false, 'Recorded complete/partial new bytes must be removed by disk identity.');
    }
    for (const [name, replaced] of [['uninstall-owned', false], ['uninstall-foreign', true], ['中文 安装', false], ['Ωé 安装', false]]) {
      const base = join(fixture, name), application = join(base, 'app'), stage = join(base, 'stage');
      await payload(application, '0.1.1-beta.6', { 'DanmuCompanion.exe': 'old', 'install-files.txt': 'DanmuCompanion.exe\r\ninstall-files.txt\r\nmanifest.json\r\n' });
      await payload(stage, '0.1.1-beta.7', { 'DanmuCompanion.exe': 'old', 'new.dll': 'verified new library' });
      const id = randomUUID().replaceAll('-', ''), transaction = join(base, '.DanmuCompanion-updates', id); await mkdir(transaction, { recursive: true });
      await writeFile(join(transaction, 'owner.json'), JSON.stringify({ schemaVersion: 1, component: 'github-updater', id, purpose: 'transaction' }));
      run(native, ['--installer-update-fixture', 'prepare', application, stage, transaction]);
      run(native, ['--installer-update-fixture', 'create-new', transaction]);
      run(join(fixture, 'fixture.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', `/DIR=${application}`, `/UPDATEJOURNAL=${transaction}`]);
      await copyFile(join(stage, 'manifest.json'), join(application, 'manifest.json'));
      run(native, ['--installer-update-fixture', 'accept', transaction]);
      if (replaced) { await rm(join(application, 'new.dll')); await writeFile(join(application, 'new.dll'), 'foreign successor'); }
      const uninstallLog = join(base, 'uninstall.log');
      run(join(application, 'unins000.exe'), ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', `/LOG=${uninstallLog}`]);
      for (let attempt = 0; attempt < 150; attempt++) {
        if ((await readFile(uninstallLog, 'utf8')).includes('Log closed.')) break;
        await delay(200);
      }
      if (replaced) assert.equal(await readFile(join(application, 'new.dll'), 'utf8'), 'foreign successor');
      else assert.equal((await readdir(application)).includes('new.dll'), false, 'Skipped Inno file must still be safely removed by the real uninstaller.\n' + await readFile(uninstallLog, 'utf8'));
    }
  } finally {
    assert.ok(resolve(fixture).startsWith(resolve(parent) + sep)); await rm(fixture, { recursive: true, force: true, maxRetries: 20, retryDelay: 200 });
  }
});
