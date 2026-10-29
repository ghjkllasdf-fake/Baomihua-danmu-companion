#requires -Version 7.6
param([Parameter(Mandatory)][string]$RunDirectory,[Parameter(Mandatory)][ValidatePattern('^latency-[a-f0-9]{12}$')][string]$Profile,[Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$OwnerNonce,[switch]$PlaybackLoad)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop\Host.ps1') -Check
$observerSource=Join-Path $PSScriptRoot 'fixtures\SettingsLatencyObserver.cs'
Add-Type -Path $observerSource -ReferencedAssemblies ($references | Select-Object -Unique)
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS='--remote-debugging-port=9232 --remote-debugging-address=127.0.0.1'
[System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2) | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()
$window=[Popcorn.DesktopWindow]::new($root,'',$Profile)
$comments=$null
if($PlaybackLoad){
    $comments=[Popcorn.Comment[]]::new(50000)
    for($i=0;$i -lt $comments.Length;$i++){ $comments[$i]=[Popcorn.Comment]::new($i*100,1,16777215,('设置负载测试 '+$i)) }
}
$observer=[SettingsLatencyObserver]::new($window,(Join-Path $RunDirectory 'native.json'),(Join-Path $RunDirectory 'control.txt'),$OwnerNonce,$comments)
$window.OpenWorkbench()
try { [System.Windows.Forms.Application]::Run($window) } finally { $observer.Dispose() }
$latest=Get-ChildItem -LiteralPath $RunDirectory -Filter 'native-*.json' | Sort-Object Name | Select-Object -Last 1
$evidence=Get-Content -LiteralPath $latest.FullName -Raw | ConvertFrom-Json
if(-not $evidence.finished -or $evidence.failure){ throw 'Settings latency observer did not finish cleanly.' }
'PASS: owned latency-test desktop closed after actual configuration observations.'
