#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\ProbeJob.cs'),(Join-Path $PSScriptRoot 'fixtures\ProbeJobTests.cs'))
$null=[Popcorn.ProbeJobTests]::Run((Join-Path $root '.deps\pwsh\pwsh.exe')).GetAwaiter().GetResult()
'PASS: owned CLI job lifecycle, no player/window APIs.'
