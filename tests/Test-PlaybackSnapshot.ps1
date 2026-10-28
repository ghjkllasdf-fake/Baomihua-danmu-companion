#requires -Version 7.6
param([switch]$WriteContractFixture)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\PlaybackModels.cs'))
function Assert($condition,[string]$message){ if(-not $condition){ throw $message } }
$clock=[Popcorn.SyncClock]::new()
$window=[Popcorn.VideoWindow]::new([IntPtr]1,'movie.mkv',-1920,-120,1920,1080)
$sample=[Popcorn.PlayerSample]::new($window,'movie',0,10000,$true,1000,'client fallback','uia-seekbar-ms+smtc',200,60000)
$clock.Observe(10000,$true,1000,1000,'movie',200)
$snapshot=[Popcorn.PlaybackSnapshot]::Capture($sample,$clock,1200,5,2)
Assert ($snapshot.Sequence -eq 5 -and $snapshot.MediaRevision -eq 2 -and $snapshot.SeekRevision -eq $clock.SeekRevision) 'The immutable snapshot must retain every authoritative revision.'
Assert ($snapshot.State -eq 'playing' -and $snapshot.PositionMs -eq 10200) 'Verified playback must extrapolate only with monotonic time.'
Assert ($null -eq $snapshot.EpisodeNumber -and $snapshot.DurationMs -eq 60000) 'Movies have no invented episode number, but retain their real duration.'
Assert ($snapshot.VideoBoundsPx.X -eq -1920 -and $snapshot.VideoBoundsPx.Y -eq -120 -and $snapshot.VideoBoundsPx.Width -eq 1920) 'Physical client coordinates, including negative origins, must be preserved.'
Assert ($snapshot.VideoBoundsSource -eq 'window-client' -and $null -eq $snapshot.DpiScale -and $null -eq $snapshot.WindowVisible) 'Unverified DPI/visibility cannot become guessed 100-percent/visible defaults.'
$clock.Observe(10200,$false,1200,1200,'movie',200)
$paused=[Popcorn.PlaybackSnapshot]::Capture($sample,$clock,1500,6,2)
Assert ($paused.State -eq 'paused' -and $paused.PositionMs -eq 10200) 'Confirmed pause must preserve position.'
$clock.Tick(2800)
$stale=[Popcorn.PlaybackSnapshot]::Capture($sample,$clock,2800,7,2)
$later=[Popcorn.PlaybackSnapshot]::Capture($sample,$clock,100000,8,2)
Assert ($stale.SyncMode -eq 'stale' -and $stale.State -eq 'unknown' -and $later.PositionMs -eq $stale.PositionMs) 'Loss of proof must remain unknown and must stop extrapolating.'
$manualClock=[Popcorn.SyncClock]::new(); $manualClock.Manual(30000,$true,1000)
$missing=[Popcorn.PlayerSample]::new($null,'',0,$null,$null,1000)
$manual=[Popcorn.PlaybackSnapshot]::Capture($missing,$manualClock,1200,1,0)
Assert ($manual.SyncMode -eq 'manual' -and $manual.State -eq 'playing' -and $manual.PositionMs -eq 30200 -and $null -eq $manual.VideoBoundsPx) 'Explicit manual time must remain visibly manual, without invented window bounds.'
$unknownClock=[Popcorn.SyncClock]::new()
$unknown=[Popcorn.PlaybackSnapshot]::Capture($missing,$unknownClock,1200,1,0)
Assert ($unknown.State -eq 'unavailable' -and $null -eq $unknown.PositionMs -and $unknown.VideoBoundsSource -eq 'unavailable') 'Missing players must not report position zero or client bounds.'
$invalidWindow=[Popcorn.VideoWindow]::new([IntPtr]1,'movie',0,0,0,0)
$invalid=[Popcorn.PlayerSample]::new($invalidWindow,'movie',0,[double]::NaN,$null,[double]::NaN,'invalid fixture','fixture',[double]::NaN,[double]::PositiveInfinity)
$safe=[Popcorn.PlaybackSnapshot]::Capture($invalid,$unknownClock,1200,1,0)
Assert ($null -eq $safe.PositionMs -and $null -eq $safe.DurationMs -and $null -eq $safe.SampleMonotonicMs -and $null -eq $safe.PositionResolutionMs -and $null -eq $safe.VideoBoundsPx) 'Invalid numeric values must remain null and JSON-safe, not become fabricated measurements.'
$json=[System.Text.Json.JsonSerializer]::Serialize($safe,[Popcorn.PlaybackSnapshot],[System.Text.Json.JsonSerializerOptions]::new())
Assert ($json -notmatch 'NaN|Infinity') 'Snapshots must serialize with default strict numeric handling.'
$transitionClock=[Popcorn.SyncClock]::new()
$transitionClock.Observe(800000,$true,1000,1000,'A#1',200)
$transitionClock.Observe($null,$null,1100,3000,'B#2',200)
$changedSample=[Popcorn.PlayerSample]::new($window,'B',2,$null,$null,1100,'aged new-media fixture','fixture',200,60000)
$changed=[Popcorn.PlaybackSnapshot]::Capture($changedSample,$transitionClock,3000,9,3)
Assert ($changed.Title -eq 'B' -and $changed.EpisodeNumber -eq 2 -and $null -eq $changed.PositionMs -and $changed.SyncMode -eq 'unavailable') 'An aged new-media snapshot cannot publish the previous media position under the new title.'
$pendingClock=[Popcorn.SyncClock]::new()
$pendingClock.Observe($null,$null,100,100,'A#1',200)
$pendingClock.Manual(30000,$false,300)
$pendingClock.Observe($null,$null,200,400,'B#2',200)
$pendingSample=[Popcorn.PlayerSample]::new($window,'B',2,$null,$null,200,'published before manual fixture','fixture',200,60000)
$pending=[Popcorn.PlaybackSnapshot]::Capture($pendingSample,$pendingClock,400,10,3)
Assert ($pending.SyncMode -eq 'unavailable' -and $null -eq $pending.PositionMs) 'The manual-command watermark cannot suppress a newer probe identity and mix media in the snapshot.'
$epochClock=[Popcorn.SyncClock]::new()
$epochClock.Observe(800000,$true,1000,1000,'A#1',200)
$epochClock.BeginMediaEpoch(1100)
$epochClock.Observe(5000,$false,950,1100,'B#2',200)
$epochSample=[Popcorn.PlayerSample]::new($window,'B',2,5000,$false,950,'new epoch older proof','smtc',200,60000)
$epoch=[Popcorn.PlaybackSnapshot]::Capture($epochSample,$epochClock,1100,11,3)
Assert ($epoch.SyncMode -eq 'automatic' -and $epoch.State -eq 'paused' -and $epoch.PositionMs -eq 5000) 'A newly identified media epoch must compare only its own timing proofs.'
if($WriteContractFixture){
    $folder=Join-Path $root '.cache\tests'
    [void][IO.Directory]::CreateDirectory($folder)
    $options=[System.Text.Json.JsonSerializerOptions]::new()
    $options.PropertyNamingPolicy=[System.Text.Json.JsonNamingPolicy]::CamelCase
    $snapshots=[Popcorn.PlaybackSnapshot[]]@($snapshot,$paused,$stale,$manual,$unknown,$safe,$changed,$pending,$epoch)
    $fixture=[System.Text.Json.JsonSerializer]::Serialize($snapshots,$snapshots.GetType(),$options)
    [IO.File]::WriteAllText((Join-Path $folder 'native-playback-snapshots.json'),$fixture)
}
'PASS: immutable playback snapshot revisions, clock modes, nullable metadata, negative client coordinates and JSON safety (no native window calls).'
