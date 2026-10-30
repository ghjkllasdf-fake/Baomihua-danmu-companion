using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Popcorn;

internal sealed class TrayFlyout : IDisposable
{
    private readonly Form form;
    private readonly Func<bool> menuVisible;
    private readonly Func<bool> animations;
    private readonly Timer animation=new() {Interval=15},dismiss=new() {Interval=120};
    private FlyoutBounds placement;
    private double started;
    private bool disposed;
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint {public int X,Y;}
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point,uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor,int kind,out uint x,out uint y);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    internal TrayFlyout(Form form,Func<bool> menuVisible,Func<bool> animations=null)
    {
        this.form=form;this.menuVisible=menuVisible;
        this.animations=animations??(()=>SystemInformation.IsMenuAnimationEnabled && NativeWindows.ClientAreaAnimations);
        animation.Tick+=Animate;dismiss.Tick+=Dismiss;
        form.Deactivate+=Deactivated;form.Activated+=Activated;
    }
    private void Activated(object sender,EventArgs args)=>dismiss.Stop();
    private void Deactivated(object sender,EventArgs args){if(!disposed)dismiss.Start();}
    private void Dismiss(object sender,EventArgs args)
    {
        if(!form.Visible){dismiss.Stop();return;}
        if(menuVisible())return;
        IntPtr active=NativeWindows.GetForegroundWindow();
        // Native select menus and WebView-owned popups are not outside clicks.
        for(int count=0;active!=IntPtr.Zero && count<16;count++)
        {
            if(active==form.Handle || GetAncestor(active,2)==form.Handle){dismiss.Stop();return;}
            IntPtr owner=GetWindow(active,4);
            if(owner==active)break;
            active=owner;
        }
        Hide();
    }
    private void Animate(object sender,EventArgs args)
    {
        if(!animations()){form.Location=placement.Bounds.Location;animation.Stop();return;}
        double progress=Math.Clamp((Monotonic.Now-started)/180,0,1),eased=1-Math.Pow(1-progress,3);
        form.Location=new Point((int)Math.Round(placement.SlideFrom.X+(placement.Bounds.X-placement.SlideFrom.X)*eased),
            (int)Math.Round(placement.SlideFrom.Y+(placement.Bounds.Y-placement.SlideFrom.Y)*eased));
        if(progress>=1)animation.Stop();
    }
    internal void Open(Point anchor)
    {
        if(disposed)return;
        dismiss.Stop();animation.Stop();
        var screen=Screen.FromPoint(anchor);
        double scale=form.DeviceDpi/96.0;
        IntPtr monitor=MonitorFromPoint(new NativePoint {X=anchor.X,Y=anchor.Y},2);
        if(GetDpiForMonitor(monitor,0,out uint dpi,out _)==0 && dpi>0)scale=dpi/96.0;
        placement=TrayPlacement.Compute(screen.Bounds,screen.WorkingArea,anchor,new Size((int)Math.Round(380*scale),(int)Math.Round(540*scale)),(int)Math.Round(8*scale));
        bool animate=animations() && !form.Visible;
        // Restore the actual HWND before WinForms applies placement; changing the
        // managed WindowState alone can leave a borderless HWND minimized.
        if(form.IsHandleCreated && NativeWindows.IsIconic(form.Handle))NativeWindows.RestoreWindow(form.Handle);
        form.WindowState=FormWindowState.Normal;
        form.Bounds=animate ? new Rectangle(placement.SlideFrom,placement.Bounds.Size) : placement.Bounds;
        form.TopMost=true;form.Show();form.Activate();
        if(animate){started=Monotonic.Now;animation.Start();}
    }
    internal void Hide()
    {
        if(disposed)return;
        animation.Stop();dismiss.Stop();form.Hide();form.TopMost=false;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        form.Deactivate-=Deactivated;form.Activated-=Activated;
        animation.Stop();dismiss.Stop();animation.Dispose();dismiss.Dispose();
    }
}
