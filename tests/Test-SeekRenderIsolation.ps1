#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'tests/Import-NativeTypes.ps1')
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$type=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.OverlaySurface',$true)
function New-Surface {
    $item=[Activator]::CreateInstance($type,$true)
    $item.Measure([System.Windows.Size]::new(1280,720))
    $item.Arrange([System.Windows.Rect]::new(0,0,1280,720))
    return $item
}
$surface=New-Surface
try {
    $surface.Load([Popcorn.Comment[]]@(
        [Popcorn.Comment]::new(10000,1,0xffffff,'已进入画面的旧弹幕'),
        [Popcorn.Comment]::new(11500,1,0xffffff,'跳转后的新弹幕')
    ))
    $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0);$surface.Frame()
    $surface.UpdateClock(12000,$false,[Popcorn.Monotonic]::Now,1,$false,0);$surface.Frame()
    $before=$surface.Snapshot($false)
    Assert ($before.ActiveCount -gt 0 -and $before.X[0] -lt 1280) 'The regression must start with an entered, visible old comment.'
    $surface.UpdateClock(11000,$false,[Popcorn.Monotonic]::Now,2,$false,0)
    $justChanged=$surface.Snapshot($false)
    Assert ($justChanged.ActiveCount -eq 0) 'A confirmed seek must synchronously discard old visible comments before the new clock can move them right.'
    Assert (-not $surface.HasPendingFrame -and $justChanged.LayoutPending) 'Seek invalidation must cancel old preparation while keeping the new timeline pending.'
    $surface.Frame()
    Assert ($surface.Snapshot($false).ActiveCount -eq 0) 'The new timeline must not reconstruct the discarded historical comments.'
    $surface.UpdateClock(11500,$false,[Popcorn.Monotonic]::Now,2,$false,0);$surface.Frame()
    $new=$surface.Snapshot($false)
    Assert ($new.ActiveCount -eq 1 -and $new.Text[0] -ceq '跳转后的新弹幕' -and [Math]::Abs($new.X[0]-1280) -lt .01) 'Only the newly due comment may enter from the right after a seek.'
    $surface.UpdateClock(11700,$false,[Popcorn.Monotonic]::Now,2,$false,0);$surface.Frame()
    Assert ($surface.Snapshot($false).X[0] -lt $new.X[0]) 'Normal leftward movement must resume after the seek without replaying the old scene.'
    'PASS: backward seek clears the old scene immediately and resumes with new right-to-left entries.'
} finally {$surface.Detach()}
$surface=New-Surface
try {
    $comments=[Popcorn.Comment[]]::new(20)
    for($i=0;$i -lt 20;$i++){$comments[$i]=[Popcorn.Comment]::new(10000,1,0xffffff,('拖动中间位置-'+$i))}
    $surface.Load($comments);$surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
    $surface.StepFrame()
    Assert $surface.HasPendingFrame 'The rapid-seek case must interrupt actual unfinished geometry.'
    foreach($target in @(12000,10500,14000,11000,15000)){
        $revision=1+([Array]::IndexOf(@(12000,10500,14000,11000,15000),$target))+1
        $surface.UpdateClock($target,$false,[Popcorn.Monotonic]::Now,$revision,$false,0)
        Assert (-not $surface.HasPendingFrame -and $surface.Snapshot($false).ActiveCount -eq 0) 'Each intermediate drag seek must discard old and partially prepared layouts immediately.'
        $surface.StepFrame()
    }
    do {$surface.StepFrame()}while($surface.HasPendingFrame)
    $final=$surface.Snapshot($false)
    Assert ($final.PositionMs -eq 15000 -and $final.ActiveCount -eq 0) 'A burst of seeks must settle on the final timeline without restoring intermediate comments.'
    Assert ($final.SpriteBytes -le [Popcorn.SpriteLimits]::MaxResidentBytes) 'Seek cancellation must retain the original sprite memory budget.'
    'PASS: rapid forward/backward drag seeks cancel intermediate work and cannot republish old comments.'
} finally {$surface.Detach()}
