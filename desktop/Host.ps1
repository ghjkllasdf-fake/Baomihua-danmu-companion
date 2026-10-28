#requires -Version 7.6
param([switch]$Check, [int]$SmokeSeconds=0, [ValidateRange(0,65535)][int]$DebugPort=0, [ValidatePattern('^[a-z0-9][a-z0-9-]{0,47}$')][string]$DataProfile='app', [switch]$NoDefaultApi)
$ErrorActionPreference='Stop'
if(-not $IsWindows -or $PSVersionTable.PSEdition -ne 'Core' -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6 -or $PSVersionTable.PSVersion.ToString().Contains('-')){ throw 'Windows x64 PowerShell 7.6 stable is required.' }
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){ throw 'Start with pwsh.exe -STA.' }
$root=Split-Path $PSScriptRoot -Parent
$sdk=Join-Path $root '.deps\webview2\lib_manual\netcoreapp3.0'
$dist=Join-Path $root 'ui\dist'
$index=Join-Path $dist 'index.html'
if(-not (Test-Path -LiteralPath $index)){ throw 'React assets missing. Run npm.cmd --prefix ui run build.' }
$assets=[regex]::Matches([IO.File]::ReadAllText($index),'(?:src|href)="(?:\./|/)?(?<asset>assets/[^"\r\n]+)"')
if(-not @($assets|Where-Object {$_.Groups['asset'].Value.EndsWith('.js')}).Count){ throw 'React asset missing: no compiled script entry. Run npm.cmd --prefix ui run build.' }
$distRoot=[IO.Path]::GetFullPath($dist)+[IO.Path]::DirectorySeparatorChar
foreach($match in $assets){
    $asset=[IO.Path]::GetFullPath((Join-Path $dist $match.Groups['asset'].Value))
    if(-not $asset.StartsWith($distRoot,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $asset -PathType Leaf)){ throw ('React asset missing or invalid: '+$match.Groups['asset'].Value+'. Rebuild the UI.') }
}
if(-not (Test-Path -LiteralPath (Join-Path $root '.deps\pwsh\pwsh.exe') -PathType Leaf)){ throw 'Probe runtime missing. Run scripts/Setup-Desktop.ps1.' }
if(-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'ProbeWorker.ps1') -PathType Leaf)){ throw 'Probe worker missing. Restore desktop/ProbeWorker.ps1.' }
if(-not (Test-Path -LiteralPath (Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll')) -or -not (Test-Path -LiteralPath (Join-Path $root '.deps\webview2\runtimes\win-x64\native\WebView2Loader.dll'))){ throw 'WebView2 SDK missing. Run scripts/Setup-Desktop.ps1.' }
$mediaSdk=Join-Path $root '.deps\windows-sdk\lib\net8.0'
if(-not (Test-Path -LiteralPath (Join-Path $mediaSdk 'WinRT.Runtime.dll')) -or -not (Test-Path -LiteralPath (Join-Path $mediaSdk 'Microsoft.Windows.SDK.NET.dll'))){ throw 'Windows media SDK missing. Run scripts/Setup-Desktop.ps1.' }
foreach($name in @('System.Windows.Forms','PresentationFramework','PresentationCore','WindowsBase','UIAutomationClient','UIAutomationTypes')){ Add-Type -AssemblyName $name }
Add-Type -Path (Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll')
Add-Type -Path (Join-Path $mediaSdk 'WinRT.Runtime.dll')
Add-Type -Path (Join-Path $mediaSdk 'Microsoft.Windows.SDK.NET.dll')
$references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
$references+=@(Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll' | Where-Object { $_.Name -match '^(Presentation(Core|Framework)|WindowsBase|UIAutomation|System\.Windows|System\.Drawing|Microsoft\.Win32\.SystemEvents)' } | ForEach-Object FullName)
$references+=@(Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll')
Add-Type -Path @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'native') -Filter '*.cs' | ForEach-Object FullName) -ReferencedAssemblies ($references | Select-Object -Unique)
if($Check){ 'PASS: PS7 native desktop assemblies compiled'; return }
$initialApi=''
if(-not $NoDefaultApi -and -not [string]::IsNullOrWhiteSpace($env:DANMU_API_BASE)){
    $initialApi=[Popcorn.ApiClient]::NormalizeBase($env:DANMU_API_BASE)
}
[System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2) | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()
$window=[Popcorn.DesktopWindow]::new($root,$initialApi,$DataProfile)
if($DebugPort -gt 0){ $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=$DebugPort --remote-debugging-address=127.0.0.1" }
$smokeTimer=$null
if($SmokeSeconds -gt 0){ $smokeTimer=[System.Windows.Forms.Timer]::new(); $smokeTimer.Interval=$SmokeSeconds*1000; $smokeTimer.add_Tick({ $smokeTimer.Stop(); $window.Close() }); $smokeTimer.Start() }
try { [System.Windows.Forms.Application]::Run($window) } finally { if($smokeTimer){ $smokeTimer.Dispose() } }
if($SmokeSeconds -gt 0){
    [pscustomobject]@{Ready=$window.Ready;Failure=$window.Failure} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root '.cache\desktop-smoke.json')
    if(-not $window.Ready){ throw 'React/WebView2 handshake did not complete.' }
    'PASS: independent PS7, WebView2 and local React handshake, message pump and close'
}
