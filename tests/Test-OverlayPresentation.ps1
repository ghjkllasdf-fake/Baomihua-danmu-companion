#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\OverlayPresentation.cs'))
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\OverlayPresentationTests.cs.in'))
if(-not $source.Contains('namespace Popcorn;')){throw 'Presentation namespace seam changed.'}
Add-Type -TypeDefinition ($source.Replace('namespace Popcorn;','namespace Popcorn {')+"`n}`n"+$fixture.Replace('using System;',''))
[Popcorn.OverlayPresentationTests]::Run()
'PASS: actual presentation lifecycle prefers DWM, cleans partial initialization before legacy fallback, and releases native/legacy resources once. No HWND was created.'
