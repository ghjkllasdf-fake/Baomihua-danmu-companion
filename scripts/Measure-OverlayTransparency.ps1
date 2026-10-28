#requires -Version 7.6
# Static, explicitly synthetic visual diagnostic. Reads player bounds; never controls playback.
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(5,60)][int]$Seconds=20,
    [double]$AreaFraction=0.5,
    [string]$NativeSourceDirectory=''
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(Test-Path -LiteralPath $OutputPath){throw 'Choose a new diagnostic output file.'}
if(-not $NativeSourceDirectory){$NativeSourceDirectory=Join-Path $root 'desktop\native'}
foreach($name in @('System.Windows.Forms','PresentationFramework','PresentationCore','WindowsBase','UIAutomationClient','UIAutomationTypes')){Add-Type -AssemblyName $name}
foreach($path in @('.deps\webview2\lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.Core.dll','.deps\windows-sdk\lib\net8.0\WinRT.Runtime.dll','.deps\windows-sdk\lib\net8.0\Microsoft.Windows.SDK.NET.dll')){Add-Type -Path (Join-Path $root $path)}
$references=@(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
$references+=@(Get-ChildItem $PSHOME -Filter '*.dll'|Where-Object {$_.Name -match '^(Presentation(Core|Framework)|WindowsBase|UIAutomation|System\.Windows|System\.Drawing|Microsoft\.Win32\.SystemEvents)'}|ForEach-Object FullName)
$references+=Join-Path $root '.deps\webview2\lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.Core.dll'
Add-Type -Path @(Get-ChildItem -LiteralPath $NativeSourceDirectory -File -Filter '*.cs'|ForEach-Object FullName) -ReferencedAssemblies ($references|Select-Object -Unique)
[void][System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2)
$target=[Popcorn.NativeWindows]::FindPlayer()
if(-not $target){throw 'An existing player is required for the transparent-underlay diagnostic.'}
$overlay=[Popcorn.OverlayHost]::new()
$settings=[Popcorn.DanmakuSettings]::new()
$settings.AreaFraction=$AreaFraction
$overlay.SetSettings($settings)
$comments=[Popcorn.Comment[]]::new(100)
for($index=0;$index -lt $comments.Length;$index++){$comments[$index]=[Popcorn.Comment]::new(9000+$index*10,1,16777215,('透明区域测试（合成）'+$index))}
$overlay.SetComments($comments)
$watch=[Diagnostics.Stopwatch]::StartNew()
try {
    while($watch.Elapsed.TotalSeconds -lt $Seconds){
        $overlay.Update($target,10000,$false,1,$false,0)
        Start-Sleep -Milliseconds 100
    }
    [pscustomobject]@{CompositionMode=$overlay.CompositionMode;AreaFraction=$AreaFraction;NativeSources=[IO.Path]::GetFullPath($NativeSourceDirectory);Snapshot=$overlay.Snapshot();Scope='Static synthetic visual diagnostic over existing video; no automatic-sync or presentation-rate claim.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $OutputPath
} finally {$overlay.Dispose()}
