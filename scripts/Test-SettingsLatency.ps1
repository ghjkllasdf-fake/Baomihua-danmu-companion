#requires -Version 7.6
param([switch]$PlaybackLoad)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
[void][IO.Directory]::CreateDirectory((Join-Path $root '.cache'))
$runLock=[IO.File]::Open((Join-Path $root '.cache\settings-latency.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
function Get-SourceHashes {
    $files=@('ui/dist/index.html','ui/package.json','desktop/Host.ps1','desktop/ProbeWorker.ps1','scripts/SettingsLatency.mjs','scripts/Test-SettingsLatency.ps1','tests/Run-SettingsLatencyHost.ps1','tests/fixtures/SettingsLatencyObserver.cs','tests/Test-SettingsLatencyEvidence.ps1','tests/Test-SettingsLatencyOwnership.ps1','tests/settings-latency.test.mjs','ui/e2e/settings-latency.mjs')
    foreach($directory in @('desktop/native','ui/src','ui/dist/assets')){ $files+=@(Get-ChildItem -LiteralPath (Join-Path $root $directory) -File -Recurse | ForEach-Object FullName) }
    @($files|ForEach-Object { $path=if([IO.Path]::IsPathRooted($_)){ $_ }else{ Join-Path $root $_ }; $file=Get-Item -LiteralPath $path; [pscustomobject]@{path=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} } | Sort-Object path -Unique)
}
try {
    $activeFile=Join-Path $root '.cache\settings-latency-active.json'
    if(Test-Path -LiteralPath $activeFile){
        $previous=Get-Content -LiteralPath $activeFile -Raw | ConvertFrom-Json
        $priorProcess=$null
        try { $priorProcess=[Diagnostics.Process]::GetProcessById($previous.hostPid) } catch [ArgumentException] { }
        if($priorProcess){
            try { if(-not $priorProcess.HasExited -and $priorProcess.StartTime.ToUniversalTime().Ticks -eq $previous.startTicks){ throw ('The previous owned latency host is still running; refusing another run. PID: '+$previous.hostPid) } }
            finally { $priorProcess.Dispose() }
        }
    }
    $lease=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,9232)
    try { $lease.Start() } finally { $lease.Stop() }
    $run='settings-latency-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
    $runDirectory=Join-Path $root ('.cache\'+$run)
    [void][IO.Directory]::CreateDirectory($runDirectory)
    $profile='latency-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
    $ownerNonce=[Guid]::NewGuid().ToString('N')
    $beforeSources=Get-SourceHashes
    $process=$null; $stageExit=1; $forcedStop=$false; $sourceStable=$false
    try {
        $hostArguments=@('-NoProfile','-ExecutionPolicy','Bypass','-STA','-WindowStyle','Hidden','-File',('"'+(Join-Path $root 'tests\Run-SettingsLatencyHost.ps1')+'"'),'-RunDirectory',('"'+$runDirectory+'"'),'-Profile',$profile,'-OwnerNonce',$ownerNonce)
        if($PlaybackLoad){$hostArguments+='-PlaybackLoad'}
        $process=Start-Process -FilePath (Join-Path $root '.deps\pwsh\pwsh.exe') -ArgumentList $hostArguments -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runDirectory 'host.out') -RedirectStandardError (Join-Path $runDirectory 'host.err') -PassThru
        [pscustomobject]@{hostPid=$process.Id;startTicks=$process.StartTime.ToUniversalTime().Ticks;run=$run}|ConvertTo-Json|Set-Content -LiteralPath $activeFile -Encoding utf8
        Write-Output ('Owned latency host PID: '+$process.Id+'; run: '+$run)
        $driverArguments=@((Join-Path $root 'ui\e2e\settings-latency.mjs'),$runDirectory)
        if($PlaybackLoad){$driverArguments+='--playback-load'}
        & node @driverArguments
        $stageExit=$LASTEXITCODE
    } finally {
        if($process -and -not $process.HasExited){
            [IO.File]::WriteAllText((Join-Path $runDirectory 'control.txt'),'close')
            if(-not $process.WaitForExit(10000)){
                $forcedStop=$true
                Write-Warning 'The owned test desktop did not close normally; stopping only the process handle created by this run.'
                $process.Kill()
                if(-not $process.WaitForExit(5000)){ Write-Warning 'Owned host exit is still unconfirmed; subsequent runs will be refused.' }
            }
        }
        foreach($name in @('host.out','host.err')){ $path=Join-Path $runDirectory $name; if(Test-Path -LiteralPath $path){ Get-Content -LiteralPath $path } }
        $afterSources=Get-SourceHashes
        $sourceStable=($beforeSources|ConvertTo-Json -Depth 4 -Compress) -ceq ($afterSources|ConvertTo-Json -Depth 4 -Compress)
        [pscustomobject]@{run=$run;profile=$profile;hostPid=$process.Id;stageExit=$stageExit;forcedStop=$forcedStop;sourceStable=$sourceStable;beforeSources=$beforeSources;afterSources=$afterSources}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $root ('artifacts\'+$run+'-sources.json')) -Encoding utf8
    }
    if($stageExit -ne 0 -or -not $sourceStable){ throw ('Settings latency evidence or source stability failed. Retained run: '+$runDirectory) }
    if(-not $process.HasExited -or $process.ExitCode -ne 0 -or $forcedStop){ throw 'Owned settings-latency desktop did not exit cleanly.' }
    'PASS: timestamped React-to-native settings delivery; see the run-specific evidence and limits.'
} finally { $runLock.Dispose() }
