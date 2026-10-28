#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Windows.cs'))
$start=$source.IndexOf('internal static IntPtr CreateDwmOverlay()')
$end=$source.IndexOf('internal static void DestroyOverlay(',$start)
if($start -lt 0 -or $end -le $start){throw 'Native overlay initialization seam changed.'}
$method=$source.Substring($start,$end-$start)
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\DwmOverlayInitializationTests.cs.in'))
Add-Type -TypeDefinition $fixture.Replace('/* INITIALIZATION_METHOD */',$method)
[DwmOverlayInitializationTests]::Run()
'PASS: actual DWM initialization requires composition, glass, constant alpha and all input styles; every partial native failure destroys the parent. No HWND was created.'
