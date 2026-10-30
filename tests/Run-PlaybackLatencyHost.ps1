#requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$OwnerNonce,
    [switch]$CompileOnly
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'tests/Import-NativeTypes.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'fixtures\PlaybackLatencyObserver.cs') -ReferencedAssemblies ($references | Select-Object -Unique)
if($CompileOnly){ 'PASS: actual desktop and test-only playback observer compile'; return }
[System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2) | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()
$profile='playback-'+$OwnerNonce.Substring(0,12)
$testApp=& (Join-Path $PSScriptRoot 'New-NativeTestApplication.ps1')
$window=[Popcorn.DesktopWindow]::new($testApp,'',$profile,(Join-Path $root '.cache'))
$comments=[Popcorn.Comment[]]::new(50000)
for($i=0;$i -lt $comments.Length;$i++){ $comments[$i]=[Popcorn.Comment]::new($i*100,1,16777215,('播放响应测试 '+$i)) }
[void][IO.Directory]::CreateDirectory($RunDirectory)
$hashFiles=@(Get-ChildItem (Join-Path $root 'desktop') -Recurse -File)+@(Get-ChildItem (Join-Path $root 'ui/dist') -Recurse -File)
$hashFiles+=@(Get-Item (Join-Path $PSScriptRoot 'fixtures/PlaybackLatencyObserver.cs'),$PSCommandPath,(Join-Path $root 'scripts/PlaybackLatency.mjs'),(Join-Path $root 'scripts/Check-PlaybackLatency.mjs'),(Join-Path $root 'scripts/PlaybackInputDriver.mjs'),(Join-Path $PSScriptRoot 'playback-latency.test.mjs'),(Join-Path $PSScriptRoot 'playback-input-driver.test.mjs'))
$hashFiles+=@(Get-Item (Join-Path $PSScriptRoot 'Import-NativeTypes.ps1'),(Join-Path $PSScriptRoot 'New-NativeTestApplication.ps1'))
$before=@($hashFiles | ForEach-Object { [pscustomobject]@{path=[IO.Path]::GetRelativePath($root,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
$observer=[PlaybackLatencyObserver]::new($window,$comments,$RunDirectory,$OwnerNonce)
$window.OpenWorkbench()
try { [System.Windows.Forms.Application]::Run($window) } finally { $observer.Dispose() }
$after=@($hashFiles | ForEach-Object { [pscustomobject]@{path=[IO.Path]::GetRelativePath($root,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
$stable=($before|ConvertTo-Json -Compress) -ceq ($after|ConvertTo-Json -Compress)
[pscustomobject]@{closedNormally=$observer.ClosedNormally;sourceStable=$stable;before=$before;after=$after}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $RunDirectory 'sources.json') -Encoding utf8
if(-not $observer.ClosedNormally -or -not $stable){ throw 'Playback observer close/source validation failed.' }
'PASS: owned playback-response desktop closed normally; source endpoints unchanged.'
