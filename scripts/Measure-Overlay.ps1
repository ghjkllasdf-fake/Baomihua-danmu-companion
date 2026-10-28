#requires -Version 7.6
param([int]$Seconds=60,[int]$Width=1920,[int]$Height=1080,[int]$Comments=50000,[int]$MaxVisible=100,[switch]$SharedTextDiagnostic)
$ErrorActionPreference='Stop'
if($Seconds -lt 3 -or $Seconds -gt 180){ throw 'Duration must be 3-180 seconds.' }
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop\Host.ps1') -Check
[System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2) | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()
$form=[System.Windows.Forms.Form]::new()
$form.Text='Popcorn renderer benchmark - synthetic video target'
$form.FormBorderStyle=[System.Windows.Forms.FormBorderStyle]::None
$form.ClientSize=[System.Drawing.Size]::new($Width,$Height)
$form.BackColor=[System.Drawing.Color]::FromArgb(35,47,40)
$form.StartPosition='CenterScreen'
$overlay=[Popcorn.OverlayHost]::new($true)
$settings=[Popcorn.DanmakuSettings]::new(); $settings.AreaFraction=1; $settings.FontSize=24; $settings.Density=$MaxVisible; $settings.Dedupe=$false
$overlay.SetSettings($settings)
$data=[Popcorn.Comment[]]::new($Comments)
for($i=0;$i -lt $Comments;$i++){ $data[$i]=[Popcorn.Comment]::new($i*50,1,16777215,$(if($SharedTextDiagnostic){'弹幕测试 · 0000'}else{'弹幕测试 · '+$i})) }
$overlay.SetComments($data)
$watch=[Diagnostics.Stopwatch]::new()
$script:targetWasForeground=$false
$script:lastTarget=$null
$timer=[System.Windows.Forms.Timer]::new(); $timer.Interval=100
$timer.add_Tick({
    $target=[Popcorn.NativeWindows]::Read($form.Handle)
    $script:lastTarget=$target
    if([Popcorn.NativeWindows]::GetForegroundWindow() -eq $form.Handle){ $script:targetWasForeground=$true }
    $overlay.Update($target,$watch.Elapsed.TotalMilliseconds,$true,1,$false,0)
    if($watch.Elapsed.TotalSeconds -ge $Seconds){ $timer.Stop(); $form.Close() }
})
$form.add_Shown({ $watch.Start(); $timer.Start() })
try {
    [System.Windows.Forms.Application]::Run($form)
    $rawIntervals=@($overlay.FrameIntervals())
    $intervals=@($rawIntervals | Select-Object -Skip 120 | Sort-Object)
    if($intervals.Count -lt 20){ throw ('Not enough rendered frames. Raw frame count: '+$rawIntervals.Count+'; target was foreground: '+$script:targetWasForeground) }
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'artifacts'))
    $overlay.Capture((Join-Path $root 'artifacts\overlay-frame.png'))
    $p95=$intervals[[Math]::Min($intervals.Count-1,[int][Math]::Floor($intervals.Count*.95))]
    $p99=$intervals[[Math]::Min($intervals.Count-1,[int][Math]::Floor($intervals.Count*.99))]
    $surface=$overlay.Snapshot()
    $result=[pscustomobject]@{Seconds=$Seconds;Comments=$Comments;CompositionMode=$overlay.CompositionMode;RequestedWidthPx=$Width;RequestedHeightPx=$Height;TargetWidthPx=$script:lastTarget.Width;TargetHeightPx=$script:lastTarget.Height;SurfaceWidthPx=$surface.ViewportWidthPx;SurfaceHeightPx=$surface.ViewportHeightPx;FramesMeasured=$intervals.Count;MaxActive=$overlay.MaxActive();FrameIntervalP95Ms=[Math]::Round($p95,2);FrameIntervalP99Ms=[Math]::Round($p99,2);WpfRenderingTier=([System.Windows.Media.RenderCapability]::Tier -shr 16);SharedTextDiagnostic=[bool]$SharedTextDiagnostic;Note='Synthetic renderer benchmark bypasses foreground gating; shared-text mode is diagnostic only. Not proof of actual presentation, foreground policy or BaoMiHua synchronization.'}
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts\overlay-performance.json')
    $result | Format-List
    if($script:lastTarget.Width -ne $Width -or $script:lastTarget.Height -ne $Height -or [Math]::Abs($surface.ViewportWidthPx-$Width) -gt 2 -or [Math]::Abs($surface.ViewportHeightPx-$Height) -gt 2){ throw 'Measured physical target/surface dimensions do not match the requested benchmark size.' }
    if($p95 -gt 20 -or $p99 -gt 33.4){ throw 'Native frame interval budget exceeded.' }
    if($result.MaxActive -lt $MaxVisible){ throw 'The benchmark did not reach the requested active comment load.' }
}finally{ $timer.Dispose(); $overlay.Dispose(); $form.Dispose() }
