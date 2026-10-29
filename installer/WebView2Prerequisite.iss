function IsWebView2VersionUsable(Value: String): Boolean;
var Index, Dots, Major: Integer; HasDigit: Boolean;
begin
  Result := False; Dots := 0; HasDigit := False;
  for Index := 1 to Length(Value) do
  begin
    if Value[Index] = '.' then
    begin
      if not HasDigit then Exit;
      Dots := Dots + 1; HasDigit := False;
    end
    else if (Value[Index] >= '0') and (Value[Index] <= '9') then HasDigit := True
    else Exit;
  end;
  if (Dots <> 3) or not HasDigit then Exit;
  Major := StrToIntDef(Copy(Value, 1, Pos('.', Value) - 1), -1);
  Result := Major >= 124;
end;

function SelectWebView2Version(MachineVersion: String; UserVersion: String): String;
begin
  if (MachineVersion <> '') and (MachineVersion <> '0.0.0.0') then Result := MachineVersion
  else Result := UserVersion;
end;

function HasSharedWebView2: Boolean;
var MachineVersion, UserVersion: String;
begin
  MachineVersion := ''; UserVersion := '';
  RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', MachineVersion);
  if not RegQueryStringValue(HKCU64, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', UserVersion) then
    RegQueryStringValue(HKCU32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', UserVersion);
  Result := IsWebView2VersionUsable(SelectWebView2Version(MachineVersion, UserVersion));
end;

function BrowserPrerequisiteResult(Started: Boolean; ExitCode: Integer; DetectedAfter: Boolean): String;
begin
  Result := '';
  if DetectedAfter then Exit;
  if not Started then Result := '无法启动微软 WebView2 安装程序。请检查安全软件提示，或从微软官网安装 Evergreen Runtime 后重试。'
  else Result := 'WebView2 安装／更新未完成（返回码 ' + IntToStr(ExitCode) + '）。请检查网络，运行 MicrosoftEdgeWebview2Setup.exe 完成安装后重试；不会继续安装本程序。';
end;

function LegacyPrivateBrowserProblem(Destination: String): String;
begin
  Result := '';
  if DirExists(AddBackslash(Destination) + 'WebView2Runtime') then
    Result := '目标位置是含私有浏览器的完整离线版。请先卸载旧版，再安装精简版，或选择新的空目录；个人配置默认保留。';
end;
