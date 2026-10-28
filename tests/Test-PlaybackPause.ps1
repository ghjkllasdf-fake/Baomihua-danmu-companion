#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'scripts\Test-PlaybackIntegration.ps1'))
$start=$source.IndexOf('$streak=[Collections.Generic.List[object]]::new()')
$end=$source.IndexOf('$result|Add-Member NoteProperty PausedFrozenSeconds',$start)
if($start -lt 0 -or $end -le $start){throw 'Actual pause-evidence seam changed.'}
$calculator=[scriptblock]::Create($source.Substring($start,$end-$start)+"`n[pscustomobject]@{Seconds=`$frozenSeconds;Drift=`$drift;XStates=`$xStates}")
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
function New-Rows {
    $rows=[Collections.Generic.List[object]]::new()
    for($index=0;$index -le 80;$index++){
        $rows.Add([pscustomobject]@{
            AtMs=$index*200;Playing=$false;Mode='automatic';SeekRevision=1;PositionMs=10000;Width=800;Height=450
            Render=[pscustomobject]@{
                Visible=$true;ActiveCount=[Math]::Min(10,$index+1);LayoutPending=($index -lt 10)
                PositionMs=10000;X=@(for($bullet=0;$bullet -lt [Math]::Min(10,$index+1);$bullet++){100+$bullet})
                ViewportWidthPx=800;ViewportHeightPx=450;FontPx=28
            }
        })
    }
    return ,$rows
}
$samples=New-Rows
$result=& $calculator
Assert ($result.Seconds -ge 10 -and $result.Drift -eq 0 -and $result.XStates -eq 1) 'Progressive initial preparation must finish before the unchanged ten-second freeze measurement begins.'
$samples=New-Rows
foreach($row in $samples|Where-Object AtMs -GE 8000){$row.Render.PositionMs=10025;$row.Render.X[0]=99}
$result=& $calculator
Assert ($result.Drift -eq 25 -and $result.XStates -eq 2) 'Real movement after layout completion must still fail zero-drift checks.'
$samples=[Collections.Generic.List[object]]::new([object[]]@((New-Rows)|Where-Object {$_.AtMs -lt 5000 -or $_.AtMs -gt 12000}))
$result=& $calculator
Assert ($result.Seconds -lt 10) 'An observation gap must not manufacture a continuous ten-second freeze.'
$samples=New-Rows
foreach($row in $samples){$row.Render.LayoutPending=$null}
$result=& $calculator
Assert ($result.Seconds -eq 0) 'Missing actual layout-readiness evidence must not be accepted as settled.'
'PASS: actual pause checker separates initial layout preparation from motion; ten-second, exact-zero drift, observation-gap and required-readiness gates remain enforced.'
