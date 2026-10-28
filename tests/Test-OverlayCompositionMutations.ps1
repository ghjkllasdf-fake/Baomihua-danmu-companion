#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$review=Join-Path $root ('.cache\overlay-composition-review-'+[Guid]::NewGuid().ToString('N'))
foreach($directory in @('desktop\native','tests\fixtures')){[void][IO.Directory]::CreateDirectory((Join-Path $review $directory))}
$files=@('desktop\native\Windows.cs','desktop\native\OverlayPresentation.cs','tests\Test-OverlayPresentation.ps1','tests\Test-DwmOverlayInitialization.ps1','tests\fixtures\OverlayPresentationTests.cs.in','tests\fixtures\DwmOverlayInitializationTests.cs.in')
foreach($file in $files){Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $review $file)}
$native=[IO.File]::ReadAllText((Join-Path $review 'desktop\native\Windows.cs'))
$presentation=[IO.File]::ReadAllText((Join-Path $review 'desktop\native\OverlayPresentation.cs'))
$variants=@(
    @{Name='parent-paints-child';File='Windows.cs';Original='styles,"STATIC","Popcorn danmaku overlay",0x82000000,';Replacement='styles,"STATIC","Popcorn danmaku overlay",0x80000000,';Test='Test-DwmOverlayInitialization.ps1';Expected='exclude child pixels'},
    @{Name='missing-transparent';File='Windows.cs';Original='const uint styles=0x80000|0x20|0x08000000|0x80|0x8;';Replacement='const uint styles=0x80000|0x08000000|0x80|0x8;';Test='Test-DwmOverlayInitialization.ps1';Expected='layered, transparent'},
    @{Name='disabled-composition';File='Windows.cs';Original='DwmIsCompositionEnabled(out bool enabled)<0 || !enabled ||';Replacement='DwmIsCompositionEnabled(out bool enabled)<0 ||';Test='Test-DwmOverlayInitialization.ps1';Expected='partial failure'},
    @{Name='leak-failed-parent';File='Windows.cs';Original='finally { if(!accepted) DestroyWindow(window); }';Replacement='finally { if(!accepted) { } }';Test='Test-DwmOverlayInitialization.ps1';Expected='partial failure'},
    @{Name='leak-native-before-fallback';File='OverlayPresentation.cs';Original='result.ReleaseNative();';Replacement='/* mutation: leak */';Test='Test-OverlayPresentation.ps1';Expected='Failed native initialization leaked'},
    @{Name='attached-root-disposal';File='OverlayPresentation.cs';Original='source.RootVisual=null;';Replacement='';Test='Test-OverlayPresentation.ps1';Expected='Detach the root'},
    @{Name='leak-child-source';File='OverlayPresentation.cs';Original='source.Dispose();';Replacement='';Test='Test-OverlayPresentation.ps1';Expected='exactly once'},
    @{Name='legacy-input-styles';File='OverlayPresentation.cs';Original='NativeWindows.ClickThrough(result.Handle);';Replacement='';Test='Test-OverlayPresentation.ps1';Expected='Legacy input styles'},
    @{Name='missing-child-resize';File='OverlayPresentation.cs';Original='NativeWindows.ResizeOverlayChild(source.Handle,width,height);';Replacement='System.GC.KeepAlive(width);';Test='Test-OverlayPresentation.ps1';Expected='Resize the child'}
)
foreach($variant in $variants){
    [IO.File]::WriteAllText((Join-Path $review 'desktop\native\Windows.cs'),$native)
    [IO.File]::WriteAllText((Join-Path $review 'desktop\native\OverlayPresentation.cs'),$presentation)
    $target=Join-Path $review ('desktop\native\'+$variant.File)
    $text=[IO.File]::ReadAllText($target)
    if(($text.Split($variant.Original).Count-1) -ne 1){throw ('Mutation seam was not unique: '+$variant.Name)}
    [IO.File]::WriteAllText($target,$text.Replace($variant.Original,$variant.Replacement))
    $output=& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $review ('tests\'+$variant.Test)) 2>&1 | Out-String -Width 240
    [IO.File]::WriteAllText((Join-Path $review ($variant.Name+'.log')),$output)
    if($LASTEXITCODE -eq 0 -or -not $output.Contains($variant.Expected)){throw ('Mutation survived or failed at the wrong assertion: '+$variant.Name+"`n"+$output)}
    'PASS: rejected '+$variant.Name
}
'Retained isolated mutation evidence: '+$review
