#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\runtime-recovery-review-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'tests'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'ui'))
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'tests'))
Copy-Item -LiteralPath (Join-Path $root 'tests/Import-NativeTypes.ps1') -Destination (Join-Path $folder 'tests')
Copy-Item -LiteralPath (Join-Path $root 'tests/New-NativeTestApplication.ps1') -Destination (Join-Path $folder 'tests')
Copy-Item -LiteralPath (Join-Path $root 'ui\dist') -Destination (Join-Path $folder 'ui') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-WebViewRuntimeRecovery.ps1') -Destination (Join-Path $folder 'tests')
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$target=Join-Path $folder 'desktop\native\Desktop.cs'
$source=[IO.File]::ReadAllText($target)
$cases=@(
    @{name='classification';old='error is WebView2RuntimeNotFoundException';new='error is not WebView2RuntimeNotFoundException';expected='Missing Runtime recovery must explain:'},
    @{name='integration';old='Failure=DescribeInitializationFailure(ex);';new='Failure=ex.Message;';expected='The actual initialization catch must use the recovery message.'},
    @{name='shared-runtime';old='系统共享运行库';new='';expected='Missing Runtime recovery must describe the shared browser distribution.'}
)
foreach($case in $cases){
    if(-not $source.Contains($case.old)){ throw ('Runtime recovery mutation no longer matches: '+$case.name) }
    [IO.File]::WriteAllText($target,$source.Replace($case.old,$case.new))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -STA -File (Join-Path $folder 'tests\Test-WebViewRuntimeRecovery.ps1') 2>&1 | Out-String)
    if($LASTEXITCODE -eq 0 -or -not $output.Contains($case.expected)){ throw ('Runtime recovery mutation survived or failed for another reason: '+$case.name) }
    [IO.File]::WriteAllText((Join-Path $folder ($case.name+'.log')),$output)
    'PASS: rejected Runtime recovery mutation '+$case.name
}
'Runtime recovery review retained at '+$folder
