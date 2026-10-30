#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
try {
    if(-not $IsWindows -or -not [Environment]::Is64BitProcess -or $PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion.Major -ne 7 -or $PSVersionTable.PSVersion.Minor -ne 6 -or $PSVersionTable.PSVersion.ToString().Contains('-')){ throw 'Unsupported probe runtime.' }
    if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'MTA'){ throw 'Probe requires MTA.' }
    Add-Type -LiteralPath (Join-Path $root 'desktop\native\ProbeGate.cs')
    $null=[Popcorn.ProbeGate]::OpenAsync([Console]::OpenStandardInput(),[TimeSpan]::FromSeconds(5),[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    # PROBE_PLAYER_BOUNDARY: no player API or SDK activation before the owned-parent gate.
    $sdk=Join-Path $root '.deps\windows-sdk\lib\net8.0'
    foreach($name in @('WindowsBase','UIAutomationClient','UIAutomationTypes')){ Add-Type -AssemblyName $name }
    Add-Type -LiteralPath (Join-Path $sdk 'WinRT.Runtime.dll')
    Add-Type -LiteralPath (Join-Path $sdk 'Microsoft.Windows.SDK.NET.dll')
    $references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
    $references+=@(Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll' | Where-Object {$_.Name -match '^(WindowsBase|UIAutomation|System\.Windows)'} | ForEach-Object FullName)
    $paths=@('Core.cs','PlaybackModels.cs','SeriesIdentity.cs','LibrarySeasonReader.cs','CachedSeasonReader.cs','ProbeTransport.cs','ProbeOperation.cs','PlaybackEventSignal.cs','MediaSession.cs','Windows.cs','PlayerControlCache.cs','UiaPlayerControls.cs','PlayerSampler.cs','ProbeCadence.cs','ProbeWorkerRuntime.cs') | ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
    Add-Type -LiteralPath $paths -ReferencedAssemblies ($references | Select-Object -Unique)
    [Popcorn.ProbeWorkerRuntime]::Run()
} catch { [Console]::Error.WriteLine('Player probe failed.'); exit 2 }
