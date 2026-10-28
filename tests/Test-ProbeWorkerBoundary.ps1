#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\ProbeWorker.ps1'))
$rootLine='$root=Split-Path $PSScriptRoot -Parent'
$run='[Popcorn.ProbeWorkerRuntime]::Run()'
$input='[Console]::OpenStandardInput()'
foreach($boundary in @($rootLine,$run,$input,'# PROBE_PLAYER_BOUNDARY')){ if(($source.Split($boundary).Count-1) -ne 1){ throw 'Actual worker boundaries changed; refusing to run the fixture.' } }
$folder=Join-Path $root ('.cache\worker-boundary-'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($folder)
$helper=Join-Path $folder 'WorkerFixture.dll'
Add-Type -Path (Join-Path $PSScriptRoot 'fixtures\ProbeWorkerFixture.cs') -OutputAssembly $helper
# Only replace the terminal player entry and the input dependency; compile all actual worker sources without constructing the sampler.
$source=$source.Replace($rootLine,('$root='''+$root.Replace("'","''")+"'`nAdd-Type -LiteralPath '"+$helper.Replace("'","''")+"'"))
$source=$source.Replace($input,'[Popcorn.WorkerGateStream]::new()').Replace($run,'[Popcorn.WorkerFixture]::Emit()')
if($source.Contains($run)){ throw 'The real player entry was not removed; fixture execution refused.' }
$script=Join-Path $folder 'Worker.ps1'; [IO.File]::WriteAllText($script,$source)
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','ProbeFrameReader.cs','ProbeJob.cs','ProbeProcess.cs') | ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
Add-Type -Path @($paths+(Join-Path $PSScriptRoot 'fixtures\ProbeWorkerBoundaryTests.cs'))
$null=[Popcorn.ProbeWorkerBoundaryTests]::Run((Join-Path $root '.deps\pwsh\pwsh.exe'),$script).GetAwaiter().GetResult()
'PASS: worker bootstrap fixture only; real PlayerSampler/ProbeWorkerRuntime.Run was not executed.'
