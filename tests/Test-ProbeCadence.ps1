#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\ProbeWorkerRuntime.cs'))
$seams=@{'new PlayerSampler(CancellationToken.None)'='new CadenceSampler()';'Console.OpenStandardOutput()'='CadenceFixture.Output()'}
foreach($boundary in $seams.Keys){if(($source.Split($boundary).Count-1) -ne 1){throw 'Worker cadence dependencies changed; refusing real sampler execution.'};$source=$source.Replace($boundary,$seams[$boundary])}
if(($source.Split('sampler.WaitForNext(').Count-1) -ne 1){throw 'Worker must delegate its bounded fallback wait to the event-aware sampler.'}
$source=$source.Replace('Monotonic.Now','CadenceFixture.Now')
$folder=Join-Path $root ('.cache\probe-cadence-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($folder)
$copy=Join-Path $folder 'Worker.cs';[IO.File]::WriteAllText($copy,$source)
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','PlayerControlCache.cs','ProbeCadence.cs')|ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
Add-Type -Path @($paths+$copy+(Join-Path $PSScriptRoot 'fixtures\ProbeCadenceTests.cs'))
[Popcorn.CadenceFixture]::Run()
'PASS: actual worker scheduling with synthetic sampler/event-wait, output and clock; no player APIs.'
