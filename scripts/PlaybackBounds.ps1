function New-PlaybackBoundsState {
    @{Key='';Since=0;LastAtMs=$null;Checked=0;Mismatch=0;Transitions=0;Unavailable=0;SamplingGaps=0;VerifiedSizes=@{};VerifiedPositions=@{};VerifiedTargets=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)}
}

function Update-PlaybackBoundsCheck {
    param([hashtable]$State,$Before,$Render,$After,[double]$AtMs)
    if($null -ne $State.LastAtMs -and ($AtMs-$State.LastAtMs -gt 500 -or $AtMs -lt $State.LastAtMs)){
        $State.Key=''; $State.SamplingGaps++
    }
    $State.LastAtMs=$AtMs
    foreach($bounds in @($Before,$After)){
        if($null -eq $bounds -or $bounds.Handle -eq 0 -or $bounds.Width -lt 100 -or $bounds.Height -lt 100 -or
            $bounds.WindowVisible -ne $true -or $bounds.WindowMinimized -ne $false){
            $State.Key=''; $State.Since=$AtMs; $State.Unavailable++
            return 'unavailable'
        }
    }
    $beforeKey=@($Before.Handle,$Before.X,$Before.Y,$Before.Width,$Before.Height,$Before.Title) -join '|'
    $key=@($After.Handle,$After.X,$After.Y,$After.Width,$After.Height,$After.Title) -join '|'
    # Bracket the render snapshot with independent live reads, not a delayed probe DTO.
    if(-not [StringComparer]::Ordinal.Equals($beforeKey,$key)){
        $State.Key=''; $State.Since=$AtMs; $State.Transitions++
        return 'transition'
    }
    if(-not [StringComparer]::Ordinal.Equals($key,$State.Key)){ $State.Key=$key; $State.Since=$AtMs }
    if(-not $Render.Visible){ $State.Key=''; $State.Since=$AtMs; return 'hidden' }
    foreach($value in @($Render.ViewportWidthPx,$Render.ViewportHeightPx,$Render.OverlayXpx,$Render.OverlayYpx)){
        if($null -eq $value -or -not [double]::IsFinite([double]$value)){
            $State.Key=''; $State.Since=$AtMs; $State.Unavailable++
            return 'unavailable'
        }
    }
    if($AtMs-$State.Since -gt 600){
        $State.Checked++
        if([Math]::Abs($Render.ViewportWidthPx-$Before.Width) -gt 2 -or [Math]::Abs($Render.ViewportHeightPx-$Before.Height) -gt 2 -or
            [Math]::Abs($Render.OverlayXpx-$Before.X) -gt 2 -or [Math]::Abs($Render.OverlayYpx-$Before.Y) -gt 2){
            $State.Mismatch++
            return 'mismatch'
        }
        $State.VerifiedSizes[(@($Before.Width,$Before.Height) -join '|')]=$true
        $State.VerifiedPositions[(@($Before.X,$Before.Y) -join '|')]=$true
        $identity=@($Before.Handle,$Before.Title) -join '|'
        if(-not $State.VerifiedTargets.ContainsKey($identity)){ $State.VerifiedTargets[$identity]=@{} }
        $extent=$State.VerifiedTargets[$identity]
        foreach($field in @('X','Y','Width','Height')){
            if(-not $extent.ContainsKey('Min'+$field)){ $extent['Min'+$field]=$Before.$field; $extent['Max'+$field]=$Before.$field }
            $extent['Min'+$field]=[Math]::Min($extent['Min'+$field],$Before.$field)
            $extent['Max'+$field]=[Math]::Max($extent['Max'+$field],$Before.$field)
        }
        return 'checked'
    }
    return 'settling'
}

function Test-PlaybackBoundsGate {
    param([hashtable]$State)
    # More than both endpoints' 2px tolerance is needed to prove actual following.
    $covered=@($State.VerifiedTargets.Values|Where-Object {
        ($_.MaxWidth-$_.MinWidth -gt 4 -or $_.MaxHeight-$_.MinHeight -gt 4) -and
        ($_.MaxX-$_.MinX -gt 4 -or $_.MaxY-$_.MinY -gt 4)
    }).Count -gt 0
    return $State.Checked -ge 10 -and $State.Mismatch -eq 0 -and $covered
}

function Convert-PlaybackBoundsEvidence {
    param($Bounds)
    if($null -eq $Bounds){ return $null }
    [pscustomobject]@{Handle=([IntPtr]$Bounds.Handle).ToInt64();Title=$Bounds.Title;X=$Bounds.X;Y=$Bounds.Y;Width=$Bounds.Width;Height=$Bounds.Height;WindowVisible=$Bounds.WindowVisible;WindowMinimized=$Bounds.WindowMinimized}
}
