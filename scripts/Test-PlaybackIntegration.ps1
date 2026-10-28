#requires -Version 7.6
param([ValidateSet('All','Snapshot')][string]$Scenario='All',[ValidateRange(5,600)][int]$Seconds=60,[switch]$Overlay)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'PlaybackBounds.ps1')
[void][IO.Directory]::CreateDirectory((Join-Path $root 'artifacts'))
. (Join-Path $root 'desktop\Host.ps1') -Check
[System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2) | Out-Null
if(-not [Popcorn.NativeWindows]::FindPlayer()){ throw 'BaoMiHua is not running. Open a video before the real-player integration check.' }
$probe=[Popcorn.PlayerProbe]::new($root)
$renderer=$null
if($Overlay){
    $renderer=[Popcorn.OverlayHost]::new()
    $data=[Popcorn.Comment[]]::new(50000)
    for($i=0;$i -lt $data.Length;$i++){ $data[$i]=[Popcorn.Comment]::new($i*100,1,16777215,('同步验证 '+$i)) }
    $renderer.SetComments($data)
}
$sync=[Popcorn.SyncClock]::new()
$samples=[Collections.Generic.List[object]]::new()
$seenPlaying=$false; $seenPaused=$false; $forward=$false; $backward=$false; $sizeChanged=$false
$last=$null; $watch=[Diagnostics.Stopwatch]::StartNew()
$lastMinimized=$false
$boundsState=New-PlaybackBoundsState
if($Scenario -eq 'All'){ Write-Output 'Play video, pause/resume, seek forward/backward, then move and resize/fullscreen during this observation. This test reads state only.' }
else { Write-Output 'Reading the current player state only. No playback controls will be changed.' }
try {
    while($watch.Elapsed.TotalSeconds -lt $Seconds){
        $sample=$probe.Latest
        $sync.Observe($sample.PositionMs,$sample.Playing,$sample.StampMs,[Popcorn.Monotonic]::Now,($sample.Title+'#'+$sample.EpisodeNumber),$sample.PositionResolutionMs)
        $visible=$null -ne $sample.Window -and [Popcorn.NativeWindows]::IsWindowVisible($sample.Window.Handle)
        $minimized=$null -ne $sample.Window -and [Popcorn.NativeWindows]::IsIconic($sample.Window.Handle)
        $foreground=$null -ne $sample.Window -and [Popcorn.NativeWindows]::GetForegroundWindow() -eq $sample.Window.Handle
        $render=$null; $boundsBefore=$null; $boundsAfter=$null; $boundsCheck=$null; $boundsReadStartMs=$null; $boundsReadEndMs=$null
        if($renderer){
            $target=if($sample.Title -and $sync.Mode -in @('automatic','manual')){ $sample.Window }else{ $null }
            $boundsReadStartMs=$watch.Elapsed.TotalMilliseconds
            $boundsBefore=if($target){ [Popcorn.NativeWindows]::Read($target.Handle) }else{ $null }
            $renderer.Update($target,$sync.At([Popcorn.Monotonic]::Now),$sync.Running,$sync.SeekRevision,($sync.Mode -eq 'automatic'),$sync.LastProofMs)
            $render=$renderer.Snapshot()
            $boundsAfter=if($target){ [Popcorn.NativeWindows]::Read($target.Handle) }else{ $null }
            $boundsReadEndMs=$watch.Elapsed.TotalMilliseconds
            $boundsCheck=Update-PlaybackBoundsCheck $boundsState $boundsBefore $render $boundsAfter $watch.Elapsed.TotalMilliseconds
        }
        $samples.Add([pscustomobject]@{AtMs=[Math]::Round($watch.Elapsed.TotalMilliseconds);Title=$sample.Title;EpisodeNumber=$sample.EpisodeNumber;WindowTitle=$sample.Window.Title;SampleStampMs=$sample.StampMs;PositionMs=$sample.PositionMs;DurationMs=$sample.DurationMs;Playing=$sample.Playing;Mode=$sync.Mode;SeekRevision=$sync.SeekRevision;Source=$sample.Source;Detail=$sample.Detail;Width=$sample.Window.Width;Height=$sample.Window.Height;WindowVisible=$visible;WindowMinimized=$minimized;IsForeground=$foreground;Render=$render;BoundsBefore=(Convert-PlaybackBoundsEvidence $boundsBefore);BoundsAfter=(Convert-PlaybackBoundsEvidence $boundsAfter);BoundsCheck=$boundsCheck;BoundsReadStartMs=$boundsReadStartMs;BoundsReadEndMs=$boundsReadEndMs})
        if($sample.Playing -eq $true){ $seenPlaying=$true }; if($sample.Playing -eq $false){ $seenPaused=$true }
        if($last -and $null -ne $sample.PositionMs -and $null -ne $last.PositionMs){
            $delta=$sample.PositionMs-$last.PositionMs
            $elapsed=$sample.StampMs-$last.StampMs
            if($delta-$elapsed -gt 1500){ $forward=$true }; if($delta -lt -500){ $backward=$true }
        }
        if($last -and $sample.Window -and $last.Window -and $sample.Window.Handle -eq $last.Window.Handle -and [StringComparer]::Ordinal.Equals($sample.Window.Title,$last.Window.Title) -and -not $minimized -and -not $lastMinimized -and ($sample.Window.Width -ne $last.Window.Width -or $sample.Window.Height -ne $last.Window.Height)){ $sizeChanged=$true }
        $last=$sample; $lastMinimized=$minimized; Start-Sleep -Milliseconds 200
    }
}finally{ $probe.Dispose(); if($renderer){ $renderer.Capture((Join-Path $root 'artifacts\overlay-player-frame.png')); $renderer.Dispose() } }
[void][IO.Directory]::CreateDirectory((Join-Path $root 'artifacts'))
$samples | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'artifacts\playback-observations.json')
$result=[pscustomobject]@{Playing=$seenPlaying;Paused=$seenPaused;ForwardSeek=$forward;BackwardSeek=$backward;WindowResize=$sizeChanged;AutomaticSamples=@($samples|Where-Object Mode -EQ automatic).Count}
if($Overlay){
    $streak=[Collections.Generic.List[object]]::new(); $best=@(); $bestSpan=-1; $priorKey=''
    foreach($row in @($samples.ToArray())+@($null)){
        $eligible=$null -ne $row -and $row.Playing -eq $false -and $row.Mode -eq 'automatic' -and $row.Render.Visible -and $row.Render.ActiveCount -gt 0 -and $row.Render.LayoutPending -eq $false
        $key=if($eligible){ "$($row.SeekRevision)|$($row.PositionMs)|$($row.Width)|$($row.Height)|$($row.Render.ViewportWidthPx)|$($row.Render.ViewportHeightPx)|$($row.Render.FontPx)" }else{ '' }
        if($streak.Count -gt 0 -and (-not $eligible -or $key -ne $priorKey -or $row.AtMs-$streak[-1].AtMs -gt 750)){
            $span=$streak[-1].AtMs-$streak[0].AtMs
            if($span -gt $bestSpan){ $best=$streak.ToArray(); $bestSpan=$span }
            $streak.Clear()
        }
        if($eligible){ $streak.Add($row); $priorKey=$key }
    }
    $frozenSeconds=0; $drift=$null; $xStates=0
    if($best.Count -gt 0){
        $first=$best[0].AtMs
        $steady=@($best|Where-Object {$_.AtMs -gt $first+600})
        if($steady.Count -gt 1){
            $frozenSeconds=($steady[-1].AtMs-$steady[0].AtMs)/1000
            $range=$steady.Render.PositionMs|Measure-Object -Minimum -Maximum
            $drift=$range.Maximum-$range.Minimum
            $xStates=@($steady|ForEach-Object {$_.Render.X -join ','}|Sort-Object -Unique).Count
        }
    }
    $result|Add-Member NoteProperty PausedFrozenSeconds $frozenSeconds
    $result|Add-Member NoteProperty PausedDriftMs $drift
    $result|Add-Member NoteProperty PausedXStates $xStates
    $result|Add-Member NoteProperty PausedPreparingSamples @($samples|Where-Object {$_.Playing -eq $false -and $_.Render.LayoutPending -eq $true}).Count
    $backgroundVisible=@($samples|Where-Object {$_.Render -and $_.Render.Visible -and $_.WindowVisible -and -not $_.WindowMinimized -and -not $_.IsForeground -and $_.Title}).Count -gt 0
    $minimizedSamples=@($samples|Where-Object {$_.WindowMinimized -and $_.Render -and -not $_.Render.Visible})
    $restored=$minimizedSamples.Count -gt 0 -and @($samples|Where-Object {$_.AtMs -gt $minimizedSamples[0].AtMs -and $_.Render.Visible -and -not $_.WindowMinimized}).Count -gt 0
    $result|Add-Member NoteProperty BackgroundVisibleObserved $backgroundVisible
    $result|Add-Member NoteProperty MinimizedHideObserved ($minimizedSamples.Count -gt 0)
    $result|Add-Member NoteProperty RestoreObserved $restored
    $result|Add-Member NoteProperty BoundsChecked $boundsState.Checked
    $result|Add-Member NoteProperty BoundsMismatch $boundsState.Mismatch
    $result|Add-Member NoteProperty BoundsBracketingTransitions $boundsState.Transitions
    $result|Add-Member NoteProperty BoundsUnavailable $boundsState.Unavailable
    $result|Add-Member NoteProperty BoundsSamplingGaps $boundsState.SamplingGaps
    $result|Add-Member NoteProperty VerifiedSizePhases $boundsState.VerifiedSizes.Count
    $result|Add-Member NoteProperty VerifiedPositionPhases $boundsState.VerifiedPositions.Count
    $result|Add-Member NoteProperty SameTargetGeometryVerified (Test-PlaybackBoundsGate $boundsState)
    $result|Add-Member NoteProperty NativeInputStylesVerified (@($samples|Where-Object {$_.Render.Visible -and (-not $_.Render.ClickThrough -or -not $_.Render.NoActivate)}).Count -eq 0)
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts\playback-result.json')
$result | Format-List
if($Overlay -and $Scenario -eq 'All'){
    if($frozenSeconds -lt 10 -or $drift -ne 0 -or $xStates -ne 1){ throw 'A continuously visible 10-second zero-drift paused overlay was not verified.' }
    if(-not ($backgroundVisible -and $minimizedSamples.Count -gt 0 -and $restored)){ throw 'Unfocused-visible/minimize/restore window policies were not all observed.' }
    if(-not (Test-PlaybackBoundsGate $boundsState) -or -not $result.NativeInputStylesVerified){ throw 'Physical overlay bounds across different sizes/positions or native input styles were not verified.' }
}
if($result.AutomaticSamples -eq 0){ throw 'No verified automatic synchronization samples were observed.' }
if($Scenario -eq 'All' -and -not ($seenPlaying -and $seenPaused -and $forward -and $backward -and $sizeChanged)){ throw 'Not all real-player scenarios were observed. See artifacts/playback-observations.json; this is not a passed integration gate.' }
