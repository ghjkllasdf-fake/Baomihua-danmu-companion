#ifndef AppVersion
  #error AppVersion must be supplied by the release build.
#endif
#ifndef AppFileVersion
  #error AppFileVersion must be supplied by the release build.
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by the release build.
#endif
#ifndef PayloadFileList
  #error PayloadFileList must be supplied by the release build.
#endif
#ifndef HelperFileList
  #error HelperFileList must be supplied by the release build.
#endif
#ifdef IncludeApi
  #define ProductId "{{D5C0CE54-F622-40E2-9F4A-D70D17C31BD2}"
  #define UninstallKey "{D5C0CE54-F622-40E2-9F4A-D70D17C31BD2}_is1"
  #define ProductName "爆米花弹幕伴侣 includeAPI"
  #define ProductDirectory "BaomihuaDanmuCompanion-includeAPI"
  #define ArtifactSuffix "-includeAPI"
#else
  #define ProductId "{{604952AE-033E-4832-9071-A9F995BA8C1D}"
  #define UninstallKey "{604952AE-033E-4832-9071-A9F995BA8C1D}_is1"
  #define ProductName "爆米花弹幕伴侣"
  #define ProductDirectory "BaomihuaDanmuCompanion"
  #define ArtifactSuffix ""
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=ghjkllasdf-fake
VersionInfoVersion={#AppFileVersion}
DefaultDirName={localappdata}\Programs\{#ProductDirectory}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
DisableDirPage=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
AlwaysShowDirOnReadyPage=yes
LicenseFile={#PayloadDir}\DISTRIBUTION-LICENSE.txt
OutputDir={#ReleaseDir}
OutputBaseFilename=DanmuCompanion-{#AppVersion}-win-x64{#ArtifactSuffix}-setup
SetupIconFile={#PayloadDir}\assets\app.ico
UninstallDisplayIcon={app}\DanmuCompanion.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\BaomihuaDanmuCompanion
CloseApplications=no
RestartApplications=no
SetupLogging=yes
; Payload ownership is tracked by disk identity, not old Inno filename-only logs.
UninstallLogMode=overwrite

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall; Check: AdmitUpdateFile
Source: "{#PayloadDir}\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{code:TrustedHelperName}"; Parameters: "--launch-installed ""{app}"""; Description: "启动爆米花弹幕伴侣"; Flags: postinstall skipifsilent

[Code]
#include "DestinationGuard.iss"
#include "WebView2Prerequisite.iss"
#include "UpdateJournal.iss"
#include PayloadFileList
#include HelperFileList
#include "HelperGuard.iss"
#include "ResourceRetirement.iss"

function TrustedHelperName(Param: String): String;
begin
  Result := ExpandConstant('{tmp}') + '\{app}\DanmuCompanion.Updater.exe';
end;

procedure DeinitializeSetup;
begin
  ReleaseHelperGuards;
end;

procedure CurStepChanged(Step: TSetupStep);
var Code: Integer;
begin
  if Step = ssPostInstall then begin
    if not Exec(ExpandConstant('{tmp}') + '\{app}\DanmuCompanion.Updater.exe',
      '--verify-current "' + ExpandConstant('{app}') + '" "' + UpdateDirectory + '"',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('Cannot verify installed payload ownership. Please reinstall to repair.');
    AcquireHelperGuards(ExpandConstant('{app}'));
    FinishResourceRetirement;
  end;
  if (Step = ssDone) and (ExpandConstant('{param:UPDATEJOURNAL|}') = '') then begin
      if not Exec(ExpandConstant('{tmp}') + '\{app}\DanmuCompanion.Updater.exe',
        '--finish-install "' + ExpandConstant('{app}') + '" "' + UpdateDirectory + '"',
        ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
        Log('Installed payload is valid; temporary native metadata cleanup deferred.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Allowed: TStringList;
begin
  if CurUninstallStep = usUninstall then RemoveOwnedStartupShortcut;
  if CurUninstallStep <> usPostUninstall then Exit;
  Allowed := TStringList.Create;
  try
    AddIncomingPayloadFiles(Allowed); UpdateDeleteOwnedFiles(Allowed);
  finally Allowed.Free; end;
end;

function DestinationProblem: String;
var
  Registered: String;
  Incoming: TStringList;
  Index: Integer;
begin
  PrepareUpdateJournal;
  Registered := '';
  if RegKeyExists(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#UninstallKey}') and
    (not RegQueryStringValue(HKCU64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#UninstallKey}',
    'InstallLocation', Registered) or (Registered = '')) then
  begin
    Result := '已存在安装登记，但无法确认原安装位置。请先修复或卸载旧安装，未覆盖现有文件。';
    Exit;
  end;
  Incoming := TStringList.Create;
  try
    AddIncomingPayloadFiles(Incoming);
    PrepareDirectRepair(Registered, '{#AppVersion}',
#ifdef IncludeApi
      'includeAPI',
#else
      'standard',
#endif
      Incoming);
  finally Incoming.Free; end;
  Result := DestinationProblemForDrive(WizardDirValue, Registered,
    LocalDriveType(AddBackslash(ExtractFileDrive(WizardDirValue))));
  if Result = '' then Result := LegacyPrivateBrowserProblem(WizardDirValue);
  if (Result = '') and not AllowRegisteredRepair and FileExists(AddBackslash(WizardDirValue) + 'unins000.exe') then
  begin
    Incoming := TStringList.Create;
    try
      AddIncomingPayloadFiles(Incoming);
      if UpdateDirectory <> '' then for Index := Incoming.Count - 1 downto 0 do
        if (PathComponentProblem(AddBackslash(WizardDirValue) + Incoming[Index]) = '') and UpdateOwnsNewFile(Incoming[Index]) then Incoming.Delete(Index);
      Result := PayloadCollisionProblem(WizardDirValue, Incoming);
    finally
      Incoming.Free;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Problem: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    Problem := DestinationProblem;
    if Problem <> '' then
    begin
      if not WizardSilent then MsgBox(Problem, mbError, MB_OK);
      Log(Problem);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer; Started: Boolean; Stage, Output, Arguments: String; Applied: AnsiString;
begin
  Result := DestinationProblem;
  if Result <> '' then Exit;
  PrepareResourceRetirement;
  ExtractTemporaryFiles('{app}\*');
  Stage := ExpandConstant('{tmp}') + '\{app}';
  AcquireHelperGuards(Stage);
  if ExpandConstant('{param:UPDATEJOURNAL|}') = '' then begin
    Output := ExpandConstant('{tmp}\native-apply-result.txt');
    if DirectRepairDirectory <> '' then
      Arguments := '--repair-current "' + WizardDirValue + '" "' + DirectRepairDirectory + '"'
    else Arguments := '--install-current "' + WizardDirValue + '" "' + Output + '"';
    if not Exec(Stage + '\DanmuCompanion.Updater.exe', Arguments, Stage,
      SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then begin
      Result := '原位安装／修复未完成，未确认成功。请退出伴侣并用完整同版本安装包重试；遇到外来文件冲突时先保留冲突文件，不要强行覆盖。';
      Exit;
    end;
    if DirectRepairDirectory <> '' then ReloadUpdateJournal(DirectRepairDirectory)
    else begin
      if not LoadStringFromFile(Output, Applied) then begin Result := '无法读取原生安装结果，未确认成功。'; Exit; end;
      ReloadUpdateJournal(UTF8Decode(Applied));
    end;
  end;
  if HasSharedWebView2 then Exit;
  Log('Shared WebView2 Runtime is missing or outdated; invoking bundled Microsoft bootstrapper.');
  Started := Exec(Stage + '\MicrosoftEdgeWebview2Setup.exe', '/silent /install', Stage, SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := BrowserPrerequisiteResult(Started, ExitCode, HasSharedWebView2);
end;
