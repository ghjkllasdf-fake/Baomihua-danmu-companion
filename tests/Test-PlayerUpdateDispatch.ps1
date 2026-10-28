#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Desktop.cs'))
if(-not $source.Contains('probe.SampleUpdated+=QueuePlayerUpdate;') -or -not $source.Contains('probe.SampleUpdated-=QueuePlayerUpdate;')){ throw 'The desktop must connect and detach actual probe notifications.' }
$methods=[regex]::Match($source,'(?s)    private void QueuePlayerUpdate\(\).*?(?=    private void UpdatePlayer\(\))')
if(-not $methods.Success){ throw 'No actual coalesced player-update dispatcher was found.' }
$template=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures\PlayerUpdateDispatchTests.cs.in'))
$folder=Join-Path $root ('.cache\player-update-dispatch-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$path=Join-Path $folder 'DispatchTests.cs'
[IO.File]::WriteAllText($path,$template.Replace('/*ACTUAL_METHODS*/',$methods.Value))
Add-Type -LiteralPath $path
[PlayerUpdateDispatchTests]::Run()
'PASS: actual desktop notification coalescing, latest-update drain, close fences and failed-post recovery; no window or player APIs.'
