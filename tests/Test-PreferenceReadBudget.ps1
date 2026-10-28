#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Preferences.cs'))
$boundary='new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)'
if(($source.Split($boundary).Count-1) -ne 1){ throw 'Could not identify the actual preference stream dependency; update this harness boundary.' }
# Keep the actual loader algorithm; replace only its file input with a counted synthetic stream.
$source=$source.Replace('namespace Popcorn;','namespace Popcorn.PreferenceBudget;').Replace($boundary,'new BudgetStream()')
$fixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\PreferenceReadHarness.cs.in'))
$folder=Join-Path $root '.cache\tests'; [void][IO.Directory]::CreateDirectory($folder)
$path=Join-Path $folder 'PreferenceReadHarness.cs'
[IO.File]::WriteAllText($path,$source+$fixture)
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),$path)
foreach($chunk in @([int]::MaxValue,7)){
    [Popcorn.PreferenceBudget.BudgetStream]::BytesRead=0
    [Popcorn.PreferenceBudget.BudgetStream]::ReadCalls=0
    [Popcorn.PreferenceBudget.BudgetStream]::ChunkSize=$chunk
    $result=[Popcorn.PreferenceBudget.SettingsPreferences]::Load('synthetic-input')
    if([Popcorn.PreferenceBudget.BudgetStream]::BytesRead -ne 65537 -or $result.Notice.Length -eq 0 -or $result.Settings.FontSize -ne 28){ throw 'The actual preference loader did not stop at the byte budget plus one sentinel.' }
    if($chunk -eq 7 -and [Popcorn.PreferenceBudget.BudgetStream]::ReadCalls -le 1){ throw 'The short-read fixture did not exercise the read loop.' }
}
'PASS: the actual preference loader consumes only 65537 bytes of a 262144-byte synthetic stream, including short reads; no giant file or window was created.'
