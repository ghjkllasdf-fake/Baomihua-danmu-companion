#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\probe supervisor '+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$assembly=Join-Path $folder 'Fixture.dll'
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','ProbeFrameReader.cs','ProbeJob.cs','ProbeGate.cs','ProbeProcess.cs','ProbeAttempt.cs','ProbeSupervisor.cs') | ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
$paths+=@((Join-Path $PSScriptRoot 'fixtures\ProbeProcessTests.cs'),(Join-Path $PSScriptRoot 'fixtures\ProbeSupervisorTests.cs'))
Add-Type -Path $paths -OutputAssembly $assembly
Add-Type -LiteralPath $assembly
$null=[Popcorn.ProbeSupervisorTests]::Run((Join-Path $root '.deps\pwsh\pwsh.exe'),(Join-Path $PSScriptRoot 'fixtures\ProbeCli.ps1'),$assembly).GetAwaiter().GetResult()
'PASS: synthetic supervisor lifetime and owned-CLI reconnect; no player APIs.'
