#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\api-deadline-review-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'desktop\native'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'tests\fixtures'))
foreach($name in @('Core.cs','Api.cs')){ Copy-Item -LiteralPath (Join-Path $root ('desktop\native\'+$name)) -Destination (Join-Path $folder 'desktop\native') }
Copy-Item -LiteralPath (Join-Path $root 'tests\Test-ApiTimeout.ps1') -Destination (Join-Path $folder 'tests')
Copy-Item -LiteralPath (Join-Path $root 'tests\fixtures\ApiTimeoutTests.cs') -Destination (Join-Path $folder 'tests\fixtures')
$target=Join-Path $folder 'desktop\native\Api.cs'
$source=[IO.File]::ReadAllText($target)
$cases=@(
    @{name='body-token';old='stream.ReadAsync(chunk,requestStop)';new='stream.ReadAsync(chunk,cancellation)';expected='A stalled response body did not honor'},
    @{name='deadline';old='if(client.Timeout!=Timeout.InfiniteTimeSpan) deadline.CancelAfter(client.Timeout);';new='';expected='A stalled response body did not honor'},
    @{name='caller-cancellation';old='!cancellation.IsCancellationRequested && (deadline.IsCancellationRequested';new='true && (deadline.IsCancellationRequested';expected='Explicit caller cancellation must remain cancellation'}
)
foreach($case in $cases){
    if(-not $source.Contains($case.old)){ throw ('API deadline mutation no longer matches: '+$case.name) }
    [IO.File]::WriteAllText($target,$source.Replace($case.old,$case.new))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $folder 'tests\Test-ApiTimeout.ps1') 2>&1|Out-String)
    if($LASTEXITCODE -eq 0 -or -not $output.Contains($case.expected)){ throw ('API deadline mutation survived or failed for another reason: '+$case.name) }
    [IO.File]::WriteAllText((Join-Path $folder ($case.name+'.log')),$output)
    'PASS: rejected API deadline mutation '+$case.name
}
'API deadline review retained at '+$folder
