#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\render-work-review-'+[guid]::NewGuid().ToString('N'))
foreach($part in @('tests\fixtures','ui')){[void][IO.Directory]::CreateDirectory((Join-Path $folder $part))}
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
Copy-Item -LiteralPath (Join-Path $root 'ui\dist') -Destination (Join-Path $folder 'ui') -Recurse
foreach($file in @('Test-RenderWorkQueue.ps1','Test-RendererResources.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $folder 'tests')}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures\RenderWorkQueueTests.cs.in') -Destination (Join-Path $folder 'tests\fixtures')
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$invoker=Join-Path $folder 'Invoke-Test.ps1'
[IO.File]::WriteAllText($invoker,@'
param([string]$TestScript,[string]$Group)
$ErrorActionPreference='Stop'
try { if($Group){& $TestScript -Group $Group}else{& $TestScript} } catch { [Console]::Error.WriteLine($_.Exception.GetBaseException().Message); exit 1 }
'@)
$target=Join-Path $folder 'desktop\native\Overlay.cs'
$source=[IO.File]::ReadAllText($target)
$cases=@(
    @{Name='unbounded-posts';Old='closed || pendingWork!=null || !Surface.NeedsWork';New='closed || !Surface.NeedsWork';Group='';Expected='Build work must coalesce'},
    @{Name='empty-build-jobs';Old=' || !Surface.NeedsWork';New='';Group='';Expected='Idle frames must still record cadence without empty build jobs.'},
    @{Name='filtered-duplicate-callbacks';Old='if(!Surface.FrameTick(nextTarget))return;';New='if(!nextTarget || !Surface.FrameTick(nextTarget))return;';Group='';Expected='Retain duplicate raw callbacks'},
    @{Name='uncancelled-operation';Old='pendingWork?.Abort();';New='';Group='';Expected='Closing must abort the pending dispatcher operation.'},
    @{Name='proof-freeze-waits-for-draw';Old='if(PrepareFrame() && invalidate) InvalidateVisual();';New='if(invalidate && PrepareFrame()) InvalidateVisual();';Group='Cadence';Expected='Proof expiry must freeze the clock in the composition tick without waiting for build work.'},
    @{Name='work-counted-as-frames';Old="public void StepFrame()`n    {";New="public void StepFrame()`n    {`n        FrameTick(true);";Group='Cadence';Expected='Build work must not manufacture frame interval samples.'}
)
foreach($case in $cases){
    if(($source.Split($case.Old).Count-1) -ne 1){throw ('Render-work mutation seam not unique: '+$case.Name)}
    [IO.File]::WriteAllText($target,$source.Replace($case.Old,$case.New))
    $scriptPath=Join-Path $folder ('tests\'+$(if($case.Group){'Test-RendererResources.ps1'}else{'Test-RenderWorkQueue.ps1'}))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -STA -File $invoker -TestScript $scriptPath -Group $case.Group 2>&1|Out-String -Width 240)
    [IO.File]::WriteAllText((Join-Path $folder ($case.Name+'.log')),$output)
    if($LASTEXITCODE -eq 0 -or -not ($output -replace '\s+',' ').Contains($case.Expected)){throw ('Render-work mutation survived or failed unexpectedly: '+$case.Name+"`n"+$output)}
    'PASS: rejected render-work mutation '+$case.Name
}
'Render-work mutation evidence retained at '+$folder
