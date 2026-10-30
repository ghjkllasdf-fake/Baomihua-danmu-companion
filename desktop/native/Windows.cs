using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Popcorn;

public static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left,Right,Top,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    private delegate bool EnumProc(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW")] private static extern bool SystemParametersInfo(uint action,uint value,out int result,uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref Point point);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int size);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window,uint key,byte alpha,uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window,ref Margins margins);
    internal static IntPtr CreateDwmOverlay()
    {
        const uint styles=0x80000|0x20|0x08000000|0x80;
        // WS_CLIPCHILDREN prevents the parent's GDI background/text painting
        // from replacing transparent pixels outside the WPF drawing bounds.
        IntPtr window=CreateWindowEx(styles,"STATIC","Popcorn danmaku overlay",0x82000000,
            0,0,1,1,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(window==IntPtr.Zero) return IntPtr.Zero;
        bool accepted=false;
        try {
            var margins=new Margins { Left=-1,Right=-1,Top=-1,Bottom=-1 };
            if(DwmIsCompositionEnabled(out bool enabled)<0 || !enabled ||
                DwmExtendFrameIntoClientArea(window,ref margins)<0 ||
                !SetLayeredWindowAttributes(window,0,255,2) || !HasExtendedStyles(window,styles)) return IntPtr.Zero;
            accepted=true;return window;
        } finally { if(!accepted) DestroyWindow(window); }
    }
    internal static void DestroyOverlay(IntPtr window)=>DestroyWindow(window);
    internal static void ShowOverlay(IntPtr window)=>ShowWindow(window,4);
    internal static void RestoreWindow(IntPtr window)=>ShowWindow(window,9);
    internal static void HideOverlay(IntPtr window)=>ShowWindow(window,0);
    internal static void ResizeOverlayChild(IntPtr window,int width,int height)=>SetWindowPos(window,IntPtr.Zero,0,0,width,height,0x10|0x4);
    public static void ClickThrough(IntPtr window) => SetWindowLongPtr(window,-20,new IntPtr(GetWindowLongPtr(window,-20).ToInt64()|0x20|0x08000000|0x80));
    public static bool HasExtendedStyles(IntPtr window,long flags)=>window!=IntPtr.Zero && (GetWindowLongPtr(window,-20).ToInt64()&flags)==flags;
    internal static uint ProcessId(IntPtr window) {GetWindowThreadProcessId(window,out uint process);return process;}
    // Read only: SPI_GETCLIENTAREAANIMATION, independent of menu animation.
    // https://learn.microsoft.com/windows/win32/winauto/client-area-animation
    internal static bool ClientAreaAnimations=>SystemParametersInfo(0x1042,0,out int enabled,0) && enabled!=0;
    internal static bool IsVideoPointExposed(IntPtr overlay,IntPtr video,int x,int y)
    {
        IntPtr hit=GetAncestor(WindowFromPoint(new Point {X=x,Y=y}),2);
        return hit==overlay || hit==GetAncestor(video,2);
    }
    public static VideoWindow Read(IntPtr window)
    {
        uint process=ProcessId(window);
        if(process==0)return null;
        if (!GetClientRect(window,out var rect)) return null;
        var point=new Point(); if (!ClientToScreen(window,ref point)) return null;
        var title=new StringBuilder(1024); GetWindowText(window,title,title.Capacity);
        // GetDpiForWindow reports the target window's DPI awareness, not a guessed monitor scale.
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdpiforwindow
        uint dpi=GetDpiForWindow(window);
        if(ProcessId(window)!=process)return null;
        return new VideoWindow(window,title.ToString(),point.X,point.Y,rect.Right-rect.Left,rect.Bottom-rect.Top)
        {
            ProcessId=process,DpiScale=dpi>0 ? dpi/96.0 : null,WindowVisible=IsWindowVisible(window),
            WindowMinimized=IsIconic(window),IsForeground=GetForegroundWindow()==window
        };
    }
    public static VideoWindow SelectPlayer(IEnumerable<VideoWindow> candidates,IntPtr foreground,string preferredTitle="")
    {
        VideoWindow best=null; int bestPriority=-1; preferredTitle=preferredTitle?.Trim()??"";
        foreach (var candidate in candidates)
        {
            if (candidate==null || candidate.Width<300 || candidate.Height<150) continue;
            string title=candidate.Title?.Trim()??"";
            int priority=preferredTitle.Length>0 && title==preferredTitle ? 2 : title.Length>0 && title is not "网易爆米花" and not "爆米花" ? 1 : 0;
            bool foregroundCandidate=candidate.Handle==foreground, foregroundBest=best?.Handle==foreground;
            if (best==null || priority>bestPriority || priority==bestPriority &&
                (foregroundCandidate && !foregroundBest || foregroundCandidate==foregroundBest && (long)candidate.Width*candidate.Height>(long)best.Width*best.Height))
            { best=candidate; bestPriority=priority; }
        }
        return best;
    }
    public static VideoWindow FindPlayer(string preferredTitle="")
    {
        var ids=new HashSet<uint>();
        foreach (var process in Process.GetProcessesByName("BaoMiHua")) { using (process) ids.Add((uint)process.Id); }
        var candidates=new List<VideoWindow>();
        EnumWindows((window,_)=>{
            GetWindowThreadProcessId(window,out uint pid);
            if (!ids.Contains(pid) || !IsWindowVisible(window)) return true;
            var candidate=Read(window);
            if (candidate!=null) candidates.Add(candidate);
            return true;
        },IntPtr.Zero);
        return SelectPlayer(candidates,GetForegroundWindow(),preferredTitle);
    }
    internal static VideoWindow[] FindLibraries(VideoWindow video)
    {
        uint process=ProcessId(video.Handle);
        if(process==0 || video.ProcessId!=0 && process!=video.ProcessId)return Array.Empty<VideoWindow>();
        var libraries=new List<VideoWindow>();
        EnumWindows((window,_)=>{
            if(window==video.Handle || ProcessId(window)!=process || !IsWindowVisible(window))return true;
            var candidate=Read(window);
            if(candidate!=null && candidate.Title is "网易爆米花" or "爆米花")libraries.Add(candidate);
            return true;
        },IntPtr.Zero);
        return libraries.ToArray();
    }
    internal static bool IsPlayerProcess(VideoWindow video)
    {
        uint id=ProcessId(video.Handle);
        if(id==0 || video.ProcessId!=0 && video.ProcessId!=id)return false;
        try{using var process=Process.GetProcessById((int)id);return process.ProcessName.Equals("BaoMiHua",StringComparison.OrdinalIgnoreCase);}
        catch(ArgumentException){return false;}
    }
    // Keep an independent no-activate HWND adjacent to the video. Cross-process
    // parenting would also couple the lifetime of the WPF presentation source.
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos
    internal static bool FollowCompanion(IntPtr overlay,VideoWindow video,bool geometryChanged=true,bool show=false)
    {
        uint flags=0x10|0x200|(show ? 0x40u : 0); // NOACTIVATE | NOOWNERZORDER | optional SHOWWINDOW
        bool targetTopmost=HasExtendedStyles(video.Handle,0x8);
        if(HasExtendedStyles(overlay,0x8)!=targetTopmost &&
            !SetWindowPos(overlay,new IntPtr(targetTopmost ? -1 : -2),0,0,0,0,flags|0x1|0x2))return false;
        IntPtr previous=GetWindow(video.Handle,3); // GW_HWNDPREV
        // The common case changes only geometry; changing Z order every frame can
        // reorder unrelated windows and interfere with activation.
        if(previous==overlay && !geometryChanged && !show)return true;
        if(!geometryChanged)flags|=0x1|0x2;
        if(previous==overlay)flags|=0x4;
        return SetWindowPos(overlay,previous==overlay ? IntPtr.Zero : previous,video.X,video.Y,video.Width,video.Height,flags);
    }
    public static void Follow(IntPtr overlay,VideoWindow video)=>FollowCompanion(overlay,video);
}
