#requires -Version 7.6
param([ValidateSet('all','player-sync','danmaku-renderer')][string]$Module='all')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-Core.ps1')
if($LASTEXITCODE -ne 0){ throw 'Core regression failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-PlaybackBounds.ps1')
if($LASTEXITCODE -ne 0){ throw 'Playback geometry observation regression failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-PlaybackPause.ps1')
if($LASTEXITCODE -ne 0){ throw 'Actual pause/layout readiness regression failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-OverlayWindowSnapshot.ps1')
if($LASTEXITCODE -ne 0){ throw 'Overlay-window physical-origin observation regression failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-OverlayVisibility.ps1')
if($LASTEXITCODE -ne 0){ throw 'Background video overlay visibility regression failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-OverlayPresentation.ps1')
if($LASTEXITCODE -ne 0){ throw 'Overlay composition lifecycle regression failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-DwmOverlayInitialization.ps1')
if($LASTEXITCODE -ne 0){ throw 'Native DWM initialization regression failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-RenderWorkQueue.ps1')
if($LASTEXITCODE -ne 0){ throw 'Renderer post-composition work queue regression failed.' }
if($Module -in @('all','player-sync')){
    foreach($test in @('Test-ProbeJob.ps1','Test-ProbeProcess.ps1','Test-ProbeProcessRaces.ps1','Test-ProbeSupervisor.ps1','Test-ProbeOperation.ps1','Test-PlaybackEvents.ps1','Test-ProbeWorkerBoundary.ps1','Test-PlayerSamplerBoundary.ps1','Test-PlayerControlCache.ps1','Test-UiaPlayerControls.ps1','Test-ProbeCadence.ps1')){
        & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -MTA -File (Join-Path $root ('tests\'+$test))
        if($LASTEXITCODE -ne 0){ throw ('Owned probe CLI regression failed: '+$test) }
    }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-ProbeTransport.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Probe transport boundary regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-PlaybackSnapshot.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Playback snapshot contract regression failed.' }
}
if($Module -eq 'all'){
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-AppIcons.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Windows application/tray icon decoding regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-StartupRegistration.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Isolated startup shortcut regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-TrayLifecycle.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Tray and hidden desktop lifecycle regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-OverlayFocusRuntime.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Actual background video overlay runtime regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-AutomaticMatch.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Automatic media matching regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-PlayerUpdateDispatch.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Coalesced desktop player-update dispatch regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-WebViewRuntimeRecovery.ps1')
    if($LASTEXITCODE -ne 0){ throw 'WebView2 Runtime recovery regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-ApiTimeout.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Whole-request API timeout regression failed.' }
    foreach($test in @('Test-PackageArchive.ps1','Test-PackageVerification.ps1')){
        & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root ('tests\'+$test))
        if($LASTEXITCODE -ne 0){ throw ('Package integrity regression failed: '+$test) }
    }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-DownloadedDeployment.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Downloaded deployment launch/probe regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-SettingsLatencyEvidence.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Settings-latency evidence publication regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-SettingsLatencyOwnership.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Settings-latency host ownership regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-Preferences.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Preference compatibility regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-PreferenceReadBudget.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Preference bounded-read regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-DesktopState.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Desktop state boundary regression failed.' }
}elseif($Module -eq 'player-sync'){
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-DesktopState.ps1') -Group Snapshot
    if($LASTEXITCODE -ne 0){ throw 'Playback snapshot delivery regression failed.' }
}
if($Module -in @('all','danmaku-renderer')){
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-SeekRenderIsolation.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Seek render isolation regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-RightToLeftEntry.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Uniform right-to-left entry regression failed.' }
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-RendererResources.ps1')
    if($LASTEXITCODE -ne 0){ throw 'Offscreen renderer resource regression failed.' }
}else{
    & (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'desktop\Host.ps1') -Check
    if($LASTEXITCODE -ne 0){ throw 'Desktop native compilation failed.' }
}
Write-Output 'Native synthetic/logic regressions and compilation passed. Real-player integration and visual/performance checks are separate gates.'
