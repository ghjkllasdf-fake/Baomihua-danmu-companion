#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\UiaPlayerControls.cs'))
$dependency='using System.Windows.Automation;'
if(($source.Split($dependency).Count-1) -ne 1 -or ($source.Split('Monotonic.Now').Count-1) -lt 3){throw 'UIA dependency/clock seams changed; refusing native API activation.'}
$folder=Join-Path $root ('.cache\uia-controls-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($folder)
$copy=Join-Path $folder 'UiaPlayerControls.cs'
[IO.File]::WriteAllText($copy,$source.Replace($dependency,'using FakeAutomation;').Replace('Monotonic.Now','FakeAutomation.Telemetry.Now'))
$paths=@('Core.cs','PlaybackModels.cs','PlayerControlCache.cs')|ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
Add-Type -Path @($paths+$copy+(Join-Path $PSScriptRoot 'fixtures\UiaPlayerControlsTests.cs'))
[Popcorn.UiaPlayerControlsTests]::Run()
'PASS: actual control-selection/read algorithms with only UIA and the clock replaced; no native APIs.'
