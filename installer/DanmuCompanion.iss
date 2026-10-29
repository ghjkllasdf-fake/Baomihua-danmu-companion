#ifndef AppVersion
  #error AppVersion must be supplied by the release build.
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by the release build.
#endif
#ifndef PayloadFileList
  #error PayloadFileList must be supplied by the release build.
#endif

[Setup]
AppId={{604952AE-033E-4832-9071-A9F995BA8C1D}
AppName=爆米花弹幕伴侣
AppVersion={#AppVersion}
AppPublisher=ghjkllasdf-fake
VersionInfoVersion=0.1.0.2
DefaultDirName={localappdata}\Programs\BaomihuaDanmuCompanion
DefaultGroupName=爆米花弹幕伴侣
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
OutputBaseFilename=DanmuCompanion-{#AppVersion}-win-x64-setup
SetupIconFile={#PayloadDir}\assets\app.ico
UninstallDisplayIcon={app}\DanmuCompanion.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\BaomihuaDanmuCompanion
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadDir}\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{group}\爆米花弹幕伴侣"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\爆米花弹幕伴侣"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DanmuCompanion.exe"; Description: "启动爆米花弹幕伴侣（后台托盘）"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\DanmuCompanion.exe"; Parameters: "--remove-startup"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveOwnedStartup"

[Code]
#include "DestinationGuard.iss"
#include "WebView2Prerequisite.iss"
#include PayloadFileList

function DestinationProblem: String;
var
  Registered: String;
  Incoming: TStringList;
begin
  Registered := '';
  if RegKeyExists(HKCU64, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{604952AE-033E-4832-9071-A9F995BA8C1D}_is1') and
    (not RegQueryStringValue(HKCU64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{604952AE-033E-4832-9071-A9F995BA8C1D}_is1',
    'InstallLocation', Registered) or (Registered = '')) then
  begin
    Result := '已存在安装登记，但无法确认原安装位置。请先修复或卸载旧安装，未覆盖现有文件。';
    Exit;
  end;
  Result := DestinationProblemForDrive(WizardDirValue, Registered,
    LocalDriveType(AddBackslash(ExtractFileDrive(WizardDirValue))));
  if Result = '' then Result := LegacyPrivateBrowserProblem(WizardDirValue);
  if (Result = '') and FileExists(AddBackslash(WizardDirValue) + 'unins000.exe') then
  begin
    Incoming := TStringList.Create;
    try
      AddIncomingPayloadFiles(Incoming);
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
var ExitCode: Integer; Started: Boolean;
begin
  Result := DestinationProblem;
  if (Result <> '') or HasSharedWebView2 then Exit;
  Log('Shared WebView2 Runtime is missing or outdated; invoking bundled Microsoft bootstrapper.');
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  Started := Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Result := BrowserPrerequisiteResult(Started, ExitCode, HasSharedWebView2);
end;
