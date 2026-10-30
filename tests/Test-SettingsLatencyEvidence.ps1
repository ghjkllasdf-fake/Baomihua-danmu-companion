#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'tests/Import-NativeTypes.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'fixtures\SettingsLatencyObserver.cs') -ReferencedAssemblies ($references | Select-Object -Unique)
$heartbeatType='LatencyHeartbeat' -as [type]
if($null -eq $heartbeatType){throw 'Settings measurement needs a distinct measured heartbeat phase that preserves warmup gaps.'}
$heartbeat=[Activator]::CreateInstance($heartbeatType)
$heartbeat.Track(100);$heartbeat.Track(2500);$heartbeat.BeginMeasurement(2600);$heartbeat.Track(2650);$heartbeat.Track(2700)
if($heartbeat.WarmupMaxGapMs -ne 2400 -or $heartbeat.MaxGapMs -ne 50 -or $heartbeat.Count -ne 2){throw 'Warmup gaps must be retained separately and cannot contaminate the calibrated input series.'}
$heartbeat.Track(3301);$heartbeat.BeginMeasurement(4000)
if($heartbeat.MaxGapMs -ne 601 -or $heartbeat.Count -ne 3){throw 'An already measured heartbeat gap must never be reset or discarded by another calibration.'}
$heartbeat.Track(4100)
if($heartbeat.MaxGapMs -ne 799 -or $heartbeat.Count -ne 4){throw 'A repeated calibration must not cut a measured observation gap at a new boundary.'}
$directory=Join-Path $root ('.cache\latency-evidence-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($directory)
$writer=[LatencyEvidenceWriter]::new((Join-Path $directory 'native.json'))
$writer.Write('{"sequence":1}')
$first=$writer.LastPath
$reader=[IO.File]::Open($first,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    $writer.Write('{"sequence":2}')
    if($writer.LastPath -eq $first){ throw 'A reader-held published file must never be replaced.' }
    if(([IO.File]::ReadAllText($first)|ConvertFrom-Json).sequence -ne 1){ throw 'Earlier evidence changed.' }
    if(([IO.File]::ReadAllText($writer.LastPath)|ConvertFrom-Json).sequence -ne 2){ throw 'New evidence was not published completely.' }
    if(@(Get-ChildItem -LiteralPath $directory -Filter 'native-*.json').Count -ne 2){ throw 'Missing immutable evidence.' }
} finally { $reader.Dispose() }
'PASS: immutable latency snapshots remain readable while another process retains the earlier record.'
