#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -LiteralPath @((Join-Path $root 'desktop\native\ProbeOperation.cs'),(Join-Path $root 'desktop\native\PlaybackEventSignal.cs'),(Join-Path $PSScriptRoot 'fixtures\PlaybackEventSignalTests.cs'))
[Popcorn.PlaybackEventSignalTests]::Run()
'PASS: typed playback-event hints, coalescing, replacement, late callbacks, fallback and confirmed detach; no player APIs.'
