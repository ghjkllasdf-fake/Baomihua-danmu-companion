#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @(
    (Join-Path $root 'desktop/native/Core.cs'),
    (Join-Path $root 'desktop/native/Api.cs'),
    (Join-Path $root 'desktop/native/AutomaticMatch.cs'),
    (Join-Path $PSScriptRoot 'fixtures/AutomaticMatchTests.cs')
)
[Popcorn.AutomaticMatchTests]::Run().GetAwaiter().GetResult()
