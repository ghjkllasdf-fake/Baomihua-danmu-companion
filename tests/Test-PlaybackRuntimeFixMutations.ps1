#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\playback-runtime-review-'+[guid]::NewGuid().ToString('N'))
foreach($part in @('desktop\native','tests\fixtures')){ [void][IO.Directory]::CreateDirectory((Join-Path $folder $part)) }
foreach($name in @('Core.cs','PlaybackModels.cs','PlayerControlCache.cs','UiaPlayerControls.cs')){
    Copy-Item -LiteralPath (Join-Path $root ('desktop\native\'+$name)) -Destination (Join-Path $folder 'desktop\native')
}
foreach($name in @('Test-Core.ps1','Test-UiaPlayerControls.ps1')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $folder 'tests')
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\UiaPlayerControlsTests.cs') -Destination (Join-Path $folder 'tests\fixtures')
$invoker=Join-Path $folder 'Invoke-Test.ps1'
[IO.File]::WriteAllText($invoker,@'
param([string]$TestScript)
$ErrorActionPreference='Stop'
try { & $TestScript } catch { [Console]::Error.WriteLine($_.Exception.GetBaseException().Message); exit 1 }
'@)
$core=[IO.File]::ReadAllText((Join-Path $folder 'desktop\native\Core.cs'))
$uia=[IO.File]::ReadAllText((Join-Path $folder 'desktop\native\UiaPlayerControls.cs'))
$cases=@(
    @{name='normal-state-transition';file='Core.cs';source=$core;old='!transitionWithinInterval && Math.Abs(drift)';new='Math.Abs(drift)';test='Test-Core.ps1';expected='Normal pause/resume anywhere within the sampled interval must not become a seek.'},
    @{name='discovery-current-rpcs';file='UiaPlayerControls.cs';source=$uia;old='var value=found[i].Cached;';new='var value=found[i].Current;';test='Test-UiaPlayerControls.ps1';expected='Discovery must batch selection properties rather than query each current property across processes.'},
    @{name='stale-playback-values';file='UiaPlayerControls.cs';source=$uia;old='var current=element.GetUpdatedCache(Properties()).Cached;';new='var current=element.Cached;';test='Test-UiaPlayerControls.ps1';expected='Discovery-time cached labels must never become playback evidence.'},
    @{name='separate-current-queries';file='UiaPlayerControls.cs';source=$uia;old='var current=element.GetUpdatedCache(Properties()).Cached;';new='var current=element.Current;';test='Test-UiaPlayerControls.ps1';expected='Each playback read must obtain one new metadata cache rather than three separate current-property queries.'},
    @{name='unfiltered-provider';file='UiaPlayerControls.cs';source=$uia;old='found=root.FindAll(TreeScope.Descendants,standard);';new='found=root.FindAll(TreeScope.Descendants,Condition.TrueCondition);';test='Test-UiaPlayerControls.ps1';expected='Standard playback discovery must filter unrelated descendants at the provider.'},
    @{name='missing-combined-fallback';file='UiaPlayerControls.cs';source=$uia;old='if(!progress) found=root.FindAll(TreeScope.Descendants,Condition.TrueCondition);';new='if(!progress) GC.KeepAlive(found);';test='Test-UiaPlayerControls.ps1';expected='Missing standard progress controls must preserve the complete-tree combined-label fallback.'}
)
foreach($case in $cases){
    if(-not $case.source.Contains($case.old)){ throw ('Playback mutation no longer matches: '+$case.name) }
    [IO.File]::WriteAllText((Join-Path $folder 'desktop\native\Core.cs'),$core)
    [IO.File]::WriteAllText((Join-Path $folder 'desktop\native\UiaPlayerControls.cs'),$uia)
    [IO.File]::WriteAllText((Join-Path $folder ('desktop\native\'+$case.file)),$case.source.Replace($case.old,$case.new))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -MTA -File $invoker -TestScript (Join-Path $folder ('tests\'+$case.test)) 2>&1|Out-String)
    [IO.File]::WriteAllText((Join-Path $folder ($case.name+'.log')),$output)
    if($LASTEXITCODE -eq 0 -or -not ($output -replace '\s+',' ').Contains($case.expected)){ throw ('Playback mutation survived or failed unexpectedly: '+$case.name) }
    'PASS: rejected playback runtime mutation '+$case.name
}
'Playback review retained at '+$folder
