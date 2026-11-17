#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\PlayerSampler.cs'))
$boundary='NativeWindows.FindPlayer(media.PreferredTitle)'
if(($source.Split($boundary).Count-1) -ne 1){ throw 'Sampler lookup boundary changed; refusing to run player APIs.' }
$accessBoundary='PlayerPrivilege.IsBlocked(window)'
if(($source.Split($accessBoundary).Count-1) -ne 1){throw 'Player privilege boundary changed; refusing real process inspection.'}
$folder=Join-Path $root ('.cache\sampler-boundary-'+[Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($folder)
$sampler=Join-Path $folder 'PlayerSampler.cs'
[IO.File]::WriteAllText($sampler,$source.Replace($boundary,'PlayerSamplerBoundaryTests.FindPlayer()').Replace($accessBoundary,'PlayerSamplerBoundaryTests.AccessBlocked(window)'))
$season=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\LibrarySeasonReader.cs'))
$seams=@{
    'NativeWindows.IsPlayerProcess(window)'='PlayerSamplerBoundaryTests.IsPlayerProcess(window)'
    'NativeWindows.FindLibraries(window)'='PlayerSamplerBoundaryTests.FindLibraries(window)'
    'CachedSeasonReader.Read(playing,duration,out bool conflict)'='PlayerSamplerBoundaryTests.ReadCachedSeason(playing,duration,out bool conflict)'
}
foreach($entry in $seams.GetEnumerator()){
    if(($season.Split($entry.Key).Count-1) -ne 1){throw 'Season boundary changed; refusing real player/cache access.'}
    $season=$season.Replace($entry.Key,$entry.Value)
}
$seasonCopy=Join-Path $folder 'LibrarySeasonReader.cs'
[IO.File]::WriteAllText($seasonCopy,$season)
$sdk=Join-Path $root '.deps\windows-sdk\lib\net8.0'
foreach($name in @('WindowsBase','UIAutomationClient','UIAutomationTypes')){ Add-Type -AssemblyName $name }
Add-Type -LiteralPath (Join-Path $sdk 'WinRT.Runtime.dll')
Add-Type -LiteralPath (Join-Path $sdk 'Microsoft.Windows.SDK.NET.dll')
$references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
$references+=@(Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll'|Where-Object {$_.Name -match '^(WindowsBase|UIAutomation|System\.Windows)'}|ForEach-Object FullName)
$paths=@('Core.cs','PlaybackModels.cs','SeriesIdentity.cs','CachedSeasonReader.cs','ProbeTransport.cs','ProbeOperation.cs','PlaybackEventSignal.cs','MediaSession.cs','Windows.cs','PlayerControlCache.cs','UiaPlayerControls.cs')|ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
Add-Type -LiteralPath @($paths+$sampler+$seasonCopy+(Join-Path $PSScriptRoot 'fixtures\PlayerSamplerBoundaryTests.cs')) -ReferencedAssemblies ($references|Select-Object -Unique)
[Popcorn.PlayerSamplerBoundaryTests]::Run()
'PASS: actual sampler/SMTC/season paths with synthetic window identity, isolated cache and library boundary; no real player APIs or windows.'
