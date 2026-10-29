#ifndef AppVersion
  #error AppVersion must be supplied by the release build.
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by the release build.
#endif

[Setup]
AppId={{604952AE-033E-4832-9071-A9F995BA8C1D}
AppName=爆米花弹幕伴侣
AppVersion={#AppVersion}
AppPublisher=ghjkllasdf-fake
VersionInfoVersion=0.1.0.1
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

[Icons]
Name: "{group}\爆米花弹幕伴侣"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\爆米花弹幕伴侣"; Filename: "{app}\DanmuCompanion.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DanmuCompanion.exe"; Description: "启动爆米花弹幕伴侣（后台托盘）"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\DanmuCompanion.exe"; Parameters: "--remove-startup"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveOwnedStartup"

[Code]
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if AddBackslash(WizardDirValue) = AddBackslash(ExtractFileDrive(WizardDirValue)) then
    begin
      MsgBox('请选择专用的安装子目录，不要直接使用磁盘根目录。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
