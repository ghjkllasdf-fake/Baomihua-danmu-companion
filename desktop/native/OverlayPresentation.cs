using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Popcorn;

// Keep WPF off the layered parent: HwndTarget removes WS_EX_LAYERED from a
// non-per-pixel WPF HWND. A native parent supplies cross-process input transparency.
internal sealed class OverlayPresentation : IDisposable
{
    private Window fallback;
    private HwndSource source;
    public IntPtr Handle { get; private set; }
    public string Mode=>fallback==null ? "dwm" : "layered";
    public bool IsVisible=>Handle!=IntPtr.Zero && NativeWindows.IsWindowVisible(Handle);
    public static OverlayPresentation Create(FrameworkElement surface)
    {
        var result=new OverlayPresentation();
        try {
            result.Handle=NativeWindows.CreateDwmOverlay();
            if(result.Handle!=IntPtr.Zero) {
                result.source=new HwndSource(new HwndSourceParameters("Popcorn WPF content") {
                    ParentWindow=result.Handle,WindowStyle=0x40000000|0x10000000,Width=1,Height=1
                });
                result.source.CompositionTarget.BackgroundColor=Colors.Transparent;
                result.source.RootVisual=surface;
                return result;
            }
        } catch(Exception error) when(error is Win32Exception or ExternalException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) {
            // A failed native composition attempt must release its root/HWND before fallback.
        }
        result.ReleaseNative();
        try {
            result.fallback=new Window {
                WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=Brushes.Transparent,
                ShowInTaskbar=false,ShowActivated=false,Topmost=true,ResizeMode=ResizeMode.NoResize,
                Content=surface,IsHitTestVisible=false
            };
            result.Handle=new WindowInteropHelper(result.fallback).EnsureHandle();
            NativeWindows.ClickThrough(result.Handle);
            return result;
        } catch { result.Dispose();throw; }
    }
    public void Show() { if(fallback!=null) fallback.Show();else NativeWindows.ShowOverlay(Handle); }
    public void Hide() { if(fallback!=null) fallback.Hide();else NativeWindows.HideOverlay(Handle); }
    public void ResizeContent(int width,int height) { if(source!=null) NativeWindows.ResizeOverlayChild(source.Handle,width,height); }
    private void ReleaseNative()
    {
        try {
            if(source!=null) { source.RootVisual=null;source.Dispose();source=null; }
        } finally {
            if(Handle!=IntPtr.Zero) { NativeWindows.DestroyOverlay(Handle);Handle=IntPtr.Zero; }
        }
    }
    public void Dispose()
    {
        if(fallback!=null) {
            var closing=fallback;fallback=null;Handle=IntPtr.Zero;
            closing.Content=null;closing.Close();
        } else ReleaseNative();
    }
}
