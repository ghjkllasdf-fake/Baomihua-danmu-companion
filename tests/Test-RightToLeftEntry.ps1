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
        [Popcorn.Comment]::new(10000,1,0xffffff,'普通滚动'),
        [Popcorn.Comment]::new(10000,4,0xffffff,'底部来源'),
        [Popcorn.Comment]::new(10000,5,0xffffff,'顶部来源'),
        [Popcorn.Comment]::new(10000,6,0xffffff,'逆向来源')
    ))
    $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0);$surface.Frame()
    $start=$surface.Snapshot($false)
    Assert ($start.ActiveCount -eq 4) 'All supported source modes must be admitted by the same rolling renderer.'
    foreach($x in $start.X){Assert ([Math]::Abs($x-1280) -lt .01) 'Every newly admitted comment must start at the right boundary, including fixed and reverse source modes.'}
    $surface.UpdateClock(10500,$false,[Popcorn.Monotonic]::Now,1,$false,0);$surface.Frame()
    $next=$surface.Snapshot($false)
    for($i=0;$i -lt 4;$i++){
        Assert ($next.X[$i] -lt $start.X[$i] -and $next.Y[$i] -eq $start.Y[$i]) 'All comments must move left on a stable horizontal lane; none may stay fixed or move vertically/reverse.'
    }
    'PASS: modes 1/4/5/6 enter at the right edge and move left on stable lanes.'
} finally {$surface.Detach()}
$surface=New-Surface
try {
    $surface.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(8000,1,0xffffff,'旧弹幕不补刷')))
    $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0);$surface.Frame()
    Assert ($surface.Snapshot($false).ActiveCount -eq 0) 'Initial loading and seek must not replay several seconds of historical comments into the viewport.'
    $surface.UpdateClock(8000,$false,[Popcorn.Monotonic]::Now,2,$false,0);$surface.Frame()
    $atSeek=$surface.Snapshot($false)
    Assert ($atSeek.ActiveCount -eq 1 -and [Math]::Abs($atSeek.X[0]-1280) -lt .01) 'After a backward seek a newly due comment must enter from the right, not appear partway across.'
    'PASS: source load/seek do not backfill old comments; newly due comments still appear.'
} finally {$surface.Detach()}
$surface=New-Surface
try {
    $comments=[Popcorn.Comment[]]::new(8)
    for($i=0;$i -lt 8;$i++){$comments[$i]=[Popcorn.Comment]::new(10000,1,0xffffff,('原子入场-'+$i))}
    $surface.Load($comments);$surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
    for($step=0;$step -lt 100;$step++){
        $surface.StepFrame();$snapshot=$surface.Snapshot($false)
        if(-not $snapshot.LayoutPending){break}
        Assert ($snapshot.ActiveCount -eq 0) 'An initial incremental layout must not publish incomplete rows from top to bottom.'
    }
    Assert ($step -lt 100 -and $snapshot.ActiveCount -eq 8) 'The complete staged layout must be published within bounded work.'
    'PASS: initial incremental construction publishes the complete layout without vertical row-by-row flashing.'
} finally {$surface.Detach()}
