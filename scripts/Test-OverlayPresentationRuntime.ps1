#requires -Version 7.6
# Real HWND/lifecycle exercise against our own synthetic target. No player input.
param([ValidateSet('dwm','layered')][string]$ExpectedMode='dwm')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop\Host.ps1') -Check
[void][System.Windows.Forms.Application]::SetHighDpiMode([System.Windows.Forms.HighDpiMode]::PerMonitorV2)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class OwnOverlayWindows {
    private delegate bool EnumProc(IntPtr hwnd,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int count);
    public static long[] Read() {
        var result=new List<long>();
        EnumWindows((hwnd,_)=>{
            GetWindowThreadProcessId(hwnd,out uint pid);
            if(pid!=Environment.ProcessId) return true;
            var text=new StringBuilder(128);GetWindowText(hwnd,text,text.Capacity);
            if(text.ToString()=="Popcorn danmaku overlay") result.Add(hwnd.ToInt64());
            return true;
        },IntPtr.Zero);
        return result.ToArray();
    }
}
'@
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$form=[System.Windows.Forms.Form]::new()
$form.Text='Overlay presentation runtime - synthetic target'
$form.ClientSize=[Drawing.Size]::new(800,450)
$overlay=$null
$timer=[System.Windows.Forms.Timer]::new()
$timer.Interval=50
$watch=[Diagnostics.Stopwatch]::new()
$script:stage=0
$script:stageStarted=0
$script:foregroundBaseline=[IntPtr]::Zero
$script:failure=$null
$script:evidence=[Collections.Generic.List[object]]::new()
try {
    $overlay=[Popcorn.OverlayHost]::new($true)
    $renderThread=$overlay.GetType().GetField('thread',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($overlay)
    Assert ($overlay.CompositionMode -ceq $ExpectedMode) 'Unexpected composition mode.'
    $overlay.SetComments([Popcorn.Comment[]]@([Popcorn.Comment]::new(10000,1,16777215,'原生组合运行测试')))
    $timer.add_Tick({
        try {
            if($script:foregroundBaseline -eq [IntPtr]::Zero){$script:foregroundBaseline=[Popcorn.NativeWindows]::GetForegroundWindow()}
            $bounds=[Popcorn.NativeWindows]::Read($form.Handle)
            $overlay.Update($bounds,10000,$false,1,$false,0)
            if($watch.ElapsedMilliseconds-$script:stageStarted -lt 1000){return}
            $snapshot=$overlay.Snapshot()
            $foreground=[Popcorn.NativeWindows]::GetForegroundWindow()
            $script:evidence.Add([pscustomobject]@{Stage=$script:stage;Mode=$overlay.CompositionMode;ExpectedForeground=$script:foregroundBaseline.ToInt64();ActualForeground=$foreground.ToInt64();TargetHandle=$form.Handle.ToInt64();Snapshot=$snapshot})
            if($script:stage -eq 2){
                Assert (-not $snapshot.Visible) 'A hidden target must hide its native parent.'
            } else {
                Assert ($snapshot.Visible -and $snapshot.ActiveCount -eq 1) 'Visible paused comments must render.'
                Assert ($snapshot.ClickThrough -and $snapshot.NoActivate) 'Actual native input styles were lost.'
                Assert ($snapshot.OverlayXpx -eq $bounds.X -and $snapshot.OverlayYpx -eq $bounds.Y) 'Parent position does not match the physical target.'
                Assert ($snapshot.ViewportWidthPx -eq $bounds.Width -and $snapshot.ViewportHeightPx -eq $bounds.Height) 'The WPF child did not resize with its parent.'
                Assert ($foreground -eq $script:foregroundBaseline) 'Showing/following the overlay changed the previously observed foreground.'
            }
            $script:stage++
            $script:stageStarted=$watch.ElapsedMilliseconds
            switch($script:stage){
                1 {$form.ClientSize=[Drawing.Size]::new(1024,576)}
                2 {$form.Hide()}
                3 {$form.Show();$script:foregroundBaseline=[Popcorn.NativeWindows]::GetForegroundWindow()}
                4 {$timer.Stop();$form.Close()}
            }
        } catch {$script:failure=$_;$timer.Stop();$form.Close()}
    })
    $form.add_Shown({$watch.Start();$timer.Start()})
    [System.Windows.Forms.Application]::Run($form)
} finally {
    $timer.Dispose()
    if($overlay){$overlay.Dispose()}
    $form.Dispose()
}
$script:evidence|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'artifacts\dwm-presentation-runtime-20261028.json')
if($script:failure){throw $script:failure}
Assert ($script:stage -eq 4) 'The complete show/resize/hide/restore cycle did not run.'
Assert (-not $renderThread.IsAlive) 'The renderer thread remained alive after normal disposal.'
Assert ([OwnOverlayWindows]::Read().Count -eq 0) 'A native overlay parent remained after normal disposal.'
'PASS: real PS7 WPF child/parent show, physical resize, hide, restore, input styles, no foreground theft, renderer thread exit and parent HWND destruction.'
