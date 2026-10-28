#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\playback-event-review-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'tests\fixtures'))
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
foreach($name in @('Test-PlaybackEvents.ps1','Test-PlayerSamplerBoundary.ps1')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $folder 'tests')
}
foreach($name in @('PlaybackEventSignalTests.cs','PlayerSamplerBoundaryTests.cs')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('fixtures\'+$name)) -Destination (Join-Path $folder 'tests\fixtures')
}
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$invoker=Join-Path $folder 'Invoke-Test.ps1'
[IO.File]::WriteAllText($invoker,@'
param([string]$TestScript)
$ErrorActionPreference='Stop'
try { & $TestScript } catch { [Console]::Error.WriteLine($_.Exception.GetBaseException().Message); exit 1 }
'@)
$signal=[IO.File]::ReadAllText((Join-Path $folder 'desktop\native\PlaybackEventSignal.cs'))
$media=[IO.File]::ReadAllText((Join-Path $folder 'desktop\native\MediaSession.cs'))
$cases=@(
    @{name='old-callback';file='PlaybackEventSignal.cs';source=$signal;old='!ReferenceEquals(Volatile.Read(ref registration),current)';new='false';test='Test-PlaybackEvents.ps1';expected='An already queued callback from an old session must not wake the current sampler.'},
    @{name='detach';file='PlaybackEventSignal.cs';source=$signal;old='previousInfo.RemoveEventHandler(previousSource,previous.Handler);';new='GC.KeepAlive(previousSource);';test='Test-PlaybackEvents.ps1';expected='Replacing a session must detach the old event.'},
    @{name='wake';file='PlaybackEventSignal.cs';source=$signal;old='changed.Set();';new='changed.Reset();';test='Test-PlaybackEvents.ps1';expected='Playback hints must wake once and coalesce; they are not state proof.'},
    @{name='reader-link';file='MediaSession.cs';source=$media;old='changes?.Attach((object)session);';new='';test='Test-PlayerSamplerBoundary.ps1';expected='The actual media reader did not attach its current playback event.'}
)
foreach($case in $cases){
    if(-not $case.source.Contains($case.old)){ throw ('Event mutation no longer matches: '+$case.name) }
    [IO.File]::WriteAllText((Join-Path $folder 'desktop\native\PlaybackEventSignal.cs'),$signal)
    [IO.File]::WriteAllText((Join-Path $folder 'desktop\native\MediaSession.cs'),$media)
    [IO.File]::WriteAllText((Join-Path $folder ('desktop\native\'+$case.file)),$case.source.Replace($case.old,$case.new))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -MTA -File $invoker -TestScript (Join-Path $folder ('tests\'+$case.test)) 2>&1|Out-String)
    [IO.File]::WriteAllText((Join-Path $folder ($case.name+'.log')),$output)
    if($LASTEXITCODE -eq 0 -or -not ($output -replace '\s+',' ').Contains($case.expected)){ throw ('Event mutation survived or failed unexpectedly: '+$case.name) }
    'PASS: rejected playback event mutation '+$case.name
}
'Event review retained at '+$folder
