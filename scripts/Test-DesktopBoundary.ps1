#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$cache=Join-Path $root '.cache'
[void][IO.Directory]::CreateDirectory($cache)
$lease=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,9231)
try { $lease.Start() } finally { $lease.Stop() }
$legacyFixture=[IO.File]::ReadAllText((Join-Path $root 'tests\Test-ApiRequest.ps1'))
$fixture=[regex]::Match($legacyFixture,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@")
if(-not $fixture.Success){ throw 'HTTP fixture source unavailable.' }
Add-Type -TypeDefinition $fixture.Groups[1].Value
$server=[DanmuTestApi]::new()
$previousBase=$env:DANMU_TEST_BASE; $env:DANMU_TEST_BASE=$server.Base
$process=$null; $lock=$null
function Stage([string]$name){
    Push-Location -LiteralPath (Join-Path $root 'ui')
    try { & node '.\e2e\desktop-boundary.mjs' $name; if($LASTEXITCODE -ne 0){ throw ('Desktop boundary stage failed: '+$name) } }
    finally { Pop-Location }
}
try {
    $process=Start-Process -FilePath (Join-Path $root '.deps\pwsh\pwsh.exe') -ArgumentList @('-NoProfile','-STA','-WindowStyle','Hidden','-File',('"'+(Join-Path $root 'desktop\Host.ps1')+'"'),'-SmokeSeconds','120','-DebugPort','9231','-DataProfile','desktop-boundary-test','-NoDefaultApi') -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $cache 'boundary.out') -RedirectStandardError (Join-Path $cache 'boundary.err') -PassThru
    Write-Output ('Boundary test process: '+$process.Id)
    Stage 'prepare'
    $preferences=Join-Path $cache 'desktop-boundary-test\preferences.json'
    $lock=[IO.File]::Open($preferences,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { Stage 'locked' } finally { $lock.Dispose(); $lock=$null }
    Stage 'verify'
} finally {
    if($lock){ $lock.Dispose() }
    if($process -and -not $process.HasExited){
        try { Stage 'close' } catch { Write-Warning $_.Exception.Message }
        if(-not $process.WaitForExit(10000)){ Write-Warning ('Boundary test is still live: '+$process.Id+'. Inspect this process before retrying.') }
    }
    $server.Dispose(); $env:DANMU_TEST_BASE=$previousBase
}
Get-Content -LiteralPath (Join-Path $cache 'boundary.out')
Get-Content -LiteralPath (Join-Path $cache 'boundary.err')
if(-not $process.HasExited -or $process.ExitCode -ne 0){ throw 'Boundary test desktop did not exit cleanly.' }
'PASS: real PS7/WebView2/React boundary integration with isolated preferences and a loopback-only API.'
