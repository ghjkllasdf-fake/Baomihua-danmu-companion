#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'tests/Import-NativeTypes.ps1')
$type=[Popcorn.OverlayHost].Assembly.GetType('Popcorn.OverlaySurface',$true)
$surface=[Activator]::CreateInstance($type,$true)
$output=Join-Path $root '.cache/readme-demo'
[void][IO.Directory]::CreateDirectory($output)
try{
    $surface.Measure([Windows.Size]::new(1280,720))
    $surface.Arrange([Windows.Rect]::new(0,0,1280,720))
    $texts=@('这片海的颜色好舒服','日落好漂亮','下班后的放松时刻','今天也要好好休息','这个画面可以多看一会儿','弹幕陪你一起看','海风与落日，刚刚好','这段配色好温柔','慢慢看，不着急','一起等太阳落下','在这里留个脚印','示例弹幕 · 向左滚动')
    $comments=[Popcorn.Comment[]]::new($texts.Count)
    for($i=0;$i -lt $texts.Count;$i++){$comments[$i]=[Popcorn.Comment]::new($i*500,1, $(if($i%4 -eq 0){0xFFE56B}else{0xFFFFFF}),$texts[$i])}
    $surface.Load($comments)
    for($position=0;$position -le 7500;$position+=250){$surface.UpdateClock($position,$false,[Popcorn.Monotonic]::Now,1,$false,0);$surface.Frame()}
    $snapshot=$surface.Snapshot($false)
    if($snapshot.ActiveCount -lt 6){throw 'The actual renderer produced too few visible example comments.'}
    $visual=[Windows.Media.DrawingVisual]::new()
    $drawing=$visual.RenderOpen()
    try{$type.GetMethod('OnRender',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($surface,[object[]]@($drawing))|Out-Null}finally{$drawing.Close()}
    $image=[Windows.Media.Imaging.RenderTargetBitmap]::new(1280,720,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $image.Render($visual)
    $pixels=[byte[]]::new(1280*720*4);$image.CopyPixels($pixels,1280*4,0)
    $visiblePixels=0;for($i=3;$i -lt $pixels.Length;$i+=4){if($pixels[$i] -gt 20){$visiblePixels++}}
    if($visiblePixels -lt 1000){throw 'The native example-comment frame is blank.'}
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
    $stream=[IO.File]::Create((Join-Path $output 'overlay.png'))
    try{$encoder.Save($stream)}finally{$stream.Dispose()}
    [ordered]@{width=1280;height=720;comments=$snapshot.ActiveCount;visiblePixels=$visiblePixels;renderer='production OverlaySurface';fontSetting=22;outlineSetting=0;speed=0.8;scope='Offscreen native renderer output for an explicitly labelled README demonstration; not a player detection acceptance test.'}|ConvertTo-Json|Set-Content (Join-Path $output 'overlay-evidence.json')
    'PASS: original example comments captured from the actual native renderer; no player or user configuration accessed.'
}finally{$surface.Detach()}
