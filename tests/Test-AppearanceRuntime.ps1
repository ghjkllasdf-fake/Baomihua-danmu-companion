#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop/Host.ps1') -Check
[void][System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2)
[System.Windows.Forms.Application]::EnableVisualStyles()
$drawing=$references+@('System.Private.Windows.GdiPlus.dll','System.Private.Windows.Core.dll'|ForEach-Object {Join-Path $PSHOME $_})
Add-Type -Path (Join-Path $PSScriptRoot 'fixtures/SkinRuntimeProbe.cs') -ReferencedAssemblies (($drawing+[Popcorn.DesktopWindow].Assembly.Location)|Where-Object {$_}|Select-Object -Unique)
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$policy=[Popcorn.DesktopWindow].Assembly.GetType('Popcorn.WindowMaterial').GetMethod('WantsAcrylic',[Reflection.BindingFlags]'Static,NonPublic')
Assert ($policy.Invoke($null,@([Popcorn.AppearanceSettings]::new('glass',$false),$false,22621))) 'Glass must request system material on a supported platform.'
Assert (-not $policy.Invoke($null,@([Popcorn.AppearanceSettings]::new('glass',$true),$false,22621))) 'Reduced transparency must suppress system material.'
Assert (-not $policy.Invoke($null,@([Popcorn.AppearanceSettings]::new('winui',$false),$true,22621))) 'High contrast must suppress system material.'
Assert (-not $policy.Invoke($null,@([Popcorn.AppearanceSettings]::new('winui',$false),$false,19045))) 'Older Windows builds must use the solid fallback.'
$folder=Join-Path $root ('.cache/skin-runtime-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$application=Join-Path $folder 'application'
[void][IO.Directory]::CreateDirectory((Join-Path $application 'ui'))
Copy-Item -LiteralPath (Join-Path $root 'ui/dist') -Destination (Join-Path $application 'ui/dist') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'assets') -Destination (Join-Path $application 'assets') -Recurse
Copy-Item -LiteralPath (Join-Path $root '.deps/webview2/runtimes/win-x64/native/WebView2Loader.dll') -Destination (Join-Path $application 'WebView2Loader.dll')
$profile='skin-test'
$window=[Popcorn.DesktopWindow]::new($application,'',$profile,$folder)
$window.GetType().GetProperty('SuppressErrorDialogs',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($window,$true)
$path=Join-Path $folder "$profile/appearance.json"
$script:skinTask=$null;$script:skinFailure=$null;$script:skinEvidence=$null
$watch=[Diagnostics.Stopwatch]::StartNew();$timer=[Windows.Forms.Timer]::new();$timer.Interval=100
$timer.add_Tick({
    try{
        if($watch.Elapsed.TotalSeconds -gt 65){throw 'Owned skin runtime deadline exceeded.'}
        if(-not $window.Ready -or -not $window.Visible){return}
        if($null -eq $script:skinTask){$script:skinTask=[SkinRuntimeProbe]::RunAsync($window,$folder,$path);return}
        if(-not $script:skinTask.IsCompleted){return}
        $script:skinEvidence=$script:skinTask.GetAwaiter().GetResult();$timer.Stop();$window.RequestExit()
    }catch{$script:skinFailure=$_;$timer.Stop();$window.RequestExit()}
})
try{$window.OpenWorkbench();$timer.Start();[Windows.Forms.Application]::Run($window)}finally{$timer.Dispose();$window.Dispose()}
if($script:skinFailure){throw $script:skinFailure}
if(-not $window.Ready){throw ('Owned native initialization failed: '+$window.Failure)}
Assert ($null -ne $script:skinEvidence) 'No native skin evidence was produced.'
$restored=[Popcorn.DesktopWindow]::new($application,'',$profile,$folder)
try{Assert ($restored.Appearance.Skin -ceq 'fluent' -and -not $restored.Appearance.ReduceTransparency) 'A fresh native host must restore the confirmed preference.'}finally{$restored.Dispose()}
Assert (-not [IO.File]::Exists((Join-Path $folder "$profile/preferences.json"))) 'Skin changes must not create or alter display preferences.'
Assert (-not [IO.File]::Exists((Join-Path $folder "$profile/api.dat"))) 'Skin changes must not create API credentials.'
[ordered]@{result='PASS_LOGIC';desktopVisual=$(if($script:skinEvidence.DesktopVisualVerified){'PASS'}else{'NOT_VERIFIED_WINDOW_NOT_EXPOSED'});evidence=$script:skinEvidence;directory=$folder;profile=$profile;osBuild=[Environment]::OSVersion.Version.Build;playbackMetadataMasked=$true}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'artifacts/skin-native-runtime.json')
'PASS: actual React/native skin synchronization, atomic failure preservation, strict host payloads, persisted restore, tray reopen and solid-fallback configuration.'
if(-not $script:skinEvidence.DesktopVisualVerified){'NOT VERIFIED: this session could not expose owned windows for desktop material capture; no other-app screenshot was retained.'}
