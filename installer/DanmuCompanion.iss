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
UninstallLogMode=append

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DanmuCompanion.exe"; Description: "启动爆米花弹幕伴侣"; Flags: nowait postinstall skipifsilent

[Code]
#include "DestinationGuard.iss"
#include "WebView2Prerequisite.iss"
#include PayloadFileList
#include "StartupCleanup.iss"

function DestinationProblem: String;
var Registered: String; Incoming: TStringList;
begin
  Registered := '';
  RegQueryStringValue(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#UninstallKey}', 'InstallLocation', Registered);
  Result := DestinationProblemForDrive(WizardDirValue, Registered,
    LocalDriveType(AddBackslash(ExtractFileDrive(WizardDirValue))));
  if Result = '' then Result := LegacyPrivateBrowserProblem(WizardDirValue);
  if Result <> '' then Exit;
  Incoming := TStringList.Create;
  try
    AddIncomingPayloadFiles(Incoming);
    Result := PayloadPathsProblem(WizardDirValue, Incoming);
  finally Incoming.Free; end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var Problem: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Problem := DestinationProblem;
  if Problem = '' then Exit;
  if not WizardSilent then MsgBox(Problem, mbError, MB_OK);
  Log(Problem); Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer; Started: Boolean;
begin
  Result := DestinationProblem;
  if (Result <> '') or HasSharedWebView2 then Exit;
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  Started := Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := BrowserPrerequisiteResult(Started, ExitCode, HasSharedWebView2);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
begin
  if Step = usUninstall then RemoveStartupShortcut(ExpandConstant('{app}'));
end;
