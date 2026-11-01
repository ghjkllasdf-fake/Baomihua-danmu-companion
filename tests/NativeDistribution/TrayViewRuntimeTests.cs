using System;
using System.Drawing;
using System.Windows.Forms;

namespace Popcorn;

internal static class TrayViewRuntimeTests
{
    internal static void Run()
    {
        using var form=new Form{FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual};
        using var flyout=new TrayFlyout(form,()=>false,()=>false);
        var screen=Screen.PrimaryScreen;var anchor=new Point(screen.WorkingArea.Left+screen.WorkingArea.Width/2,screen.Bounds.Bottom-1);
        flyout.SetView("home",true);flyout.Open(anchor);
        var first=form.Bounds;
        flyout.SetView("settings",true);var expanded=form.Bounds;
        Console.WriteLine($"Native view bounds: home={first}; expanded={expanded}; dpi={form.DeviceDpi}; work={screen.WorkingArea}");
        DistributionTests.Check(first.Bottom==expanded.Bottom && expanded.Height>=first.Height,"View changes retain the bottom taskbar anchor without growing outside the work area.");
        flyout.SetView("home",true);DistributionTests.Check(form.Bounds==first,"Returning home restores the exact native popup bounds.");
        flyout.Hide();flyout.SetView("search",false);
        DistributionTests.Check(!form.Visible,"A hidden view-size change must not reopen the popup.");
        flyout.Open(anchor);DistributionTests.Check(form.Bounds==expanded,"Reopening uses the retained target size and current anchor.");
        DistributionTests.Check(screen.WorkingArea.Contains(form.Bounds),"Actual popup bounds must remain inside the monitor work area.");
        flyout.Hide();
        Console.WriteLine("PASS: actual native view resize, anchor retention, hidden state and reopen; controlled window, not Shell tray input.");
    }
}
