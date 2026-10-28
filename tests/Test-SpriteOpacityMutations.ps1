#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\sprite-opacity-review-'+[guid]::NewGuid().ToString('N'))
foreach($part in @('tests','ui')){[void][IO.Directory]::CreateDirectory((Join-Path $folder $part))}
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
Copy-Item -LiteralPath (Join-Path $root 'ui\dist') -Destination (Join-Path $folder 'ui') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-RendererResources.ps1') -Destination (Join-Path $folder 'tests')
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$invoker=Join-Path $folder 'Invoke-Test.ps1'
[IO.File]::WriteAllText($invoker,@'
param([string]$TestScript)
$ErrorActionPreference='Stop'
try { & $TestScript -Group Opacity } catch { [Console]::Error.WriteLine($_.Exception.GetBaseException().Message); exit 1 }
'@)
$storePath=Join-Path $folder 'desktop\native\SpriteStore.cs'
$overlayPath=Join-Path $folder 'desktop\native\Overlay.cs'
$store=[IO.File]::ReadAllText($storePath)
$overlay=[IO.File]::ReadAllText($overlayPath)
$cases=@(
    @{Name='unfrozen-brush';File='SpriteStore.cs';Old='alphaBrush.Freeze();';New='';Expected='Opacity brushes must freeze'},
    @{Name='cloned-image';File='SpriteStore.cs';Old='new ImageBrush(Image)';New='new ImageBrush(Image.Clone())';Expected='Opacity brushes must freeze and share'},
    @{Name='uncached-brush';File='SpriteStore.cs';Old='alphaBrush==null || brushOpacity!=opacity';New='alphaBrush==null || brushOpacity!=opacity || opacity>=0';Expected='Unchanged opacity must reuse its brush.'},
    @{Name='stale-opacity';File='SpriteStore.cs';Old='alphaBrush==null || brushOpacity!=opacity';New='alphaBrush==null && brushOpacity!=opacity';Expected='Changing opacity must preserve'},
    @{Name='drawing-bypasses-alpha';File='Overlay.cs';Old='dc.DrawRectangle(b.Sprite.AlphaBrush(settings.Opacity),null,new Rect(x,y,b.Sprite.Width,b.Sprite.Height));';New='dc.DrawImage(b.Sprite.Image,new Rect(x,y,b.Sprite.Width,b.Sprite.Height));';Expected='Actual surface text and outline must render at the requested opacity.'}
)
foreach($case in $cases){
    [IO.File]::WriteAllText($storePath,$store);[IO.File]::WriteAllText($overlayPath,$overlay)
    $target=Join-Path $folder ('desktop\native\'+$case.File)
    $source=[IO.File]::ReadAllText($target)
    if(($source.Split($case.Old).Count-1) -ne 1){throw ('Opacity mutation seam not unique: '+$case.Name)}
    [IO.File]::WriteAllText($target,$source.Replace($case.Old,$case.New))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -STA -File $invoker -TestScript (Join-Path $folder 'tests\Test-RendererResources.ps1') 2>&1|Out-String -Width 240)
    [IO.File]::WriteAllText((Join-Path $folder ($case.Name+'.log')),$output)
    if($LASTEXITCODE -eq 0 -or -not ($output -replace '\s+',' ').Contains($case.Expected)){throw ('Opacity mutation survived or failed unexpectedly: '+$case.Name+"`n"+$output)}
    'PASS: rejected opacity mutation '+$case.Name
}
'Opacity mutation evidence retained at '+$folder
