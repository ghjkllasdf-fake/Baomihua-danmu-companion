#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\PlayerSampler.cs'))
$boundary='NativeWindows.FindPlayer(media.PreferredTitle)'
if(($source.Split($boundary).Count-1) -ne 1){ throw 'Sampler lookup boundary changed; refusing to run player APIs.' }
$folder=Join-Path $root ('.cache\sampler-boundary-'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($folder)
$sampler=Join-Path $folder 'PlayerSampler.cs'
[IO.File]::WriteAllText($sampler,$source.Replace($boundary,'PlayerSamplerBoundaryTests.FindPlayer()'))
$sdk=Join-Path $root '.deps\windows-sdk\lib\net8.0'
foreach($name in @('WindowsBase','UIAutomationClient','UIAutomationTypes')){ Add-Type -AssemblyName $name }
Add-Type -LiteralPath (Join-Path $sdk 'WinRT.Runtime.dll')
Add-Type -LiteralPath (Join-Path $sdk 'Microsoft.Windows.SDK.NET.dll')
$references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
$references+=@(Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll'|Where-Object {$_.Name -match '^(WindowsBase|UIAutomation|System\.Windows)'}|ForEach-Object FullName)
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','ProbeOperation.cs','PlaybackEventSignal.cs','MediaSession.cs','Windows.cs','PlayerControlCache.cs','UiaPlayerControls.cs')|ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
Add-Type -LiteralPath @($paths+$sampler+(Join-Path $PSScriptRoot 'fixtures\PlayerSamplerBoundaryTests.cs')) -ReferencedAssemblies ($references|Select-Object -Unique)
[Popcorn.PlayerSamplerBoundaryTests]::Run()
'PASS: actual sampler/SMTC error filters with only the window lookup replaced; no player APIs or windows.'
