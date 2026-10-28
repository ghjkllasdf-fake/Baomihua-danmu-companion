#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\PlaybackModels.cs'),(Join-Path $root 'desktop\native\PlayerControlCache.cs'),(Join-Path $PSScriptRoot 'fixtures\PlayerControlCacheTests.cs'))
[Popcorn.PlayerControlCacheTests]::Run()
'PASS: control-reference cache with synthetic elements only; no UIA, SMTC, processes or windows.'
