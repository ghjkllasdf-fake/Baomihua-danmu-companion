#requires -Version 7.6
param([string]$Assembly,[string]$Scenario)
$ErrorActionPreference='Stop'
try {
    Add-Type -LiteralPath $Assembly
    $announce=$Scenario.StartsWith('gate-')
    $null=[Popcorn.ProbeProcessTests]::OpenGate($announce).GetAwaiter().GetResult()
    if($announce){ $Scenario=$Scenario.Substring(5) }
    [Popcorn.ProbeProcessTests]::Emit($Scenario,$Assembly,$PSCommandPath)
} catch { [Console]::Error.WriteLine('Synthetic probe failed.'); exit 2 }
