#requires -Version 7.6
param([ValidateSet('Protocol','Frames','All')][string]$Group='All')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\PlaybackModels.cs'),(Join-Path $root 'desktop\native\ProbeTransport.cs'),(Join-Path $root 'desktop\native\ProbeFrameReader.cs'),(Join-Path $PSScriptRoot 'fixtures\ProbeTransportTests.cs'))
$null=[Popcorn.ProbeTransportTests]::Run($Group).GetAwaiter().GetResult()
'PASS: synthetic probe transport checks; no player APIs, processes or windows were started.'
