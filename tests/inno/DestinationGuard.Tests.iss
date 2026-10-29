[Setup]
AppId=DanmuDestinationGuardTests
AppName=Danmu destination guard tests
AppVersion=1.0
CreateAppDir=no
CreateUninstallRegKey=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir={#ReleaseDir}
OutputBaseFilename=DestinationGuard.Tests
Compression=none

[Code]
#include "..\..\installer\DestinationGuard.iss"

var
  Results: String;

procedure CheckCase(Name: String; Passed: Boolean);
begin
  if Passed then Results := Results + Name + '=pass' + #13#10
  else Results := Results + Name + '=FAIL' + #13#10;
end;

function InitializeSetup: Boolean;
var
  Root, ReportPath, Empty, Occupied, Portable, Registered, NewTarget: String;
  Incoming: TStringList;
begin
  Root := ExpandConstant('{param:ROOT|}');
  ReportPath := ExpandConstant('{param:REPORT|}');
  if (Root = '') or (ReportPath = '') then RaiseException('Missing isolated test paths.');
  Empty := Root + '\empty'; Occupied := Root + '\occupied';
  Portable := Root + '\portable'; Registered := Root + '\registered';
  NewTarget := Root + '\new target';
  ForceDirectories(Empty); ForceDirectories(Occupied);
  ForceDirectories(Portable); ForceDirectories(Registered);
  SaveStringToFile(Occupied + '\sentinel.txt', 'unrelated sentinel', False);
  SaveStringToFile(Portable + '\DanmuCompanion.exe', 'portable sentinel', False);
  SaveStringToFile(Portable + '\manifest.json', '{}', False);
  SaveStringToFile(Registered + '\DanmuCompanion.exe', 'owned placeholder', False);
  SaveStringToFile(Registered + '\manifest.json', '{}', False);
  SaveStringToFile(Registered + '\unins000.exe', 'owned uninstaller placeholder', False);
  SaveStringToFile(Registered + '\user-note.txt', 'must survive', False);
  CheckCase('new-directory', DestinationProblemForDrive(NewTarget, '', 3) = '');
  CheckCase('registered-install-cannot-be-orphaned', DestinationProblemForDrive(NewTarget, Registered, 3) <> '');
  CheckCase('empty-directory', DestinationProblemForDrive(Empty, '', 3) = '');
  CheckCase('unrelated-nonempty', DestinationProblemForDrive(Occupied, '', 3) <> '');
  CheckCase('portable-is-not-installation', DestinationProblemForDrive(Portable, '', 3) <> '');
  CheckCase('registered-upgrade', DestinationProblemForDrive(Registered, Registered, 3) = '');
  CheckCase('other-registered-path', DestinationProblemForDrive(Portable, Registered, 3) <> '');
  CheckCase('missing-ownership-files', DestinationProblemForDrive(Occupied, Occupied, 3) <> '');
  CheckCase('drive-root', DestinationProblemForDrive(ExtractFileDrive(Root) + '\', '', 3) <> '');
  CheckCase('unc', DestinationProblemForDrive('\\example.invalid\share\app', '', 3) <> '');
  CheckCase('mapped-network-drive', DestinationProblemForDrive('Z:\app', '', 4) <> '');
  CheckCase('missing-drive', DestinationProblemForDrive('Z:\app', '', 1) <> '');
  SaveStringToFile(Root + '\existing-file', 'keep', False);
  CheckCase('existing-file', DestinationProblemForDrive(Root + '\existing-file', '', 3) <> '');
  Incoming := TStringList.Create;
  try
    Incoming.Add('DanmuCompanion.exe');
    CheckCase('missing-upgrade-ownership-list', PayloadCollisionProblem(Registered, Incoming) <> '');
    SaveStringToFile(Registered + '\install-files.txt', 'DanmuCompanion.exe' + #13#10 + 'manifest.json' + #13#10, False);
    CheckCase('owned-upgrade-file', PayloadCollisionProblem(Registered, Incoming) = '');
    Incoming.Add('new-file.dll');
    CheckCase('new-unoccupied-file', PayloadCollisionProblem(Registered, Incoming) = '');
    Incoming.Add('user-note.txt');
    CheckCase('new-file-collides-with-foreign-file', PayloadCollisionProblem(Registered, Incoming) <> '');
    Incoming.Clear; Incoming.Add('user-note.txt\child.dll');
    CheckCase('parent-collides-with-foreign-file', PayloadCollisionProblem(Registered, Incoming) <> '');
    Incoming.Clear; Incoming.Add('existing-directory.dll');
    ForceDirectories(Registered + '\existing-directory.dll');
    CheckCase('file-collides-with-directory', PayloadCollisionProblem(Registered, Incoming) <> '');
    Incoming.Clear; Incoming.Add('linked-assets\sentinel.txt');
    SaveStringToFile(Registered + '\install-files.txt', 'linked-assets\sentinel.txt' + #13#10, False);
    CheckCase('owned-file-through-junction', PayloadCollisionProblem(Registered, Incoming) <> '');
    CheckCase('new-directory-below-junction', DestinationProblemForDrive(Root + '\alias\new-app', '', 3) <> '');
  finally
    Incoming.Free;
  end;
  if not SaveStringToFile(ReportPath, Results, False) then RaiseException('Cannot save guard test results.');
  Result := False;
end;
