#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\ProbeOperation.cs'),(Join-Path $PSScriptRoot 'fixtures\ProbeOperationTests.cs'))
[Popcorn.ProbeOperationTests]::Run()
'PASS: synthetic operation completion classification; no WinRT/SMTC activation.'
