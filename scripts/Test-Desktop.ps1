#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
[void][IO.Directory]::CreateDirectory((Join-Path $root '.cache'))
$lease=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,9229)
try { $lease.Start() } finally { $lease.Stop() }
$out=Join-Path $root '.cache\desktop-test.out'; $err=Join-Path $root '.cache\desktop-test.err'
$process=Start-Process -FilePath (Join-Path $root '.deps\pwsh\pwsh.exe') -ArgumentList @('-NoProfile','-STA','-WindowStyle','Hidden','-File',('"'+(Join-Path $root 'desktop\Host.ps1')+'"'),'-SmokeSeconds','420','-DebugPort','9229','-DataProfile','desktop-api-test') -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
Write-Output ('Desktop integration process: '+$process.Id)
Push-Location -LiteralPath (Join-Path $root 'ui')
try { & node '.\e2e\desktop-live.mjs'; $testCode=$LASTEXITCODE } finally { Pop-Location }
if(-not $process.WaitForExit(10000)){ throw ('Desktop test process is still live: '+$process.Id+'. Inspect it before retrying; do not restart blindly.') }
Get-Content -LiteralPath $out; Get-Content -LiteralPath $err
if($testCode -ne 0 -or $process.ExitCode -ne 0){ throw 'Real desktop/API integration failed.' }
