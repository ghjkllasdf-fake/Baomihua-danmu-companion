#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\probe cli '+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$compiled=Join-Path $folder 'Fixture.dll'
$assembly=Join-Path $folder 'Fixture [argument].dll'
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','ProbeFrameReader.cs','ProbeJob.cs','ProbeGate.cs','ProbeProcess.cs') | ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
$paths+=Join-Path $PSScriptRoot 'fixtures\ProbeProcessTests.cs'
Add-Type -Path $paths -OutputAssembly $compiled
[IO.File]::WriteAllBytes($assembly,[IO.File]::ReadAllBytes($compiled))
Add-Type -LiteralPath $assembly
$priorSecret=$env:DANMU_TEST_SECRET
$env:DANMU_TEST_SECRET='do-not-inherit-fixture-marker'
try { $null=[Popcorn.ProbeProcessTests]::Run((Join-Path $root '.deps\pwsh\pwsh.exe'),(Join-Path $PSScriptRoot 'fixtures\ProbeCli.ps1'),$assembly).GetAwaiter().GetResult() }
finally { $env:DANMU_TEST_SECRET=$priorSecret }
'PASS: hidden synthetic CLI ownership, gate, transport, stderr and reap; no player APIs.'
