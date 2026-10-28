#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\ProbeProcess.cs'))
$stop='if (Interlocked.Exchange(ref stopped,1)!=0) return;'
$start='if (!process.Start())'
$reap='if (Volatile.Read(ref launchState)!=1'
if(-not $source.Contains($stop) -or -not $source.Contains($start) -or -not $source.Contains($reap)){ throw 'Actual process lifecycle boundaries changed; update the race harness.' }
# Scheduling barriers only: the production lifecycle conditions and resource calls remain unchanged.
$source=$source.Replace($stop,$stop+' RaceSchedule.AtStop();').Replace($start,'RaceSchedule.AtStart(); '+$start).Replace($reap,'RaceSchedule.AtReapCheck(); '+$reap).Replace('process.HasExited','RaceSchedule.HasExited(process)')
$folder=Join-Path $root '.cache\tests'; [void][IO.Directory]::CreateDirectory($folder)
$path=Join-Path $folder 'ProbeProcess.races.cs'; [IO.File]::WriteAllText($path,$source)
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','ProbeFrameReader.cs','ProbeJob.cs','ProbeGate.cs') | ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
Add-Type -Path @($paths+$path+(Join-Path $PSScriptRoot 'fixtures\ProbeProcessRaceTests.cs'))
$null=[Popcorn.ProbeProcessRaceTests]::Run((Join-Path $root '.deps\pwsh\pwsh.exe'),(Join-Path $PSScriptRoot 'fixtures\ProbeCli.ps1')).GetAwaiter().GetResult()
'PASS: scheduled start/stop/reap interleavings, no player/window APIs.'
