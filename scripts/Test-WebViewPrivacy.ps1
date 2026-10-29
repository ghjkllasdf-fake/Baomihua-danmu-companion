#requires -Version 7.6
param(
    [ValidateSet('Both','Verify')][string]$Phase='Both',
    [string]$Profile=''
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(-not $Profile){ $Profile='privacy-'+[Guid]::NewGuid().ToString('N').Substring(0,12) }
if($Profile -notmatch '^privacy-[a-f0-9]{12}$'){ throw 'Invalid isolated privacy profile.' }
if($Phase -eq 'Both'){
    $run=Join-Path $root ('.cache\privacy-check-'+[Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($run)
    foreach($step in @('Verify')){
        $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root '.deps\pwsh\pwsh.exe'))
        $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
        $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true; $info.WorkingDirectory=$root
        foreach($argument in @('-NoProfile','-ExecutionPolicy','Bypass','-STA','-File',$PSCommandPath,'-Phase',$step,'-Profile',$Profile)){ $info.ArgumentList.Add($argument) }
        $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
        try {
            if(-not $process.Start()){ throw 'Owned privacy-check process did not start.' }
            [pscustomobject]@{pid=$process.Id;startTicks=$process.StartTime.ToUniversalTime().Ticks;phase=$step;profile=$Profile}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run ($step+'-owner.json'))
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            $forced=$false
            if(-not $process.WaitForExit(30000)){
                $forced=$true; $process.Kill()
                if(-not $process.WaitForExit(5000)){ throw 'Owned privacy-check process exit is unconfirmed.' }
            }
            $output=$stdout.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
            $errorOutput=$stderr.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $run ($step+'.out')),$output)
            [IO.File]::WriteAllText((Join-Path $run ($step+'.err')),$errorOutput)
            if($forced -or $process.ExitCode -ne 0){ throw ('Hidden privacy '+$step+' failed; inspect '+$run) }
        } finally { $process.Dispose() }
    }
    'PASS: actual hidden WebView2 startup disables form/password saving; the actual native policy resets both enabled switches. No desktop input or API credentials.'
    'Retained privacy evidence: '+$run
    return
}
. (Join-Path $root 'desktop\Host.ps1') -Check
[System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2) | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()
$data=Join-Path $root ('.cache\'+$Profile)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Popcorn.DesktopWindow]::new($root,'',$Profile)
    $created=$window.Handle
    try {
        $initialize=$window.GetType().GetMethod('InitializeAsync',$flags)
        $initializer=$initialize.CreateDelegate([Func[string,Threading.Tasks.Task]],$window)
        $task=$initializer.Invoke($data)
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not $task.IsCompleted -and $watch.ElapsedMilliseconds -lt 15000){
            [System.Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 10
        }
        if(-not $task.IsCompleted){ throw 'Hidden WebView2 initialization did not complete within 15 seconds.' }
        $null=$task.GetAwaiter().GetResult()
        if($window.Visible){ throw 'The privacy fixture must not show or focus a desktop window.' }
        $core=$window.GetType().GetField('core',$flags).GetValue($window)
        if($null -eq $core){ throw ('No actual CoreWebView2 was created: '+$window.Failure) }
        if($core.Settings.IsGeneralAutofillEnabled -or $core.Settings.IsPasswordAutosaveEnabled){
            throw 'The actual WebView2 profile permits form/password saving.'
        }
        # Exercise the actual policy against enabled live settings, without assuming persistence across browser processes.
        $core.Settings.IsGeneralAutofillEnabled=$true
        $core.Settings.IsPasswordAutosaveEnabled=$true
        if(-not $core.Profile.IsGeneralAutofillEnabled -or -not $core.Profile.IsPasswordAutosaveEnabled){ throw 'Owned-profile seed did not apply.' }
        $policyMethod=$window.GetType().GetMethod('ConfigureBrowserSettings',[Reflection.BindingFlags]'Static,NonPublic')
        $policy=$policyMethod.CreateDelegate([Action[Microsoft.Web.WebView2.Core.CoreWebView2Settings]])
        $policy.Invoke($core.Settings)
        if($core.Settings.IsGeneralAutofillEnabled -or $core.Settings.IsPasswordAutosaveEnabled){
            throw 'The actual WebView2 profile permits form/password saving.'
        }
    } finally {
        $window.RequestExit()
        $window.Dispose()
        [System.Windows.Forms.Application]::DoEvents()
    }
'PASS: hidden privacy phase '+$Phase
