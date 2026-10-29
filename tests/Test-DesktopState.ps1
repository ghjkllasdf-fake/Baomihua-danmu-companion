#requires -Version 7.6
param([ValidateSet('Requests','Admission','Settings','Document','Sequence','Snapshot','All')][string]$Group='All')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Desktop.cs'))
$update=[regex]::Match($source,'(?s)    private void UpdatePlayer\(\).*?(?=    private async Task InitializeAsync)')
$handler=[regex]::Match($source,'(?s)    private async void OnMessage\(.*?(?=\r?\n\})')
$push=[regex]::Match($source,'(?s)    private void PushState\(\).*?(?=    private async void OnMessage)')
if(-not $update.Success -or -not $handler.Success -or -not $push.Success){ throw 'Could not extract the actual desktop handlers; update the harness boundary.' }
$template=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\DesktopHarness.cs.in'))
$generated=$template.Replace('/*UPDATE_PLAYER*/',$update.Value).Replace('/*PUSH_STATE*/',$push.Value).Replace('/*ON_MESSAGE*/',$handler.Value)
$cache=Join-Path $root '.cache\tests'
[void][IO.Directory]::CreateDirectory($cache)
$path=Join-Path $cache 'DesktopHarness.cs'
[IO.File]::WriteAllText($path,$generated)
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\Api.cs'),(Join-Path $root 'desktop/native/ApiPreferences.cs'),(Join-Path $root 'desktop\native\AutomaticMatch.cs'),(Join-Path $root 'desktop\native\PlaybackModels.cs'),(Join-Path $root 'desktop\native\Preferences.cs'),$path) -ReferencedAssemblies (@(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)+(Join-Path $PSHOME 'System.Security.Cryptography.ProtectedData.dll'))
$failures=[Popcorn.DesktopStateTests]::Run($Group)
if($failures.Count){ throw ($failures -join "`n") }
'PASS: actual desktop handlers preserve media/request/settings boundaries (external dependencies are fakes).'
