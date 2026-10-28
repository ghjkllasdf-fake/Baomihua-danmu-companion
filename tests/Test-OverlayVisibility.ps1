#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop/native/Overlay.cs'))
$start=$source.IndexOf('    public void Follow()')
$end=$source.IndexOf("`n}",$start)
if($start -lt 0 -or $end -le $start){throw 'Actual overlay-follow seam was not found.'}
$method=$source.Substring($start,$end-$start)
$fixture=@'
using System;
namespace Popcorn;
public sealed record VideoWindow(IntPtr Handle,string Title,int X,int Y,int Width,int Height);
public sealed class VisibilitySurface {
    public bool HasComments=true,Enabled=true;
    public int Rebuilds;
    public void Rebuild(bool preserve){if(!preserve)throw new Exception("Restore must preserve current motion.");Rebuilds++;}
}
public sealed class VisibilityPresentation {public void ResizeContent(int width,int height){}}
public static class NativeWindows {
    public static bool Visible=true,Minimized;
    public static IntPtr Foreground=new(1);
    public static VideoWindow Bounds=new(new(1),"video",10,20,800,450);
    public static bool IsWindowVisible(IntPtr hwnd)=>Visible;
    public static bool IsIconic(IntPtr hwnd)=>Minimized;
    public static IntPtr GetForegroundWindow()=>Foreground;
    public static VideoWindow Read(IntPtr hwnd)=>Bounds;
    public static void Follow(IntPtr overlay,VideoWindow bounds){}
}
public sealed class VisibilityHarness {
    public bool benchmark=false;
    public VideoWindow Target=NativeWindows.Bounds;
    private VideoWindow lastBounds;
    private readonly IntPtr handle=new(2);
    public readonly VisibilitySurface Surface=new();
    private readonly VisibilityPresentation presentation=new();
    public bool IsVisible;
    private void Show()=>IsVisible=true;
    private void Hide()=>IsVisible=false;
/* FOLLOW */
}
public static class OverlayVisibilityTests {
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run(){
        var host=new VisibilityHarness();host.Follow();
        Check(host.IsVisible,"A visible video with comments must display its overlay.");
        for(int i=0;i<4;i++){
            NativeWindows.Foreground=new IntPtr(10+i);host.Follow();
            Check(host.IsVisible,"Clicking another window must not hide a still-visible video overlay.");
            Check(NativeWindows.Foreground==new IntPtr(10+i),"Following must not steal foreground focus.");
        }
        Check(host.Surface.Rebuilds==1,"Focus changes must not repeatedly hide/show and rebuild the current comment scene.");
        NativeWindows.Minimized=true;host.Follow();Check(!host.IsVisible,"A minimized video must still hide the overlay.");
        NativeWindows.Minimized=false;host.Follow();Check(host.IsVisible,"Restoring the video must restore comments without requiring focus.");
        NativeWindows.Visible=false;host.Follow();Check(!host.IsVisible,"A hidden video must hide comments.");
        NativeWindows.Visible=true;host.Surface.Enabled=false;host.Follow();Check(!host.IsVisible,"The explicit display switch must still hide comments.");
        host.Surface.Enabled=true;host.Target=null;host.Follow();Check(!host.IsVisible,"Closing or losing the target must hide comments.");
        host.Target=NativeWindows.Bounds;host.Follow();
        NativeWindows.Bounds=NativeWindows.Bounds with {Title="different video"};host.Follow();
        Check(!host.IsVisible,"A mismatched window identity must not show comments for the old video.");
        Console.WriteLine("PASS: actual follow policy keeps unfocused visible video comments, avoids focus rebuild/steal, and respects minimize/hide/close/disable/identity.");
    }
}
'@
Add-Type -TypeDefinition $fixture.Replace('/* FOLLOW */',$method)
[Popcorn.OverlayVisibilityTests]::Run()
