#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\player-dispatch-review-'+[guid]::NewGuid().ToString('N'))
foreach($part in @('desktop\native','tests\fixtures')){[void][IO.Directory]::CreateDirectory((Join-Path $folder $part))}
Copy-Item (Join-Path $root 'desktop\native\Desktop.cs') (Join-Path $folder 'desktop\native')
Copy-Item (Join-Path $PSScriptRoot 'Test-PlayerUpdateDispatch.ps1') (Join-Path $folder 'tests')
Copy-Item (Join-Path $PSScriptRoot 'fixtures\PlayerUpdateDispatchTests.cs.in') (Join-Path $folder 'tests\fixtures')
$invoker=Join-Path $folder 'Invoke-Test.ps1'
[IO.File]::WriteAllText($invoker,@'
param([string]$TestScript)
$ErrorActionPreference='Stop'
try { & $TestScript } catch { [Console]::Error.WriteLine($_.Exception.GetBaseException().Message); exit 1 }
'@)
$target=Join-Path $folder 'desktop\native\Desktop.cs'
$source=[IO.File]::ReadAllText($target)
$cases=@(
    @{name='unbounded-posts';old='Interlocked.Exchange(ref playerUpdateQueued,1)!=0';new='false';expected='Probe notifications must coalesce into one pending UI post.'},
    @{name='closed-overlay';old='if(closing || IsDisposed) return;';new='';expected='A queued update must not touch the overlay after closing.'}
)
foreach($case in $cases){
    if(-not $source.Contains($case.old)){throw ('Dispatch mutation no longer matches: '+$case.name)}
    [IO.File]::WriteAllText($target,$source.Replace($case.old,$case.new))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -MTA -File $invoker -TestScript (Join-Path $folder 'tests\Test-PlayerUpdateDispatch.ps1') 2>&1|Out-String)
    [IO.File]::WriteAllText((Join-Path $folder ($case.name+'.log')),$output)
    if($LASTEXITCODE -eq 0 -or -not ($output -replace '\s+',' ').Contains($case.expected)){throw ('Dispatch mutation survived or failed unexpectedly: '+$case.name)}
    'PASS: rejected player dispatch mutation '+$case.name
}
'Dispatch review retained at '+$folder
