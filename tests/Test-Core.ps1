#requires -Version 7.6
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot '..\desktop\native\Core.cs')
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$clock = [Popcorn.PlaybackClock]::new()
$clock.Set(1000, $true, 100)
Assert ($clock.At(600) -eq 1500) 'Playing time must advance with monotonic time.'
$clock.Set($clock.At(600), $false, 600)
Assert ($clock.At(10600) -eq 1500) 'Paused time must remain frozen.'
$clock.Set(500, $false, 10600)
Assert ($clock.At(11600) -eq 500) 'Seek while paused must stay paused at the new position.'
$clock.Set(500, $true, 11600)
Assert ($clock.At(12100) -eq 1000) 'Resume must advance from the seek position.'
$settings = [Popcorn.DanmakuSettings]::new()
Assert ($settings.AreaFraction -eq 0.5) 'Default region must be upper half.'
$settings.AreaFraction = 0.3
$rejected=$false
try { $settings.Validate() } catch { $rejected=$true }
Assert $rejected 'Unsupported region must be rejected.'
$settings.AreaFraction = 0.25
$settings.Validate()
Assert ([Popcorn.LayoutMath]::Scale(640,360,$true) -eq 0.5) 'Font must scale proportionally.'
Assert ([Popcorn.LayoutMath]::Scale(640,360,$false) -eq 1) 'Fixed-size option must disable scaling.'
Assert (-not [Popcorn.LayoutMath]::CanFollow(1880,100,200,240,1920,20)) 'New comment cannot enter before the previous right edge clears.'
Assert ([Popcorn.LayoutMath]::CanFollow(1000,100,200,200,1920,20)) 'Equal-speed separated comments should be safe.'
$motion=[Popcorn.Motion]::new(0,8000)
$before=$motion.Progress(2000)
$motion.Retime(2000,4000)
Assert ($motion.Progress(2000) -eq $before) 'Changing speed must preserve the current position.'
Assert ($motion.Progress(3000) -gt $before) 'New speed must affect subsequent movement.'
$comments=[Popcorn.Comment[]]@([Popcorn.Comment]::new(1000,1,16777215,'one'),[Popcorn.Comment]::new(2000,1,16777215,'two'),[Popcorn.Comment]::new(3000,5,16777215,'three'))
$queue=[Popcorn.CommentQueue]::new($comments)
Assert (-not $queue.HasDue(999) -and $queue.HasDue(1000) -and $queue.HasDue(1000)) 'Due-work checks must preserve the cursor and honor the exact next timestamp.'
$emptyQueue=[Popcorn.CommentQueue]::new([Popcorn.Comment[]]::new(0))
Assert (-not $emptyQueue.HasDue(100000)) 'An exhausted queue must not schedule empty frame work.'
$queue.Seek(2500,0)
Assert (-not $queue.HasDue(2999) -and $queue.HasDue(3000)) 'Forward seek must update due-work readiness.'
Assert ($queue.TakeDue(3100,1000).Count -eq 1) 'Forward seek must not replay skipped comments.'
Assert (-not $queue.HasDue(999999)) 'Consuming the last comment must clear due-work readiness.'
$queue.Seek(500,0)
Assert ($queue.TakeDue(1500,1000)[0].Text -eq 'one') 'Backward seek must restore earlier comments.'
$sync=[Popcorn.SyncClock]::new()
$sync.Observe(10000,$true,100,100,'episode-1')
Assert ($sync.Mode -eq 'automatic') 'Automatic mode requires a real position and state.'
$initialRevision=$sync.SeekRevision
$sync.Observe(2000,$true,300,300,'episode-1')
Assert ($sync.SeekRevision -gt $initialRevision) 'Backward seek must reset the queue revision.'
$sync.Observe(2000,$false,500,500,'episode-1')
Assert ($sync.At(10500) -eq 2000) 'Real pause must freeze.'
$sync.Observe(5000,$false,700,700,'episode-1')
Assert ($sync.At(1700) -eq 5000) 'Paused seek must remain paused.'
$sync.Observe(5000,$true,900,900,'episode-1')
$sync.Tick(2600)
Assert ($sync.Mode -eq 'stale' -and -not $sync.Running) 'Stale source must not keep claiming automatic sync.'
$sync.Observe($null,$null,2700,2700,'episode-1')
Assert ($sync.Mode -ne 'automatic') 'Unknown state is not automatic synchronization.'
$smallSeek=[Popcorn.SyncClock]::new()
$smallSeek.Observe(10000,$true,100,100,'episode-1')
$beforeRevision=$smallSeek.SeekRevision
$smallSeek.Observe(11000,$true,300,300,'episode-1')
Assert ($smallSeek.SeekRevision -eq $beforeRevision -and $smallSeek.At(300) -eq 11000) 'Small forward correction must update time without classifying a quantized boundary as seek.'
$smallSeek.Observe(12000,$false,1300,1300,'episode-1')
Assert ($smallSeek.SeekRevision -eq $beforeRevision -and $smallSeek.At(1800) -eq 12000) 'Normal pause across a whole-second boundary is not a seek.'
$lost=[Popcorn.SyncClock]::new()
$lost.Observe(10000,$true,100,100,'episode-1')
$lost.Observe($null,$null,10100,10100,'episode-1')
Assert ($lost.Mode -eq 'stale' -and $lost.At(10100) -eq 11500) 'Unknown samples after a long gap must respect the same 1500ms extrapolation cap.'
$smallSeek.Tick(3000)
$smallSeek.Manual(30000,$true,3000)
$smallSeek.Observe(12000,$true,500,3100,'episode-1')
Assert ($smallSeek.Mode -eq 'manual' -and $smallSeek.At(3100) -eq 30100) 'Delayed old sample must not overwrite explicit manual time.'
$invalidScale=$false
try { [void][Popcorn.LayoutMath]::Scale([double]::NaN,720,$true) }catch{ $invalidScale=$true }
Assert $invalidScale 'Invalid viewport dimensions must be rejected.'
foreach($playingAfterSeek in @($true,$false)){
    $quantized=[Popcorn.SyncClock]::new()
    $quantized.Observe(10000,$true,100,100,'episode-1')
    $quantized.Observe(10000,$true,300,300,'episode-1')
    $quantized.Observe(10000,$true,500,500,'episode-1')
    $priorRevision=$quantized.SeekRevision
    $quantized.Observe(12000,$playingAfterSeek,700,700,'episode-1')
    Assert ($quantized.SeekRevision -gt $priorRevision) 'A 2s advance within 200ms cannot be explained by ordinary whole-second quantization.'
}
$precise=[Popcorn.SyncClock]::new()
$precise.Observe(10000,$true,100,100,'episode-1',50)
$priorRevision=$precise.SeekRevision
$precise.Observe(10210,$true,300,300,'episode-1',50)
Assert ($precise.SeekRevision -eq $priorRevision) 'Precise normal playback with 10ms jitter must not be a seek.'
$precise.Observe(10800,$true,500,500,'episode-1',50)
Assert ($precise.SeekRevision -gt $priorRevision -and $precise.At(500) -eq 10800) 'Precise sub-second forward seek must relocate immediately.'
$priorRevision=$precise.SeekRevision
$precise.Observe(10600,$false,700,700,'episode-1',50)
Assert ($precise.SeekRevision -gt $priorRevision -and -not $precise.Running) 'Precise short backward seek with pause must relocate and freeze.'
foreach($transition in @(
    @{before=$false;after=$true;position=1500},
    @{before=$true;after=$false;position=1100}
)){
    $boundary=[Popcorn.SyncClock]::new()
    $boundary.Observe(1000,$transition.before,100,100,'episode-1',200)
    $priorRevision=$boundary.SeekRevision
    $boundary.Observe($transition.position,$transition.after,700,700,'episode-1',200)
    Assert ($boundary.SeekRevision -eq $priorRevision) 'Normal pause/resume anywhere within the sampled interval must not become a seek.'
    Assert ($boundary.Running -eq $transition.after -and $boundary.At(700) -eq $transition.position) 'State transitions must still apply the actual new position and running state immediately.'
}
foreach($beforePlaying in @($true,$false)){
    foreach($jump in @(0,2600)){
        $boundarySeek=[Popcorn.SyncClock]::new()
        $boundarySeek.Observe(1000,$beforePlaying,100,100,'episode-1',200)
        $priorRevision=$boundarySeek.SeekRevision
        $boundarySeek.Observe($jump,(-not $beforePlaying),700,700,'episode-1',200)
        Assert ($boundarySeek.SeekRevision -gt $priorRevision -and $boundarySeek.At(700) -eq $jump) 'A state change must not hide a forward/backward jump outside the sampled movement interval.'
    }
}
Assert ([Popcorn.MediaIdentity]::Matches('示例电视剧 第16集','示例电视剧 第16集',3440000,3440454)) 'Same title and compatible duration must bind, even while seek position differs from the lagging SMTC timeline.'
Assert (-not [Popcorn.MediaIdentity]::Matches('示例电视剧 第16集','示例电视剧 第17集',3440000,3440454)) 'A stale previous-episode session must not bind.'
Assert (-not [Popcorn.MediaIdentity]::Matches('same','same',3440000,3600000)) 'Different-duration media must not bind.'
Assert (-not [Popcorn.MediaIdentity]::Matches('','',3440000,3440000)) 'Empty media titles must not establish session identity.'
Assert (-not [Popcorn.MediaIdentity]::Matches('clip.mp4','clip.mp4',$null,3600000)) 'Missing UIA duration must not bind a potentially stale same-name media session.'
$published=[Popcorn.SyncClock]::new()
$published.Observe(10000,$true,100,100,'episode-1',200)
$revision=$published.SeekRevision
$published.Observe(10100,$true,300,300,'episode-1',200)
$published.Observe(10450,$true,500,500,'episode-1',200)
$published.Observe(10550,$true,700,700,'episode-1',200)
Assert ($published.SeekRevision -eq $revision) 'Observed player publication jitter must not repeatedly rebuild the comment queue.'
$mediaChange=[Popcorn.SyncClock]::new()
$mediaChange.Observe(100000,$true,100,100,'A',200)
$revision=$mediaChange.SeekRevision
$mediaChange.Observe($null,$null,300,300,'B',200)
Assert ($mediaChange.Mode -eq 'unavailable' -and -not $mediaChange.Running -and $mediaChange.At(300) -eq 0 -and $mediaChange.SeekRevision -gt $revision) 'A confirmed new media with unknown state must not inherit the previous media timeline.'
$mediaChange.Observe(5000,$false,500,500,'B',200)
Assert ($mediaChange.Mode -eq 'automatic' -and $mediaChange.At(1500) -eq 5000) 'The new media must resume from its own verified paused position.'
$manualMedia=[Popcorn.SyncClock]::new()
$manualMedia.Observe($null,$null,100,100,'A',200)
$manualMedia.Manual(30000,$true,200)
$manualMedia.Observe($null,$null,300,300,'A',200)
Assert ($manualMedia.Mode -eq 'manual' -and $manualMedia.At(300) -eq 30100) 'Repeated unverified samples for the same identity must preserve explicit manual time.'
$manualMedia.Observe($null,$null,500,500,'B',200)
Assert ($manualMedia.Mode -eq 'unavailable' -and -not $manualMedia.Running -and $manualMedia.At(500) -eq 0) 'A changed unverified identity must not inherit manually calibrated time from the previous media.'
$pendingMedia=[Popcorn.SyncClock]::new()
$pendingMedia.Observe($null,$null,100,100,'A',200)
$pendingMedia.Manual(30000,$false,300)
$pendingMedia.Observe(1000,$true,200,400,'A',200)
Assert ($pendingMedia.Mode -eq 'manual' -and $pendingMedia.At(400) -eq 30000) 'Same-media proof captured before a manual command must not overwrite that explicit calibration.'
$pendingMedia.Observe($null,$null,250,450,'B',200)
Assert ($pendingMedia.Mode -eq 'unavailable' -and $pendingMedia.At(450) -eq 0) 'A new probe identity published before manual calibration must still invalidate the previous media calibration.'
$agedChange=[Popcorn.SyncClock]::new()
$agedChange.Observe(800000,$true,1000,1000,'A#1',200)
$revision=$agedChange.SeekRevision
$agedChange.Observe($null,$null,1100,3000,'B#2',200)
Assert ($agedChange.Mode -eq 'unavailable' -and -not $agedChange.Running -and $agedChange.At(3000) -eq 0 -and $agedChange.SeekRevision -gt $revision) 'A newer identity on an aged sample must invalidate the old media clock, not retain its position.'
$agedChange.Observe(800000,$true,1000,3100,'A#1',200)
Assert ($agedChange.Mode -eq 'unavailable' -and $agedChange.At(3100) -eq 0) 'An out-of-order previous-media sample cannot restore its discarded clock.'
$agedChange.Observe(5000,$false,3300,3300,'B#2',200)
Assert ($agedChange.Mode -eq 'automatic' -and $agedChange.At(4300) -eq 5000) 'The changed media must recover only from its own fresh verified position.'
foreach($badStamp in @([double]::NaN,[double]::PositiveInfinity,-1,50000)){
    $protected=[Popcorn.SyncClock]::new()
    $protected.Observe(10000,$true,100,100,'A',200)
    $revision=$protected.SeekRevision
    $protected.Observe(800000,$true,$badStamp,300,'B',200)
    Assert ($protected.SeekRevision -eq $revision -and $protected.LastProofMs -eq 100 -and $protected.At(300) -eq 10200) 'Invalid or future sample timestamps must not replace the identity, position or ordering watermark.'
    $protected.Observe(12000,$false,400,400,'A',200)
    Assert ($protected.Mode -eq 'automatic' -and -not $protected.Running -and $protected.At(500) -eq 12000) 'A rejected future timestamp must not poison later valid playback evidence.'
}
$futureLost=[Popcorn.SyncClock]::new()
$futureLost.Observe(10000,$true,100,100,'A',200)
$futureLost.Observe(800000,$true,50000,5000,'B',200)
Assert ($futureLost.Mode -eq 'stale' -and -not $futureLost.Running -and $futureLost.At(5000) -eq 11500) 'Rejected future proof must still allow the old evidence to age out and freeze.'
Write-Output 'PASS: clock pause/resume/seek, timestamp admission, settings validation, proportional scaling, safe lanes, bidirectional queue seek'
