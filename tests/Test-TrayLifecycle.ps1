#requires -Version 7.6
param([string]$ApplicationRoot,[switch]$OpenImmediately)
$ErrorActionPreference='Stop'
$evidenceRoot=Split-Path $PSScriptRoot -Parent
$root=if($ApplicationRoot){[IO.Path]::GetFullPath($ApplicationRoot)}else{$evidenceRoot}
. (Join-Path $root 'desktop/Host.ps1') -Check
$paintReferences=$references+@('System.Private.Windows.GdiPlus.dll','System.Private.Windows.Core.dll'|ForEach-Object {Join-Path $PSHOME $_})
Add-Type -Path (Join-Path $PSScriptRoot 'fixtures/TrayContentProbe.cs') -ReferencedAssemblies ($paintReferences|Select-Object -Unique)
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$trayType=[Popcorn.DesktopWindow].Assembly.GetType('Popcorn.DesktopTray')
$tooltip=$trayType.GetMethod('Tooltip',$flags)
$text=[string]$tooltip.Invoke($null,@('示例电视剧',3,'来源甲',[int]12345))
Assert ($text.Contains('示例电视剧') -and $text.Contains('第3集') -and $text.Contains('来源甲') -and $text.Contains('12345 条')) 'Tooltip must include the episode, actual source label and comment count.'
$text=[string]$tooltip.Invoke($null,@(('剧'*600),[int]::MaxValue,('源'*600),[int]::MaxValue))
Assert ($text.Length -le 127 -and $text.Contains('2147483647 条') -and $text.Contains('第2147483647集')) 'Long titles must preserve episode/count within the Windows tooltip limit.'
$text=[string]$tooltip.Invoke($null,@('',0,'旧来源',12345))
Assert ($text.Contains('等待爆米花播放') -and $text.Contains('0 条') -and -not $text.Contains('旧来源')) 'Unknown playback must not show a stale source or count.'
$text=[string]$tooltip.Invoke($null,@("示例`r`n剧",1,"来源`n标签",12))
Assert (($text.ToCharArray()|Where-Object {$_ -eq "`n"}|Measure-Object).Count -eq 2) 'External labels must not inject extra tooltip rows.'
$window=[Popcorn.DesktopWindow]::new($root,'',('tray-test-'+[Guid]::NewGuid().ToString('N')))
$controllerField=$window.GetType().GetField('controller',[Reflection.BindingFlags]'Instance,NonPublic')
$script:trayTestPhase=0
$script:trayTestFailure=$null
$script:trayPaintTask=$null
$paintFolder=Join-Path $root ('.cache/tray-paint-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($paintFolder)
$watch=[Diagnostics.Stopwatch]::StartNew()
$timer=[Windows.Forms.Timer]::new()
$timer.Interval=200
$timer.add_Tick({
    try {
        if($watch.Elapsed.TotalSeconds -gt 40){throw 'Hidden startup handshake timed out.'}
        if(-not $window.Ready){return}
        switch($script:trayTestPhase){
            0 {
                if($OpenImmediately){Assert $window.Visible 'An immediate tray open must remain visible during initialization.'}
                else {Assert (-not $window.Visible -and -not $window.IsDisposed) 'Startup must remain hidden while completing the real React handshake.'}
                $tray=$window.GetType().GetField('tray',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($window)
                $icon=$tray.GetType().GetField('icon',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($tray)
                $script:trayTestIcon=$icon
                Assert $icon.Visible 'The tray icon must be visible from startup, without waiting for failure.'
                $window.OpenWorkbench();$script:trayTestPhase=1
            }
            1 {
                Assert ($window.Visible -and [Popcorn.NativeWindows]::IsWindowVisible($window.Handle)) 'The tray open action must show a real visible workbench.'
                Assert ($controllerField.GetValue($window).IsVisible) 'The actual WebView controller must become visible after opening from the tray; a hidden loaded page is a white screen.'
                if($null -eq $script:trayPaintTask){$script:trayPaintTask=[TrayContentProbe]::CaptureAsync($window,(Join-Path $paintFolder 'first-open.png'));return}
                if(-not $script:trayPaintTask.IsCompleted){return}
                $script:trayFirstPaint=$script:trayPaintTask.GetAwaiter().GetResult()
                $window.Close();$script:trayTestPhase=2
            }
            2 {
                Assert (-not $window.Visible -and -not $window.IsDisposed) 'Window close must hide the workbench without exiting.'
                Assert (-not $controllerField.GetValue($window).IsVisible) 'Closing to the tray must hide the browser controller.'
                $script:trayPaintTask=$null;$window.OpenWorkbench();$script:trayTestPhase=3
            }
            3 {
                Assert $window.Visible 'A hidden workbench must reopen.'
                Assert ($controllerField.GetValue($window).IsVisible) 'Reopening must show the browser content again.'
                if($null -eq $script:trayPaintTask){$script:trayPaintTask=[TrayContentProbe]::CaptureAsync($window,(Join-Path $paintFolder 'reopen.png'));return}
                if(-not $script:trayPaintTask.IsCompleted){return}
                $script:trayReopenPaint=$script:trayPaintTask.GetAwaiter().GetResult()
                $window.WindowState=[Windows.Forms.FormWindowState]::Minimized;$script:trayTestPhase=4
            }
            4 {
                Assert (-not $controllerField.GetValue($window).IsVisible) 'Minimizing must hide the browser controller.'
                $window.OpenWorkbench();$script:trayTestPhase=5
            }
            5 {
                Assert ($controllerField.GetValue($window).IsVisible) 'Restoring from minimized must display browser content.'
                $timer.Stop();$window.RequestExit();$script:trayTestPhase=6
            }
        }
    } catch {$script:trayTestFailure=$_;$timer.Stop();$window.RequestExit()}
})
try {
    if($OpenImmediately){$window.OpenWorkbench()}
    $timer.Start()
    [Windows.Forms.Application]::Run($window)
    if($script:trayTestFailure){throw $script:trayTestFailure}
    Assert ($script:trayTestPhase -eq 6 -and $null -ne $script:trayTestIcon -and -not $script:trayTestIcon.Visible) 'Explicit exit must close the workbench and remove its tray icon.'
} finally {$timer.Dispose();$window.Dispose()}
$unstarted=[Popcorn.DesktopWindow]::new($root,'',('tray-test-'+[Guid]::NewGuid().ToString('N')))
$unstartedTray=$unstarted.GetType().GetField('tray',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($unstarted)
$unstartedIcon=$unstartedTray.GetType().GetField('icon',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($unstartedTray)
$unstarted.Dispose()
Assert (-not $unstartedIcon.Visible) 'Disposing before the message loop starts must not leave a ghost tray icon.'
[ordered]@{first=$script:trayFirstPaint;reopen=$script:trayReopenPaint;directory=$paintFolder;applicationRoot=$root}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $evidenceRoot 'artifacts/tray-paint-evidence.json')
'PASS: actual controller visibility, visible DOM and nonblank browser frames on first open/reopen, minimize/restore, tray lifetime and explicit exit.'
