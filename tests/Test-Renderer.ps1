#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop\Host.ps1') -Check
[System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2) | Out-Null
$form=[System.Windows.Forms.Form]::new()
$form.Text='Popcorn renderer regression - synthetic target'
$form.ClientSize=[System.Drawing.Size]::new(1280,720)
$renderer=[Popcorn.OverlayHost]::new($true)
function Assert($condition,[string]$message){ if(-not $condition){ throw $message } }
function Pump([int]$ms=120){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.ElapsedMilliseconds -lt $ms){ [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 10 }
}
function Update([double]$position=10000,[int]$revision=1){
    $renderer.Update([Popcorn.NativeWindows]::Read($form.Handle),$position,$false,$revision,$false,0)
    Pump
}
function Settings { [Popcorn.DanmakuSettings]::new() }
try{
    $form.Show(); Pump
    $comments=[Popcorn.Comment[]]@(
        [Popcorn.Comment]::new(7000,1,16777215,'rolling'),
        [Popcorn.Comment]::new(7000,5,16777215,'top'),
        [Popcorn.Comment]::new(7000,4,16777215,'bottom'),
        [Popcorn.Comment]::new(7000,1,16777215,'blocked'),
        [Popcorn.Comment]::new(7000,1,16777215,'rolling'))
    $renderer.SetComments($comments); Update
    $s=$renderer.Snapshot()
    Assert ($s.Visible -and $s.ActiveCount -eq 4) 'Real WPF surface must render four deduplicated comments.'
    Assert ($s.ClickThrough -and $s.NoActivate) 'The actual layered window must have transparent/no-activate native styles.'
    $before=$s.X -join ','; Pump 300
    Assert (($renderer.Snapshot().X -join ',') -eq $before) 'Paused sprites must remain at identical coordinates.'
    $settings=Settings; $settings.Keywords='blocked'; $renderer.SetSettings($settings); Pump
    Assert (-not ($renderer.Snapshot().Text -contains 'blocked')) 'Keyword filtering must reach the native renderer.'
    foreach($region in @(.25,.5,.75,1.0)){
        $settings=Settings; $settings.AreaFraction=$region; $renderer.SetSettings($settings); Pump
        $s=$renderer.Snapshot()
        Assert ([Math]::Abs($s.AreaHeight-$s.ViewportHeightPx*$region) -lt 1) 'Every region must cover its exact upper-screen fraction.'
        Assert ($s.Text -contains 'bottom') 'Fixed bottom comment must still fit inside the selected upper region.'
    }
    $settings=Settings; $settings.Rolling=$false; $settings.Bottom=$false; $renderer.SetSettings($settings); Pump
    Assert (($renderer.Snapshot().Text -join ',') -eq 'top') 'Native type switches must exclude rolling and bottom comments.'
    $renderer.SetComments([Popcorn.Comment[]]@([Popcorn.Comment]::new(9500,1,16777215,'offset')))
    $settings=Settings; $settings.OffsetMs=1000; $renderer.SetSettings($settings); Update 10000 2
    Assert ($renderer.Snapshot().ActiveCount -eq 0) 'Positive offset must delay comments.'
    $settings=Settings; $settings.OffsetMs=-1000; $renderer.SetSettings($settings); Pump
    Assert ($renderer.Snapshot().ActiveCount -eq 1) 'Negative offset must advance comments.'
    $renderer.SetComments([Popcorn.Comment[]]@([Popcorn.Comment]::new(4000,1,16777215,'speed')))
    $settings=Settings; $renderer.SetSettings($settings); Update 10000 3
    $before=$renderer.Snapshot()
    $settings=Settings; $settings.SpeedMultiplier=2; $renderer.SetSettings($settings); Pump
    $after=$renderer.Snapshot()
    Assert ([Math]::Abs($before.X[0]-$after.X[0]) -lt .01) 'Changing speed must preserve the visible sprite position.'
    $form.ClientSize=[System.Drawing.Size]::new(960,540); Update 10000 3
    $resized=$renderer.Snapshot()
    Assert ($resized.FontPx -lt $after.FontPx) 'Font must shrink proportionally with a smaller player.'
    Assert ($resized.ActiveCount -eq 1) 'A retimed comment older than the new duration must survive resize until its actual motion ends.'
    Assert ([Math]::Abs($resized.Progress[0]-$after.Progress[0]) -lt .001) 'Resize must preserve retimed progress instead of jumping to the new-speed timeline.'
    $settings=Settings; $settings.SpeedMultiplier=2; $settings.Enabled=$false; $renderer.SetSettings($settings); Update 10000 3
    Assert (-not $renderer.Snapshot().Visible) 'Disabled overlay must hide without activating a window.'
    $settings=Settings; $settings.SpeedMultiplier=2; $renderer.SetSettings($settings); Update 10000 3
    Assert ($renderer.Snapshot().ActiveCount -eq 1) 'Restoring a hidden overlay must preserve retimed active motion.'
    $settings=Settings; $settings.ScaleWithWindow=$false; $renderer.SetSettings($settings); Pump
    $fixed=$renderer.Snapshot().FontPx
    $form.ClientSize=[System.Drawing.Size]::new(1280,720); Update 10000 3
    Assert ([Math]::Abs($renderer.Snapshot().FontPx-$fixed) -lt .01) 'Fixed-font mode must not scale with window size.'
    $title=$form.Text; $form.Text='Synthetic new media title'; Pump 200
    Assert (-not $renderer.Snapshot().Visible) 'An observed title change must hide the old overlay before the next player probe arrives.'
    $form.Text=$title; Update 10000 3
    $expired=[Popcorn.Comment[]]::new(1000)
    for($i=0;$i -lt $expired.Length;$i++){ $expired[$i]=[Popcorn.Comment]::new(3000,5,16777215,('expired-'+$i)) }
    $cached=$renderer.Snapshot().CacheEntries
    $renderer.SetComments($expired); Update 10000 4
    Assert ($renderer.Snapshot().ActiveCount -eq 0) 'Expired fixed comments must not render.'
    Assert ($renderer.Snapshot().CacheEntries -eq $cached) 'Expired comments must be rejected before allocating any sprite bitmap.'
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'artifacts'))
    $renderer.Capture((Join-Path $root 'artifacts\renderer-regression.png'))
    'PASS: native WPF dedupe, keywords, regions, types, pause, offset, speed and proportional/fixed resize'
}finally{ $renderer.Dispose(); $form.Close(); $form.Dispose() }
