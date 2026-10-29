[Setup]
AppId=DanmuBrowserPrerequisiteTests
AppName=Danmu browser prerequisite tests
AppVersion=1.0
CreateAppDir=no
CreateUninstallRegKey=no
Uninstallable=no
PrivilegesRequired=lowest
OutputDir={#ReleaseDir}
OutputBaseFilename=WebView2Prerequisite.Tests
Compression=none

[Code]
#include "..\..\installer\WebView2Prerequisite.iss"

var Results: String;
procedure CheckCase(Name: String; Passed: Boolean);
begin
  if Passed then Results := Results + Name + '=pass' + #13#10
  else Results := Results + Name + '=FAIL' + #13#10;
end;

function InitializeSetup: Boolean;
var Root: String;
begin
  Root := ExpandConstant('{param:ROOT|}');
  CheckCase('minimum-stable', IsWebView2VersionUsable('124.0.2478.35'));
  CheckCase('current-stable', IsWebView2VersionUsable('156.0.4314.50'));
  CheckCase('empty-version', not IsWebView2VersionUsable(''));
  CheckCase('zero-version', not IsWebView2VersionUsable('0.0.0.0'));
  CheckCase('old-version', not IsWebView2VersionUsable('123.0.0.0'));
  CheckCase('preview-version', not IsWebView2VersionUsable('156.0.0.0 beta'));
  CheckCase('malformed-version', not IsWebView2VersionUsable('156.0..0'));
  CheckCase('machine-precedes-user', SelectWebView2Version('123.0.0.0', '156.0.0.0') = '123.0.0.0');
  CheckCase('user-fallback', SelectWebView2Version('0.0.0.0', '156.0.0.0') = '156.0.0.0');
  CheckCase('machine-preferred-even-if-user-older', SelectWebView2Version('156.0.0.0', '123.0.0.0') = '156.0.0.0');
  CheckCase('failed-start', BrowserPrerequisiteResult(False, 0, False) <> '');
  CheckCase('failed-install', BrowserPrerequisiteResult(True, 1, False) <> '');
  CheckCase('zero-without-runtime', BrowserPrerequisiteResult(True, 0, False) <> '');
  CheckCase('success-rechecked', BrowserPrerequisiteResult(True, 0, True) = '');
  CheckCase('concurrent-install', BrowserPrerequisiteResult(True, 1, True) = '');
  CheckCase('empty-layout', LegacyPrivateBrowserProblem(Root) = '');
  ForceDirectories(Root + '\WebView2Runtime');
  CheckCase('legacy-offline-layout', LegacyPrivateBrowserProblem(Root) <> '');
  if not SaveStringToFile(ExpandConstant('{param:REPORT|}'), Results, False) then RaiseException('Cannot write isolated test results.');
  Result := False;
end;
