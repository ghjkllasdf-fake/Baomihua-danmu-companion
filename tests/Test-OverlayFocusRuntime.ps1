#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'tests/Import-NativeTypes.ps1')
[void][System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2)
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$form=[System.Windows.Forms.Form]::new()
$form.Text='Overlay focus regression - owned video target'
$form.ClientSize=[Drawing.Size]::new(800,450)
$other=[System.Windows.Forms.Form]::new()
$other.Text='Overlay focus regression - owned other window'
$other.ClientSize=[Drawing.Size]::new(240,150)
$overlay=[Popcorn.OverlayHost]::new()
$thread=$overlay.GetType().GetField('thread',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($overlay)
$renderWindow=$overlay.GetType().GetField('window',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($overlay)
$overlayHandle=$renderWindow.GetType().GetField('handle',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($renderWindow)
$timer=[System.Windows.Forms.Timer]::new()
$timer.Interval=50
$watch=[Diagnostics.Stopwatch]::new()
$script:focusStage=0
$script:focusStageStarted=0
$script:focusError=$null
$script:expectedForeground=[IntPtr]::Zero
$script:focusEvidence=[Collections.Generic.List[object]]::new()
try {
    $overlay.SetComments([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,1,0xffffff,'切换窗口后继续显示')))
    $timer.add_Tick({
        try {
            $bounds=[Popcorn.NativeWindows]::Read($form.Handle)
            $overlay.Update($bounds,$(if($script:focusStage -eq 0){10000}else{11000}),$false,1,$false,0)
            if($script:expectedForeground -eq [IntPtr]::Zero){$script:expectedForeground=[Popcorn.NativeWindows]::GetForegroundWindow()}
            if($watch.ElapsedMilliseconds-$script:focusStageStarted -lt 1000){return}
            $snapshot=$overlay.Snapshot()
            $foreground=[Popcorn.NativeWindows]::GetForegroundWindow()
            $script:focusEvidence.Add([pscustomobject]@{stage=$script:focusStage;foreground=$foreground.ToInt64();target=$form.Handle.ToInt64();other=$other.Handle.ToInt64();snapshot=$snapshot})
            if($script:focusStage -in @(2,4)){
                Assert (-not $snapshot.Visible) 'Minimized and hidden real targets must hide their overlay.'
            } else {
                Assert ($snapshot.Visible -and $snapshot.ActiveCount -eq 1) 'A real visible target must retain comments even while another window has focus.'
                Assert ($snapshot.ClickThrough -and $snapshot.NoActivate) 'Background display must preserve click-through and no-activate styles.'
            }
            if($script:focusStage -eq 1){
                Assert ($foreground -eq $other.Handle -and $foreground -ne $form.Handle) 'The regression must actually focus the owned other window.'
                Assert ($snapshot.X[0] -lt $script:focusEvidence[0].snapshot.X[0]) 'An already entered comment must remain present after focus switches.'
            }
            Assert ($foreground -eq $script:expectedForeground) 'The overlay must not steal focus while remaining visible.'
            $script:focusStage++
            $script:focusStageStarted=$watch.ElapsedMilliseconds
            switch($script:focusStage){
                1 {$other.Show();$other.Activate()}
                2 {$form.WindowState=[System.Windows.Forms.FormWindowState]::Minimized}
                3 {$form.WindowState=[System.Windows.Forms.FormWindowState]::Normal;$other.Activate()}
                4 {$form.Hide()}
                5 {$form.Show();$other.Activate()}
                6 {$timer.Stop();$form.Close()}
            }
            $script:expectedForeground=[Popcorn.NativeWindows]::GetForegroundWindow()
        } catch {$script:focusError=$_;$timer.Stop();$form.Close()}
    })
    $form.add_Shown({$watch.Start();$timer.Start()})
    [System.Windows.Forms.Application]::Run($form)
} finally {
    $timer.Dispose();$overlay.Dispose();$other.Dispose();$form.Dispose()
}
$script:focusEvidence|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $root 'artifacts/overlay-focus-runtime-20261028.json')
if($script:focusError){throw $script:focusError}
Assert ($script:focusStage -eq 6) 'The complete focus/minimize/restore/hide cycle must finish.'
Assert (-not $thread.IsAlive -and $null -eq [Popcorn.NativeWindows]::Read($overlayHandle)) 'Normal close must stop the renderer and destroy the overlay HWND.'
'PASS: production overlay remains visible behind an actually focused other window, respects minimize/hide/restore, never steals focus, and closes cleanly.'
