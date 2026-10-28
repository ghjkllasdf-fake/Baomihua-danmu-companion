#requires -Version 7.6
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\PlaybackBounds.ps1')
function Assert($condition,[string]$message){ if(-not $condition){ throw $message } }
function Window($width=3440,$height=1440,$x=0,$handle=42){
    [pscustomobject]@{ Handle=$handle;Title='fixture';X=$x;Y=0;Width=$width;Height=$height;WindowVisible=$true;WindowMinimized=$false }
}
function Render($width=3440,$height=1440,$x=0){ [pscustomobject]@{Visible=$true;ViewportWidthPx=$width;ViewportHeightPx=$height;OverlayXpx=$x;OverlayYpx=0} }
function Tracker { New-PlaybackBoundsState }

$state=Tracker; $a=Window; $render=Render
Assert ((Update-PlaybackBoundsCheck $state $a $render $a 0) -eq 'settling') 'A fresh geometry must first settle.'
foreach($at in @(200,400)){ [void](Update-PlaybackBoundsCheck $state $a $render $a $at) }
Assert ((Update-PlaybackBoundsCheck $state $a $render $a 600) -eq 'settling') 'The original 600ms settling floor must remain.'
Assert ((Update-PlaybackBoundsCheck $state $a $render $a 601) -eq 'checked') 'Stable matching physical bounds were not counted.'
Assert ($state.Checked -eq 1 -and $state.Mismatch -eq 0) 'Matching bounds produced a false failure.'

# Reproduce the real fullscreen exit: the renderer sees new bounds before the probe does.
$b=Window 3380; $nextRender=Render 3380
Assert ((Update-PlaybackBoundsCheck $state $a $nextRender $b 800) -eq 'transition') 'A window change during the render snapshot is not a comparable observation.'
Assert ($state.Checked -eq 1 -and $state.Mismatch -eq 0 -and $state.Transitions -eq 1) 'A bracketed transition must be recorded, not silently counted as success or failure.'
Assert ((Update-PlaybackBoundsCheck $state $b $nextRender $b 1000) -eq 'settling') 'New bounds must restart settling.'
foreach($at in @(1200,1400)){ [void](Update-PlaybackBoundsCheck $state $b $nextRender $b $at) }
Assert ((Update-PlaybackBoundsCheck $state $b $nextRender $b 1601) -eq 'checked') 'Settled new bounds must be verified.'

$wrong=Render 3300
Assert ((Update-PlaybackBoundsCheck $state $b $wrong $b 1800) -eq 'mismatch') 'A persistent renderer size defect must still fail.'
Assert ($state.Checked -eq 3 -and $state.Mismatch -eq 1) 'Real mismatches must remain in the gate counters.'
$tolerance=Render 3378
Assert ((Update-PlaybackBoundsCheck $state $b $tolerance $b 2000) -eq 'checked') 'The existing two-pixel tolerance changed.'
$overTolerance=Render 3377
Assert ((Update-PlaybackBoundsCheck $state $b $overTolerance $b 2200) -eq 'mismatch') 'Three pixels must still fail.'

$moved=Window 3380 1440 100
Assert ((Update-PlaybackBoundsCheck $state $moved $nextRender $moved 2400) -eq 'settling') 'Moving without resizing must restart the geometry observation.'
$replaced=Window 3380 1440 100 43
Assert ((Update-PlaybackBoundsCheck $state $moved $nextRender $replaced 2600) -eq 'transition') 'Different target handles must not form one bounds observation.'
Assert ((Update-PlaybackBoundsCheck $state $null $nextRender $null 2800) -eq 'unavailable') 'Missing target geometry must not be compared.'
Assert ($state.Unavailable -eq 1) 'Unavailable geometry must be visible in evidence.'
$replaced.WindowMinimized=$true
Assert ((Update-PlaybackBoundsCheck $state $replaced $nextRender $replaced 3000) -eq 'unavailable') 'Minimized window geometry is not a visible overlay target.'
Assert ((Update-PlaybackBoundsCheck $state $b $nextRender $b 3200) -eq 'settling') 'An unavailable interval must reset settling.'
$hidden=Render 3380; $hidden.Visible=$false
Assert ((Update-PlaybackBoundsCheck $state $b $hidden $b 4000) -eq 'hidden') 'A hidden overlay must not count as a successful size check.'

$state=Tracker; $at=0
foreach($at in @(0,200,400,600,800)){ [void](Update-PlaybackBoundsCheck $state $a $render $a $at) }
Assert ((Update-PlaybackBoundsCheck $state $a $render $a 1401) -eq 'settling') 'A long observation gap cannot prove continuous geometry stability.'
Assert ($state.SamplingGaps -eq 1) 'Observation gaps must be retained in evidence.'
foreach($at in @(1600,1800,2000)){ [void](Update-PlaybackBoundsCheck $state $moved $nextRender $moved $at) }
Assert ((Update-PlaybackBoundsCheck $state $moved $nextRender $moved 2201) -eq 'mismatch') 'Correct dimensions at the old overlay screen position must fail.'
$correctPosition=Render 3380 1440 100
Assert ((Update-PlaybackBoundsCheck $state $moved $correctPosition $moved 2400) -eq 'checked') 'Actual overlay coordinates must match the stable target coordinates.'
$missingPosition=Render 3380; $missingPosition.OverlayXpx=$null
Assert ((Update-PlaybackBoundsCheck $state $moved $missingPosition $moved 2600) -eq 'unavailable') 'An unknown overlay position must not be interpreted as zero.'
foreach($invalid in @([double]::NaN,[double]::PositiveInfinity,[double]::NegativeInfinity)){
    $invalidRender=Render; $invalidRender.ViewportWidthPx=$invalid
    Assert ((Update-PlaybackBoundsCheck (Tracker) $a $invalidRender $a 0) -eq 'unavailable') 'Non-finite viewport evidence cannot become a successful comparison.'
}
$state=Tracker
foreach($at in @(0,200,400,600,800)){ [void](Update-PlaybackBoundsCheck $state $a $render $a $at) }
$hidden=Render; $hidden.Visible=$false
Assert ((Update-PlaybackBoundsCheck $state $a $hidden $a 1000) -eq 'hidden') 'Hidden overlay evidence must be explicit.'
Assert ((Update-PlaybackBoundsCheck $state $a (Render 3300) $a 1200) -eq 'settling') 'Visibility restoration must restart settling before comparing geometry.'
$evidence=Convert-PlaybackBoundsEvidence (Window 3440 1440 0 ([IntPtr]::new(4294967338)))
$roundTrip=$evidence|ConvertTo-Json|ConvertFrom-Json
Assert ($roundTrip.Handle -is [long] -and $roundTrip.Handle -eq 4294967338) 'Evidence HWND must remain a full-width JSON integer, not IntPtr object fields.'

$state=Tracker
foreach($at in 0..15){ [void](Update-PlaybackBoundsCheck $state $a $render $a ($at*200)) }
Assert ($state.Checked -ge 10 -and -not (Test-PlaybackBoundsGate $state)) 'Pre-resize checks alone cannot satisfy post-resize acceptance.'
$hidden=Render 3380; $hidden.Visible=$false
foreach($at in 16..31){ [void](Update-PlaybackBoundsCheck $state $moved $hidden $moved ($at*200)) }
Assert (-not (Test-PlaybackBoundsGate $state)) 'A resize followed only by hidden snapshots must not satisfy post-resize evidence.'
foreach($at in 32..47){ [void](Update-PlaybackBoundsCheck $state $moved $correctPosition $moved ($at*200)) }
Assert (Test-PlaybackBoundsGate $state) 'Different settled sizes and positions should satisfy the geometry coverage gate.'
$state.Mismatch=1
Assert (-not (Test-PlaybackBoundsGate $state)) 'Any measured mismatch must still fail the complete gate.'

$state=Tracker; $vertical=Window; $vertical.Y=100
foreach($at in 0..4){ [void](Update-PlaybackBoundsCheck $state $vertical $render $vertical ($at*200)) }
Assert ($state.Mismatch -gt 0) 'An overlay at the old vertical screen position must fail independently of X and size.'

foreach($case in @('other-handle','other-title','title-case-only','size-only','position-only','micro-change')){
    $state=Tracker
    foreach($at in 0..15){ [void](Update-PlaybackBoundsCheck $state $a $render $a ($at*200)) }
    $other=Window 3380 1440 100
    $otherRender=Render 3380 1440 100
    if($case -eq 'other-handle'){ $other.Handle=43 }
    if($case -eq 'other-title'){ $other.Title='other media' }
    if($case -eq 'title-case-only'){ $other.Title='FIXTURE' }
    if($case -eq 'size-only'){ $other.X=0; $otherRender.OverlayXpx=0 }
    if($case -eq 'position-only'){ $other.Width=3440; $otherRender.ViewportWidthPx=3440 }
    if($case -eq 'micro-change'){ $other.Width=3441; $other.X=1; $otherRender=Render }
    foreach($at in 16..31){ [void](Update-PlaybackBoundsCheck $state $other $otherRender $other ($at*200)) }
    Assert ($state.Mismatch -eq 0 -and -not (Test-PlaybackBoundsGate $state)) ('Incomplete same-target phase coverage must fail: '+$case)
}
$caseTitle=Window; $caseTitle.Title='FIXTURE'
Assert ((Update-PlaybackBoundsCheck (Tracker) $a $render $caseTitle 0) -eq 'transition') 'Title identity is ordinal even within one bracket.'
foreach($axis in @('OverlayXpx','OverlayYpx')){
    $state=Tracker; $tolerated=Render; $tolerated.$axis=2
    foreach($at in @(0,200,400,600)){ [void](Update-PlaybackBoundsCheck $state $a $tolerated $a $at) }
    Assert ((Update-PlaybackBoundsCheck $state $a $tolerated $a 800) -eq 'checked') ('Two pixels of coordinate tolerance must remain valid: '+$axis)
    $tolerated.$axis=3
    Assert ((Update-PlaybackBoundsCheck $state $a $tolerated $a 1000) -eq 'mismatch') ('Three pixels of coordinate error must fail: '+$axis)
}
$state=Tracker
foreach($at in @(0,200,400,600,800)){ [void](Update-PlaybackBoundsCheck $state $a $render $a $at) }
Assert ((Update-PlaybackBoundsCheck $state $a $render $a 1300) -eq 'checked') 'An exact 500ms observation gap remains admissible.'
Assert ((Update-PlaybackBoundsCheck $state $a $render $a 1801) -eq 'settling') 'A 501ms gap must restart evidence settling.'
foreach($delta in @(4,5)){
    $state=Tracker; $overlap=Render 3442 1440 2
    foreach($at in 0..15){ [void](Update-PlaybackBoundsCheck $state $a $overlap $a ($at*200)) }
    $other=Window (3440+$delta) 1440 $delta
    $followed=if($delta -eq 4){ $overlap }else{ Render (3440+$delta) 1440 $delta }
    foreach($at in 16..31){ [void](Update-PlaybackBoundsCheck $state $other $followed $other ($at*200)) }
    Assert ((Test-PlaybackBoundsGate $state) -eq ($delta -eq 5)) 'Geometry changes must exceed both endpoints of the two-pixel tolerance, not merely equal them.'
}
$state=Tracker
foreach($at in @(0,200,400,600,800)){ [void](Update-PlaybackBoundsCheck $state $a $render $a $at) }
Assert ((Update-PlaybackBoundsCheck $state $caseTitle $render $caseTitle 1000) -eq 'settling') 'A case-only identity change between observations must restart settling even without a geometry change.'
$state=Tracker
foreach($at in 0..15){ [void](Update-PlaybackBoundsCheck $state $a $render $a ($at*200)) }
$verticalOnly=Window; $verticalOnly.Y=100; $verticalOnly.Height=1540
$verticalRender=Render 3440 1540; $verticalRender.OverlayYpx=100
foreach($at in 16..31){ [void](Update-PlaybackBoundsCheck $state $verticalOnly $verticalRender $verticalOnly ($at*200)) }
Assert (Test-PlaybackBoundsGate $state) 'Height and Y-only changes must independently prove following without width or X changes.'
$composed=Window; $composed.Title=[string][char]0x00e9
$decomposed=Window; $decomposed.Title='e'+[char]0x0301
Assert ((Update-PlaybackBoundsCheck (Tracker) $composed $render $decomposed 0) -eq 'transition') 'Unicode canonical equivalence is not ordinal target identity within one bracket.'
$state=Tracker
foreach($at in @(0,200,400,600,800)){ [void](Update-PlaybackBoundsCheck $state $composed $render $composed $at) }
Assert ((Update-PlaybackBoundsCheck $state $decomposed $render $decomposed 1000) -eq 'settling') 'A Unicode-normalization change between observations must restart settling.'
$state=Tracker
foreach($at in 0..15){ [void](Update-PlaybackBoundsCheck $state $composed $render $composed ($at*200)) }
$decomposed.Width=3540; $decomposed.X=100
$unicodeRender=Render 3540 1440 100
foreach($at in 16..31){ [void](Update-PlaybackBoundsCheck $state $decomposed $unicodeRender $decomposed ($at*200)) }
Assert ($state.VerifiedTargets.Count -eq 2 -and -not (Test-PlaybackBoundsGate $state)) 'Unicode-normalized but ordinal-distinct targets cannot combine geometry phases.'

# Execute the real driver condition with plain objects, without loading its native APIs.
$driver=Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\Test-PlaybackIntegration.ps1'
$resizeLines=@([IO.File]::ReadAllLines($driver)|Where-Object {$_ -match '^\s*if\(\$last .+\{ \$sizeChanged=\$true \}$'})
Assert ($resizeLines.Count -eq 1) 'The real resize observation seam changed; refusing a substitute condition.'
$resizeStatement=[scriptblock]::Create($resizeLines[0])
function ResizeObserved($before,$after){
    $last=[pscustomobject]@{Window=$before}; $sample=[pscustomobject]@{Window=$after}
    $lastMinimized=$false; $minimized=$false; $sizeChanged=$false
    . $resizeStatement
    return $sizeChanged
}
$wider=Window 3540
Assert (ResizeObserved $a $wider) 'A real same-target dimension change must remain observable.'
Assert (-not (ResizeObserved $composed $decomposed)) 'The real resize condition must reject ordinal-distinct Unicode target titles.'
$wider.Title='FIXTURE'
Assert (-not (ResizeObserved $a $wider)) 'The real resize condition must reject case-distinct target titles.'
$wider.Title='fixture'; $wider.Handle=43
Assert (-not (ResizeObserved $a $wider)) 'Replacing the target window is not a resize of the original target.'
'PASS: bracketed live geometry, explicit transitions, original settling/tolerance and persistent-defect gate.'
