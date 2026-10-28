#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Overlay.cs'))
$owner=$source.IndexOf('internal sealed class OverlayWindow')
$start=$source.IndexOf('private void Rendering(object sender,EventArgs args)',$owner)
$end=$source.IndexOf('public OverlaySnapshot Snapshot()',$start)
if($start -lt 0 -or $end -le $start){throw 'Actual renderer dispatch seam changed.'}
$methods=$source.Substring($start,$end-$start)
if(-not $methods.Contains('private void QueueFrameWork()') -or -not $methods.Contains('private void CancelFrameWork()')){throw 'The renderer needs one bounded post-render work queue with explicit cancellation.'}
if(-not $source.Contains('runFrameWork=RunFrameWork;') -or -not $source.Contains('public void Close() { CancelFrameWork();')){throw 'Actual renderer construction/close must bind and cancel its work callback.'}
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\RenderWorkQueueTests.cs.in'))
Add-Type -TypeDefinition $fixture.Replace('/* ACTUAL_METHODS */',$methods)
[RenderWorkQueueTests]::Run()
'PASS: actual renderer dispatch records callbacks, coalesces composition targets/work, skips no-due work, reads current state, aborts on close and clears failed posts. No HWND was created.'
