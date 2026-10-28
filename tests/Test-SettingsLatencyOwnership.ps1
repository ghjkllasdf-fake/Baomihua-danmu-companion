#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'scripts\Test-SettingsLatency.ps1'))
$seam='$root=Split-Path $PSScriptRoot -Parent'
if(($source.Split($seam).Count-1) -ne 1){ throw 'Latency-launcher root seam changed.' }
$fixture=Join-Path $root ('.cache\latency-owner-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture '.cache'))
$driver=Join-Path $fixture 'Test-SettingsLatency.ps1'
[IO.File]::WriteAllText($driver,$source.Replace($seam,('$root='''+$fixture.Replace("'","''")+'''')))
$self=[Diagnostics.Process]::GetCurrentProcess()
$active=Join-Path $fixture '.cache\settings-latency-active.json'
function Invoke-Fixture {
    $text=& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File $driver 2>&1 | Out-String
    [pscustomobject]@{ExitCode=$LASTEXITCODE;Text=$text}
}
try {
    [pscustomobject]@{hostPid=$self.Id;startTicks=$self.StartTime.ToUniversalTime().Ticks}|ConvertTo-Json|Set-Content -LiteralPath $active
    $result=Invoke-Fixture
    if($result.ExitCode -eq 0 -or -not $result.Text.Contains('previous owned latency host is still running') -or $result.Text.Contains('Owned latency host PID:')){ throw 'An existing exact owned PID/creation-time pair did not refuse another launch.' }
    $lock=[IO.File]::Open((Join-Path $fixture '.cache\settings-latency.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { $result=Invoke-Fixture } finally { $lock.Dispose() }
    if($result.ExitCode -eq 0 -or $result.Text.Contains('previous owned latency host is still running') -or $result.Text.Contains('Owned latency host PID:')){ throw 'Concurrent invocation did not stop at the exclusive lease.' }
    [pscustomobject]@{hostPid=$self.Id;startTicks=($self.StartTime.ToUniversalTime().Ticks+1)}|ConvertTo-Json|Set-Content -LiteralPath $active
    $result=Invoke-Fixture
    if($result.Text.Contains('previous owned latency host is still running') -or $result.Text.Contains('Owned latency host PID:') -or $result.ExitCode -eq 0){ throw 'A recycled PID was treated as the old owned process or missing fixture sources reached launch.' }
    if($self.HasExited){ throw 'The preflight terminated a process instead of refusing.' }
} finally { $self.Dispose() }
'PASS: exact owned-process reuse and concurrent launches are refused before any GUI; PID recycling is distinguished by creation time.'
