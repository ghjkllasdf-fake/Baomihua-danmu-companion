#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\incremental-render-review-'+[guid]::NewGuid().ToString('N'))
foreach($part in @('tests','ui')){[void][IO.Directory]::CreateDirectory((Join-Path $folder $part))}
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
Copy-Item -LiteralPath (Join-Path $root 'ui\dist') -Destination (Join-Path $folder 'ui') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-RendererResources.ps1') -Destination (Join-Path $folder 'tests')
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$invoker=Join-Path $folder 'Invoke-Test.ps1'
[IO.File]::WriteAllText($invoker,@'
param([string]$TestScript)
$ErrorActionPreference='Stop'
try { & $TestScript -Group Incremental } catch { [Console]::Error.WriteLine($_.Exception.GetBaseException().Message); exit 1 }
'@)
$target=Join-Path $folder 'desktop\native\Overlay.cs'
$source=[IO.File]::ReadAllText($target)
$cases=@(
    @{name='unstarted-layout-ready';old='LayoutPending:!detached&&(rebuild||pendingFrame!=null)';new='LayoutPending:!detached&&pendingFrame!=null';expected='An unstarted rebuild must not claim a settled layout.'},
    @{name='partial-layout-ready';old='LayoutPending:!detached&&(rebuild||pendingFrame!=null)';new='LayoutPending:!detached&&rebuild';expected='A paused partial layout must remain explicitly preparing.'},
    @{name='presentation-bulk-path';old='if (IsVisible) Surface.StepFrame();';new='if (IsVisible) Surface.Frame();';expected='The actual presentation callback must use the incremental production path.'},
    @{name='combined-expensive-steps';old="geometryBuilds++;`n                yield return 0;";new='geometryBuilds++;';expected='A production step must not build the whole comment batch.'},
    @{name='uncancelled-settings';old='public void Rebuild(bool preserve=false) { CancelFrame();';new='public void Rebuild(bool preserve=false) {';expected='A settings generation must cancel its unfinished layout before returning.'},
    @{name='uncancelled-detach';old='public void Detach() { CancelFrame();';new='public void Detach() {';expected='Detach must cancel pending work and prevent later pixel allocation.'},
    @{name='blank-hot-layout';old='if(!preserveDisplay) {ReleaseActive();layoutSettings=settings;}';new='ReleaseActive();layoutSettings=settings;';expected='A hot visual rebuild must retain one complete prior layout while replacements are prepared.'},
    @{name='lost-budget-fallback';old='pendingFrame=rebuild ? BuildFrame(generation).GetEnumerator() : null;';new='pendingFrame=null;';expected='A budget that fits only one layout must still finish the latest font after rejecting the temporary double layout.'}
)
foreach($case in $cases){
    if(-not $source.Contains($case.old)){throw ('Incremental mutation no longer matches: '+$case.name)}
    [IO.File]::WriteAllText($target,$source.Replace($case.old,$case.new))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -STA -File $invoker -TestScript (Join-Path $folder 'tests\Test-RendererResources.ps1') 2>&1|Out-String)
    [IO.File]::WriteAllText((Join-Path $folder ($case.name+'.log')),$output)
    if($LASTEXITCODE -eq 0 -or -not ($output -replace '\s+',' ').Contains($case.expected)){throw ('Incremental mutation survived or failed unexpectedly: '+$case.name)}
    'PASS: rejected incremental-render mutation '+$case.name
}
'Incremental review retained at '+$folder
