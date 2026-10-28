#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Overlay.cs'))
$owner=$source.IndexOf('internal sealed class OverlayWindow')
$start=$source.IndexOf('public OverlaySnapshot Snapshot()',$owner)
$end=$source.IndexOf('public void Follow()',$start)
if($owner -lt 0 -or $start -lt 0 -or $end -le $start){ throw 'Overlay snapshot seam changed; refusing a substitute implementation.' }
$method=$source.Substring($start,$end-$start).Trim()
$template=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\OverlayWindowSnapshotTests.cs.in'))
if(($template.Split('/* SNAPSHOT_METHOD */').Count-1) -ne 1){ throw 'Invalid overlay snapshot fixture.' }
Add-Type -TypeDefinition $template.Replace('/* SNAPSHOT_METHOD */',$method)
[OverlayWindowSnapshotTests]::Run()
'PASS: actual overlay-window snapshot reads its own physical origin; null is not a fabricated zero. No HWND was created.'
