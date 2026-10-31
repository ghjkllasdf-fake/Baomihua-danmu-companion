#define UninstallKey "ResourceRetirementTests-not-used"
[Setup]
AppId=DanmuResourceRetirementTests
AppName=Resource retirement tests
AppVersion=1
CreateAppDir=no
CreateUninstallRegKey=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir={#ReleaseDir}
OutputBaseFilename=ResourceRetirement.Tests
Compression=none
[Code]
#include "..\..\installer\DestinationGuard.iss"
#include "..\..\installer\UpdateJournal.iss"
procedure AddIncomingPayloadFiles(Files: TStringList);
begin Files.Add('zh-Hans\System.Windows.Forms.resources.dll'); end;
#include "..\..\installer\ResourceRetirement.iss"
function InitializeSetup: Boolean;
var Root, Snapshot, Report: String; Records: AnsiString; Owned, Incoming: TStringList;
begin
  Root := ExpandConstant('{param:ROOT|}'); Snapshot := ExpandConstant('{param:SNAPSHOT|}'); Report := ExpandConstant('{param:REPORT|}');
  if (Root = '') or (Snapshot = '') or (Report = '') then RaiseException('Missing controlled fixture paths.');
  Owned := TStringList.Create; Incoming := TStringList.Create;
  try
    Owned.Add('de\System.Windows.Forms.resources.dll'); Owned.Add('zh-Hans\System.Windows.Forms.resources.dll');
    Owned.Add('de\..\personal.txt'); Owned.Add('de\personal.txt'); Owned.Add('de\System.Windows.Forms.resources.dll');
    AddIncomingPayloadFiles(Incoming);
    if not CaptureRetiredResources(Root, Snapshot, Owned, Incoming) then RaiseException('Controlled old resource snapshot was not captured.');
    if IsRetiredResource('zh-Hans\System.Windows.Forms.resources.dll') or IsRetiredResource('en\System.Windows.Forms.resources.dll') or
      IsRetiredResource('de\..\personal.resources.dll') or IsRetiredResource('de\personal.txt') then RaiseException('Resource classifier admitted an unsafe or retained path.');
    if CaptureRetiredResources(Root, Snapshot, Owned, Incoming) then RaiseException('A snapshot must never overwrite an existing directory.');
    if not LoadStringFromFile(Snapshot + '\scope.txt', Records) then RaiseException('Snapshot record was not written.');
    SaveStringToFile(Report, 'pass', False);
  finally Owned.Free; Incoming.Free; end;
  Result := False;
end;
