#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\Api.cs'),(Join-Path $PSScriptRoot 'fixtures\ApiTimeoutTests.cs'))
$null=[Popcorn.ApiTimeoutTests]::Run().GetAwaiter().GetResult()
'PASS: actual API request timeout covers stalled response headers/body; caller cancellation and infinite timeout remain distinct. No network.'
