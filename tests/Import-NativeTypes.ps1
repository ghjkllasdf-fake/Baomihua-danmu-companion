#requires -Version 7.6
# Development-only assembly loader. Does not launch the application or read user settings.
param([string]$ApplicationRoot)
$ErrorActionPreference='Stop'
if(-not $IsWindows -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6){
    throw 'Windows x64 PowerShell 7.6 is required for the native test harness.'
}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Run the native test harness with -STA.'}
$root=if($ApplicationRoot){[IO.Path]::GetFullPath($ApplicationRoot)}else{Split-Path $PSScriptRoot -Parent}
foreach($name in @('System.Windows.Forms','PresentationFramework','PresentationCore','WindowsBase','UIAutomationClient','UIAutomationTypes')){
    Add-Type -AssemblyName $name
}
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$sdk=if($ApplicationRoot){$root}else{Join-Path $root '.deps/webview2/lib_manual/netcoreapp3.0'}
$mediaSdk=if($ApplicationRoot){$root}else{Join-Path $root '.deps/windows-sdk/lib/net8.0'}
foreach($assembly in @((Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll'),(Join-Path $mediaSdk 'WinRT.Runtime.dll'),(Join-Path $mediaSdk 'Microsoft.Windows.SDK.NET.dll'))){
    Add-Type -Path $assembly
}
$references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
$references+=@(Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll'|Where-Object {$_.Name -match '^(Presentation(Core|Framework)|WindowsBase|UIAutomation|System\.Windows|System\.Drawing|Microsoft\.Win32\.SystemEvents)'}|ForEach-Object FullName)
$references+=@((Join-Path $sdk 'Microsoft.Web.WebView2.Core.dll'),(Join-Path $PSHOME 'System.Security.Cryptography.ProtectedData.dll'))
if($ApplicationRoot){
    $assembly=Join-Path $root 'DanmuCompanion.dll'
    Add-Type -Path $assembly
    $references+=$assembly
    $loader=$root
}else{
    Add-Type -Path @(Get-ChildItem -LiteralPath (Join-Path $root 'desktop/native') -Filter '*.cs'|ForEach-Object FullName) -ReferencedAssemblies ($references|Select-Object -Unique)
    $loader=Join-Path $root '.deps/webview2/runtimes/win-x64/native'
}
[Microsoft.Web.WebView2.Core.CoreWebView2Environment]::SetLoaderDllFolderPath($loader)
'PASS: native test assemblies loaded; no application or player started.'
