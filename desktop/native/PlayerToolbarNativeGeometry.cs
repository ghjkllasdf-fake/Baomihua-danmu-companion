using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Popcorn;

internal static class PlayerToolbarNativeGeometry
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

    internal static PlayerClientGeometry? Read(IntPtr window)
    {
        uint process=NativeWindows.ProcessId(window);
        if(process==0 || !GetClientRect(window,out var rect))return null;
        var point=new Point();
        if(!ClientToScreen(window,ref point))return null;
        uint dpi=GetDpiForWindow(window);
        long width=(long)rect.Right-rect.Left,height=(long)rect.Bottom-rect.Top;
        if(dpi==0 || width<=0 || width>int.MaxValue || height<=0 || height>int.MaxValue || NativeWindows.ProcessId(window)!=process)return null;
        return new PlayerClientGeometry(new Rectangle(point.X,point.Y,(int)width,(int)height),dpi,process);
    }
}
