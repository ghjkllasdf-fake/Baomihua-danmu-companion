#requires -Version 7.6
param([ValidateSet('Text','Lanes','Limits','Budget','Opacity','Cadence','Surface','EmptyLoad','Incremental','All')][string]$Group='All')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop\Host.ps1') -Check
function Assert($condition,[string]$message){ if(-not $condition){ throw $message } }
function Parse-Text([string]$text){
    $json=@{ comments=@(@{ p='1,1,16777215'; m=$text }) } | ConvertTo-Json -Depth 4 -Compress
    [Popcorn.ApiClient]::ParseComments([System.Text.Json.Nodes.JsonNode]::Parse($json))
}
if($Group -in @('Text','All')){
    $comments=@(Parse-Text "line`r`nsecond`tpart$([char]0x2028)tail$([char]0x0085)end$([char]0x7f)")
    Assert ($comments.Count -eq 1 -and $comments[0].Text -ceq 'line second part tail end') 'Display text must flatten all control whitespace and remove other controls before layout.'
    $emoji=[char]::ConvertFromUtf32(0x1f37f)
    $comments=@(Parse-Text ($emoji*121))
    Assert ($comments[0].Text -ceq ($emoji*120)) 'The 120-scalar limit must not split supplementary Unicode characters.'
    Assert (@(Parse-Text "`n`t$([char]0x7f)").Count -eq 0) 'Control-only comments must not reach the renderer.'
    'PASS: API display text is single-line, scalar-safe and non-empty.'
}
if($Group -in @('Lanes','All')){
    # The surface is not attached to an HWND; no app Window, player probe or screen capture is created.
    $type=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.OverlaySurface',$true)
    $surface=[Activator]::CreateInstance($type,$true)
    try{
        $surface.Measure([System.Windows.Size]::new(1280,720))
        $surface.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        Assert ($null -eq [System.Windows.PresentationSource]::FromVisual($surface)) 'The resource fixture must remain detached from a presentation window.'
        $comments=[Popcorn.Comment[]]::new(50)
        for($i=0;$i -lt $comments.Length;$i++){ $comments[$i]=[Popcorn.Comment]::new(10000,5,(0x808080+$i),('lane-'+$i)) }
        $surface.Load($comments)
        $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
        $surface.Frame()
        $snapshot=$surface.Snapshot($false)
        Assert ($snapshot.ActiveCount -gt 0 -and $snapshot.ActiveCount -lt $comments.Length) 'The fixture must exhaust the available rolling lanes, including formerly fixed source comments.'
        Assert ($snapshot.CacheEntries -eq $snapshot.ActiveCount) 'Comments without a lane must not allocate or enter the sprite cache.'
        'PASS: actual offscreen surface rejects no-lane comments before bitmap allocation.'
    }finally{ $surface.Detach() }
}
if($Group -in @('Limits','All')){
    $size=[Popcorn.RasterSize]::new(0,0,0)
    Assert ([Popcorn.SpriteLimits]::TryMeasure(100.2,20.1,1.5,[ref]$size)) 'Ordinary fractional-DPI sprites must be accepted.'
    Assert ($size.Width -eq 151 -and $size.Height -eq 31 -and $size.Bytes -eq (151*31*4)) 'Pixel dimensions must round up and use four bytes per Pbgra32 pixel.'
    Assert ([Popcorn.SpriteLimits]::TryMeasure(8192,512,1,[ref]$size) -and $size.Bytes -eq [Popcorn.SpriteLimits]::MaxSpriteBytes) 'The exact per-sprite budget boundary must remain valid.'
    Assert (-not [Popcorn.SpriteLimits]::TryMeasure(8192,513,1,[ref]$size)) 'One pixel row beyond the byte boundary must be rejected.'
    foreach($dimensions in @(@(46100,416,1),@(23060,29188,1),@(181652,416,1),@(8192,8192,1),@([double]::MaxValue,2,2),@([double]::NaN,20,1),@(20,20,[double]::PositiveInfinity),@(-1,20,1),@(0,20,1))){
        Assert (-not [Popcorn.SpriteLimits]::TryMeasure($dimensions[0],$dimensions[1],$dimensions[2],[ref]$size)) 'Invalid or oversized sprite dimensions must be rejected before allocating any pixels.'
        Assert ($size.Bytes -eq 0) 'A rejected raster must not retain an earlier valid byte count.'
    }
    'PASS: finite pixel dimensions and checked byte limits reject all oversized fixtures without allocation.'
}
if($Group -in @('Budget','All')){
    $storeType=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.SpriteStore',$true)
    $store=[Activator]::CreateInstance($storeType,[object[]]@([long]2048))
    $raster=[Popcorn.RasterSize]::new(16,16,1024)
    $script:pixelCalls=0
    $factory=[Func[System.Windows.Media.Imaging.BitmapSource]]{
        $script:pixelCalls++
        [System.Windows.Media.Imaging.BitmapSource]::Create(16,16,96,96,[System.Windows.Media.PixelFormats]::Pbgra32,$null,[byte[]]::new(1024),64)
    }
    $a=$store.Create('a',16,16,$raster,$factory); $store.Pin($a)
    $b=$store.Create('b',16,16,$raster,$factory); $store.Pin($b)
    Assert ($store.ResidentBytes -eq 2048 -and $store.Count -eq 2) 'Pinned and cached sprites must share one accounted byte budget.'
    $c=$store.Create('c',16,16,$raster,$factory)
    Assert ($null -eq $c -and $store.Created -eq 2 -and $script:pixelCalls -eq 2) 'A full pinned budget must reject a new sprite before its pixel factory runs.'
    $store.Release($a)
    $c=$store.Create('c',16,16,$raster,$factory); $store.Pin($c)
    Assert ($null -eq $store.Find('a') -and [object]::ReferenceEquals($store.Find('b'),$b)) 'LRU eviction must remove only unpinned entries.'
    $store.ClearUnused()
    Assert ($store.ResidentBytes -eq 2048) 'Clearing the cache must continue counting active sprite references.'
    $store.Pin($b); $store.Release($b); $store.ClearUnused()
    Assert ($store.ResidentBytes -eq 2048) 'Shared active sprites must stay pinned until their last user releases them.'
    $store.Release($b); $store.ClearUnused()
    Assert ($store.ResidentBytes -eq 1024) 'An unpinned sprite must release its accounted bytes when evicted.'
    $store.Release($c); $store.ClearUnused()
    Assert ($store.ResidentBytes -eq 0 -and $store.Count -eq 0) 'Detaching all users and clearing must leave no accounted sprite pixels.'
    $failed=$false
    try { $null=$store.Create('failed',16,16,$raster,[Func[System.Windows.Media.Imaging.BitmapSource]]{ throw 'fixture pixel factory failure' }) }catch{ $failed=$true }
    Assert ($failed -and $store.ResidentBytes -eq 0 -and $store.Count -eq 0) 'A failed bitmap factory must not leak a reservation or cache entry.'
    $store=[Activator]::CreateInstance($storeType,[object[]]@([long]8192))
    $tiny=[System.Windows.Media.Imaging.BitmapSource]::Create(1,1,96,96,[System.Windows.Media.PixelFormats]::Pbgra32,$null,[byte[]]::new(4),4)
    $tinyFactory=[Func[System.Windows.Media.Imaging.BitmapSource]]{ $tiny }
    $tinyRaster=[Popcorn.RasterSize]::new(1,1,4)
    for($i=0;$i -lt 513;$i++){ $null=$store.Create(('tiny-'+$i),1,1,$tinyRaster,$tinyFactory) }
    Assert ($store.Count -eq 512 -and $null -eq $store.Find('tiny-0')) 'The entry-count backstop must not allow a 513th cached sprite.'
    $store.ClearUnused()
    'PASS: byte-budget admission, pinned references, LRU eviction, factory failure and entry-count bounds.'
}
if($Group -in @('Opacity','All')){
    $assembly=[Popcorn.OverlayHost].Assembly
    $spriteType=$assembly.GetType('Popcorn.Sprite',$true)
    Assert ($null -ne $spriteType.GetMethod('AlphaBrush')) 'Sprites must expose opacity through a cached image brush without allocating new pixels.'
    $store=[Activator]::CreateInstance($assembly.GetType('Popcorn.SpriteStore',$true),[object[]]@([long]16))
    $pixels=[byte[]](255,255,255,255,255,255,255,255,255,255,255,255,255,255,255,255)
    $image=[System.Windows.Media.Imaging.BitmapSource]::Create(2,2,96,96,[System.Windows.Media.PixelFormats]::Pbgra32,$null,$pixels,8)
    $sprite=$store.Create('alpha',2,2,[Popcorn.RasterSize]::new(2,2,16),[Func[System.Windows.Media.Imaging.BitmapSource]]{$image})
    $quarter=$sprite.AlphaBrush(.25)
    Assert ($quarter.IsFrozen -and [object]::ReferenceEquals($quarter.ImageSource,$sprite.Image)) 'Opacity brushes must freeze and share the existing sprite image.'
    Assert ([object]::ReferenceEquals($quarter,$sprite.AlphaBrush(.25))) 'Unchanged opacity must reuse its brush.'
    $threeQuarters=$sprite.AlphaBrush(.75)
    Assert (-not [object]::ReferenceEquals($quarter,$threeQuarters) -and $threeQuarters.IsFrozen -and [object]::ReferenceEquals($threeQuarters.ImageSource,$sprite.Image) -and $quarter.Opacity -eq .25) 'Changing opacity must preserve the previous frozen brush and reuse the image.'
    Assert ($store.Count -eq 1 -and $store.Created -eq 1 -and $store.ResidentBytes -eq 16) 'Opacity changes must not allocate or charge another raster.'
    Add-Type -TypeDefinition @'
using System;
using System.Windows.Media.Imaging;
public static class SpritePixelProof {
    public static int MaxAlpha(BitmapSource image) {
        int stride=image.PixelWidth*4,maximum=0;
        var pixels=new byte[stride*image.PixelHeight];image.CopyPixels(pixels,stride,0);
        for(int i=3;i<pixels.Length;i+=4) maximum=Math.Max(maximum,pixels[i]);
        return maximum;
    }
}
'@ -ReferencedAssemblies ($references|Select-Object -Unique)
    $surface=[Activator]::CreateInstance($assembly.GetType('Popcorn.OverlaySurface',$true),$true)
    try {
        $surface.Measure([System.Windows.Size]::new(1280,720));$surface.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $surface.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,0xffffff,'透明描边检查')))
        $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
        $surface.Frame()
        $surface.UpdateClock(12000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
        $created=$null
        foreach($opacity in @(.25,.75)){
            $settings=[Popcorn.DanmakuSettings]::new();$settings.Opacity=$opacity;$surface.Configure($settings);$surface.Frame()
            $visual=[System.Windows.Media.DrawingVisual]::new()
            $drawing=$visual.RenderOpen()
            try {$surface.GetType().GetMethod('OnRender',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($surface,[object[]]@($drawing))|Out-Null}
            finally {$drawing.Close()}
            $frame=[System.Windows.Media.Imaging.RenderTargetBitmap]::new(1280,720,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
            $frame.Render($visual)
            $alpha=[SpritePixelProof]::MaxAlpha($frame)
            Assert ([Math]::Abs($alpha-255*$opacity) -le 1) 'Actual surface text and outline must render at the requested opacity.'
            if($null -eq $created){$created=$surface.Snapshot($false).SpritesCreated}
            else {Assert ($surface.Snapshot($false).SpritesCreated -eq $created) 'A live opacity change must keep its cached sprite pixels.'}
        }
    } finally {$surface.Detach()}
    $store.ClearUnused()
    Assert ($store.ResidentBytes -eq 0) 'Releasing opacity brushes must not retain an accounted sprite entry.'
    'PASS: cached frozen opacity brushes share pixel storage; actual production drawing preserves text/outline alpha and live opacity changes reuse raster pixels.'
}
if($Group -in @('Surface','All')){
    $type=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.OverlaySurface',$true)
    # Detached visuals inherit machine DPI; measure one real glyph before sizing the tiny fixture.
    $measurement=[Activator]::CreateInstance($type,$true)
    try {
        $measurement.Measure([System.Windows.Size]::new(1280,720)); $measurement.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $measurement.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,0x808080,'i')))
        $measurement.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0); $measurement.Frame()
        $single=$measurement.Snapshot($false)
        Assert ($single.ActiveCount -eq 1 -and $single.SpritesCreated -eq 1 -and $single.SpriteBytes -gt 0) 'The pixel-budget fixture must measure exactly one real glyph.'
        $surfaceBudget=[long]($single.SpriteBytes*2)
    } finally { $measurement.Detach() }
    $surface=[Activator]::CreateInstance($type,[object[]]@($surfaceBudget))
    try{
        $surface.Measure([System.Windows.Size]::new(1280,720)); $surface.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $settings=[Popcorn.DanmakuSettings]::new(); $settings.Dedupe=$false; $surface.Configure($settings)
        $comments=[Popcorn.Comment[]]::new(20)
        for($i=0;$i -lt $comments.Length;$i++){ $comments[$i]=[Popcorn.Comment]::new(10000,5,(0x808080+$i),'i') }
        $surface.Load($comments); $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0); $surface.Frame()
        $snapshot=$surface.Snapshot($false)
        Assert ($snapshot.ActiveCount -eq 2 -and $snapshot.SpriteBytes -eq $surfaceBudget -and $snapshot.RejectedSprites -gt 0) 'The actual surface must pin exactly two measured glyphs and reject further pixels before allocation.'
        $created=$snapshot.SpritesCreated
        for($i=0;$i -lt 20;$i++){ $surface.Frame() }
        Assert ($surface.Snapshot($false).SpritesCreated -eq $created) 'Repeated paused frames must reuse images, not recreate pixels.'
        for($i=0;$i -lt 8;$i++){
            $settings=[Popcorn.DanmakuSettings]::new(); $settings.Dedupe=$false; $settings.FontSize=28+($i%2)*12; $surface.Configure($settings); $surface.Frame()
            Assert ($surface.Snapshot($false).SpriteBytes -le $surfaceBudget) 'Layout rebuilds must release old active references before admitting replacement sprites.'
        }
        $settings=[Popcorn.DanmakuSettings]::new(); $settings.Dedupe=$false; $surface.Configure($settings); $surface.Frame()
        Assert ($surface.Snapshot($false).ActiveCount -gt 0) 'The natural-expiry fixture must contain pinned active sprites before time advances.'
        $surface.UpdateClock(19000,$false,[Popcorn.Monotonic]::Now,1,$false,0); $surface.Frame()
        Assert ($surface.Snapshot($false).ActiveCount -eq 0) 'Natural expiry without a seek/rebuild must remove sprites after the unified eight-second rolling duration.'
    }finally{ $surface.Detach() }
    Assert ($surface.Snapshot($false).SpriteBytes -eq 0) 'Detaching the actual surface must release active and cached pixel accounting.'
    $surface=[Activator]::CreateInstance($type,$true)
    try{
        $surface.Measure([System.Windows.Size]::new(3840,2160)); $surface.Arrange([System.Windows.Rect]::new(0,0,3840,2160))
        $settings=[Popcorn.DanmakuSettings]::new(); $settings.FontSize=64; $settings.OutlineWidth=3; $settings.AreaFraction=1; $surface.Configure($settings)
        $surface.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,16777215,([string][char]0x6d4b)*120)))
        $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0); $surface.Frame()
        $snapshot=$surface.Snapshot($false)
        Assert ($snapshot.ActiveCount -eq 0 -and $snapshot.SpritesCreated -eq 0 -and $snapshot.RejectedSprites -eq 1) 'The actual maximum-font 4K geometry must be rejected before allocating an oversized sprite.'
    }finally{ $surface.Detach() }
    $surface=[Activator]::CreateInstance($type,$true)
    try{
        $surface.Measure([System.Windows.Size]::new(1280,720)); $surface.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $surface.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(4000,1,16777215,'motion')))
        $surface.UpdateClock(4000,$false,[Popcorn.Monotonic]::Now,1,$false,0); $surface.Frame()
        $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0); $surface.Frame(); $before=$surface.Snapshot($false)
        $settings=[Popcorn.DanmakuSettings]::new(); $settings.SpeedMultiplier=2; $surface.Configure($settings); $surface.Frame(); $after=$surface.Snapshot($false)
        Assert ($after.ActiveCount -eq 1 -and [Math]::Abs($before.X[0]-$after.X[0]) -lt .01) 'Resource admission must not regress position-preserving speed changes.'
        $surface.Measure([System.Windows.Size]::new(960,540)); $surface.Arrange([System.Windows.Rect]::new(0,0,960,540)); $surface.Frame(); $resized=$surface.Snapshot($false)
        Assert ($resized.ActiveCount -eq 1 -and [Math]::Abs($after.Progress[0]-$resized.Progress[0]) -lt .001) 'Resource rebuilds must preserve retimed motion through resize.'
    }finally{ $surface.Detach() }
    'PASS: offscreen surface budget, paused reuse, rebuild/expiry/detach accounting, oversize admission and motion preservation.'
}
if($Group -in @('EmptyLoad','All')){
    $type=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.OverlaySurface',$true)
    foreach($controlsOnly in @($false,$true)){
        $surface=[Activator]::CreateInstance($type,$true)
        try{
            $surface.Measure([System.Windows.Size]::new(1280,720)); $surface.Arrange([System.Windows.Rect]::new(0,0,1280,720))
            $surface.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,16777215,'small')))
            $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0); $surface.Frame()
            Assert ($surface.Snapshot($false).ActiveCount -eq 1) 'The empty-load fixture must start with a pinned active sprite.'
            $empty=[Popcorn.Comment[]]::new(0)
            if($controlsOnly){ $empty=[Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,16777215,"`n`t")) }
            $surface.Load($empty)
            $snapshot=$surface.Snapshot($false)
            Assert (-not $surface.HasComments -and $snapshot.ActiveCount -eq 0 -and $snapshot.SpriteBytes -eq 0) 'Clearing the source must release active and cached sprites immediately, without a future visible Frame.'
        }finally{ $surface.Detach() }
    }
    'PASS: empty and normalized-empty sources release pixels without a subsequent frame.'
}
if($Group -in @('Cadence','All')){
    $type=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.OverlaySurface',$true)
    Assert ($null -ne $type.GetMethod('FrameTick')) 'Cadence must be observed in the real composition callback separately from queued build work.'
    $surface=[Activator]::CreateInstance($type,$true)
    try {
        $surface.Measure([System.Windows.Size]::new(1280,720));$surface.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $surface.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,0xffffff,'真实回调计时')))
        $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
        $surface.StepFrame();$surface.StepFrame()
        Assert ($surface.Intervals.Count -eq 0) 'Build work must not manufacture frame interval samples.'
        Assert ($surface.FrameTick($true) -and $surface.FrameTick($false) -and $surface.Intervals.Count -eq 1) 'All accepted composition ticks must be counted, including repeated rendering targets.'
        $surface.StepFrame()
        Assert ($surface.Intervals.Count -eq 1) 'A queued build must not add another cadence sample.'
        $now=[Popcorn.Monotonic]::Now
        $surface.UpdateClock(10000,$true,$now,1,$true,$now-2000)
        $surface.FrameTick($false)|Out-Null
        Assert (-not $surface.Snapshot($false).Running) 'Proof expiry must freeze the clock in the composition tick without waiting for build work.'
        $surface.Detach();$count=$surface.Intervals.Count
        Assert (-not $surface.FrameTick($true) -and $surface.Intervals.Count -eq $count -and -not $surface.NeedsWork) 'Detached surfaces must reject ticks and pending build readiness.'
    } finally {$surface.Detach()}
    'PASS: actual composition ticks own cadence accounting and stale-proof freezing; queued work and detached surfaces cannot fabricate frame counts.'
}
if($Group -in @('Incremental','All')){
    $rendererSource=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Overlay.cs'))
    Assert ($rendererSource.Contains('if (IsVisible) Surface.StepFrame();')) 'The actual presentation callback must use the incremental production path.'
    $type=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.OverlaySurface',$true)
    $surface=[Activator]::CreateInstance($type,$true)
    try {
        Assert ($null -ne $type.GetMethod('StepFrame')) 'The actual renderer needs a bounded incremental production frame path.'
        $surface.Measure([System.Windows.Size]::new(1280,720));$surface.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $settings=[Popcorn.DanmakuSettings]::new();$settings.AreaFraction=1;$settings.Density=100;$settings.Dedupe=$false;$surface.Configure($settings)
        $comments=[Popcorn.Comment[]]::new(60)
        for($i=0;$i -lt $comments.Length;$i++){$comments[$i]=[Popcorn.Comment]::new(10000,5,0xffffff,('分步测试 '+$i))}
        $surface.Load($comments);$surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
        Assert ($surface.Snapshot($false).LayoutPending) 'An unstarted rebuild must not claim a settled layout.'
        $before=$surface.Snapshot($false);$surface.StepFrame();$after=$surface.Snapshot($false)
        $initialFontPx=$after.FontPx
        Assert ($surface.HasPendingFrame -and ($after.GeometryBuilds-$before.GeometryBuilds)+($after.SpritesCreated-$before.SpritesCreated) -le 1) 'A production step must not build the whole comment batch.'
        Assert ($after.LayoutPending) 'A paused partial layout must remain explicitly preparing.'
        $settings=[Popcorn.DanmakuSettings]::new();$settings.FontSize=40;$settings.AreaFraction=1;$settings.Density=100;$settings.Dedupe=$false;$surface.Configure($settings)
        Assert (-not $surface.HasPendingFrame) 'A settings generation must cancel its unfinished layout before returning.'
        Assert ($surface.Snapshot($false).LayoutPending) 'Cancelling an old iterator must not mark the new rebuild complete.'
        $steps=0
        do {
            $before=$surface.Snapshot($false);$surface.StepFrame();$after=$surface.Snapshot($false);$steps++
            Assert (($after.GeometryBuilds-$before.GeometryBuilds)+($after.SpritesCreated-$before.SpritesCreated) -le 1) 'Every incremental callback must limit geometry and bitmap work.'
            Assert ($after.SpriteBytes -le [Popcorn.SpriteLimits]::MaxResidentBytes) 'Pending layout work must remain inside the original pixel budget.'
            Assert ($steps -le 500) 'A finite paused batch must finish without an unbounded pending queue.'
        } while($surface.HasPendingFrame)
        Assert ($after.ActiveCount -gt 0 -and [Math]::Abs($after.FontPx-$initialFontPx*40/28) -lt .001) 'The final requested layout must actually create active sprites.'
        Assert (-not $after.LayoutPending) 'The completed paused batch must report settled layout.'
        $visibleBefore=$after
        $settings=[Popcorn.DanmakuSettings]::new();$settings.FontSize=48;$settings.AreaFraction=1;$settings.Density=100;$settings.Dedupe=$false;$surface.Configure($settings)
        $surface.StepFrame();$preparing=$surface.Snapshot($false)
        Assert ($preparing.ActiveCount -eq $visibleBefore.ActiveCount -and $preparing.FontPx -eq $visibleBefore.FontPx) 'A hot visual rebuild must retain one complete prior layout while replacements are prepared.'
        do {
            $surface.StepFrame();$current=$surface.Snapshot($false)
            Assert ($current.ActiveCount -gt 0 -and $current.SpriteBytes -le [Popcorn.SpriteLimits]::MaxResidentBytes) 'Visual replacement must not blank the active load or escape pixel accounting.'
        } while($surface.HasPendingFrame)
        Assert ([Math]::Abs($current.FontPx-$initialFontPx*48/28) -lt .001) 'Prepared visual resources must eventually commit the requested font as a complete layout.'
        $surface.Load([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,0xffffff,'late prepared geometry')))
        $surface.StepFrame();$createdBeforeExpiry=$surface.Snapshot($false).SpritesCreated
        $surface.UpdateClock(20000,$false,[Popcorn.Monotonic]::Now,1,$false,0)
        do {$surface.StepFrame()}while($surface.HasPendingFrame)
        Assert ($surface.Snapshot($false).ActiveCount -eq 0 -and $surface.Snapshot($false).SpritesCreated -eq $createdBeforeExpiry) 'A prepared geometry that expires before bitmap admission must not allocate late pixels.'
        $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0);$surface.Load($comments);$surface.StepFrame()
        Assert $surface.HasPendingFrame 'The seek-cancellation fixture must have unfinished layout work.'
        $surface.UpdateClock(20000,$false,[Popcorn.Monotonic]::Now,2,$false,0)
        Assert (-not $surface.HasPendingFrame) 'A confirmed seek must cancel unfinished items from the old timeline.'
        do {$surface.StepFrame()}while($surface.HasPendingFrame)
        Assert ($surface.Snapshot($false).ActiveCount -eq 0) 'Old expired comments must not appear after an incremental seek.'
        $surface.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,2,$false,0);$surface.Load($comments);$surface.StepFrame()
        Assert $surface.HasPendingFrame 'The detach fixture must have unfinished layout work.'
        $surface.Detach()
        $surface.StepFrame()
        Assert (-not $surface.HasPendingFrame -and $surface.Snapshot($false).SpriteBytes -eq 0) 'Detach must cancel pending work and prevent later pixel allocation.'
        Assert (-not $surface.Snapshot($false).LayoutPending) 'Detached resources must not report pending layout work.'
    } finally {$surface.Detach()}
    $measurement=[Activator]::CreateInstance($type,$true)
    $one=[Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,5,0xffffff,'bounded replacement'))
    try {
        $measurement.Measure([System.Windows.Size]::new(1280,720));$measurement.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $measurement.Load($one);$measurement.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0);$measurement.Frame()
        $small=$measurement.Snapshot($false)
        $next=[Popcorn.DanmakuSettings]::new();$next.FontSize=40;$measurement.Configure($next);$measurement.Frame()
        $type.GetField('shapes',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($measurement).ClearUnused()
        $replacement=$measurement.Snapshot($false)
        $limit=[long][Math]::Max($small.SpriteBytes,$replacement.SpriteBytes)
    } finally {$measurement.Detach()}
    $bounded=[Activator]::CreateInstance($type,[object[]]@($limit))
    try {
        $bounded.Measure([System.Windows.Size]::new(1280,720));$bounded.Arrange([System.Windows.Rect]::new(0,0,1280,720))
        $bounded.Load($one);$bounded.UpdateClock(10000,$false,[Popcorn.Monotonic]::Now,1,$false,0);$bounded.Frame()
        $bounded.UpdateClock(10500,$false,[Popcorn.Monotonic]::Now,1,$false,0);$bounded.Frame()
        $oldProgress=$bounded.Snapshot($false).Progress[0]
        $oldFont=$bounded.Snapshot($false).FontPx
        $next=[Popcorn.DanmakuSettings]::new();$next.FontSize=40;$bounded.Configure($next);$attempts=0
        do {
            $bounded.StepFrame();$attempts++
            Assert ($bounded.Snapshot($false).SpriteBytes -le $limit -and $attempts -le 100) 'A retained-layout reservation failure must fall back without exceeding the exact bitmap budget or looping.'
        } while($bounded.HasPendingFrame)
        $final=$bounded.Snapshot($false)
        Assert ($final.ActiveCount -eq 1 -and [Math]::Abs($final.FontPx-$oldFont*40/28) -lt .001 -and $final.RejectedSprites -gt 0) 'A budget that fits only one layout must still finish the latest font after rejecting the temporary double layout.'
        Assert ([Math]::Abs($final.Progress[0]-$oldProgress) -lt .001) 'A pixel-budget fallback must preserve an already-entered comment without replaying or dropping it.'
    } finally {$bounded.Detach()}
    'PASS: actual incremental frame work bounds, final layout, generation/seek cancellation, expiry and detach.'
}
