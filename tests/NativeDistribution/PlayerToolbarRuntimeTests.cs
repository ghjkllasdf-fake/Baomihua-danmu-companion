using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Popcorn;

internal static class PlayerToolbarRuntimeTests
{
    internal static void VerifyFixture()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var viewport=new Rectangle(50,50,680,480);
        var settings=new DanmakuSettings{Opacity=.65,FontSize=62,SpeedMultiplier=1.6,ShadowStrength=.42};
        int commits=0;
        using var panel=new PlayerToolbarSheet((edit,commit)=>{if(commit){edit(settings);settings.Validate();commits++;}}){Opacity=.90};
        panel.Observe(settings);
        Check(commits==0,"Syncing the panel must not persist another edit.");
        panel.Show();
        panel.Controls.OfType<Button>().Single(control=>control.Text=="1/10").PerformClick();
        panel.Hide();
        Check(commits==1 && settings.AreaFraction==.1 && settings.FontSize==62 && settings.ShadowStrength==.42,
            "The panel must edit only the requested display field.");
        Check(panel.Controls.OfType<Label>().All(control=>control.Text!="\u9f20\u6807\u79fb\u5f00\u81ea\u52a8\u6536\u8d77"),"The obsolete footer note must be absent.");
        Check(PlayerRow.TryPlace(viewport,96,out var buttons,out var popup) && popup.Size==panel.ClientSize && panel.Opacity==.90,
            "The panel must preserve the fixed size and opacity.");
        Check(PlayerRow.TryPlace(new Rectangle(0,0,1920,1080),96,out _,out var larger) && larger.Size==popup.Size,
            "Resizing the player must not resize the panel.");
        using var target=new Form{StartPosition=FormStartPosition.Manual,Bounds=viewport};
        using var cover=new Form{StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(buttons.Left+buttons.Width/2,buttons.Top-20,160,80)};
        using var toolbar=new PlayerToolbar((edit,commit)=>{},PlayerToolbar.ExitFadeMs);
        target.Show();cover.Show();cover.Activate();Application.DoEvents();
        IntPtr foreground=NativeWindows.GetForegroundWindow();
        var window=new VideoWindow(target.Handle,"fixture",viewport.X,viewport.Y,viewport.Width,viewport.Height)
            {DpiScale=1,ProcessId=(uint)Environment.ProcessId};
        toolbar.Update(window,true,0);toolbar.Update(window,true,250);
        Check(toolbar.ButtonsVisible && NativeWindows.GetForegroundWindow()==foreground,"Toolbar must not activate its target.");
        toolbar.Update(window,false,260);toolbar.Update(window,false,759);
        Check(toolbar.IsFading && toolbar.ButtonsVisible,"Exit must not end before 500 ms.");
        toolbar.Update(window,false,760);Check(!toolbar.ButtonsVisible,"Exit must finish after 500 ms.");
        toolbar.Update(window,true,770);toolbar.Update(window,null,780);Check(!toolbar.ButtonsVisible,"Unknown proof must hide immediately.");
        toolbar.Update(window with{ProcessId=0},true,790);Check(!toolbar.ButtonsVisible,"Unknown window identity must not be trusted.");
        Console.WriteLine("PASS: native toolbar visibility, timing, fixed panel and shared settings edit.");
    }

    private static void Check(bool condition,string message)=>DistributionTests.Check(condition,message);

    // Let the user operate the player. This only records the distinct visibility
    // states from the production owned worker and never reads titles into logs.
    internal static void Run(string root)
    {
        using var probe=new PlayerProbe(Path.GetFullPath(root),toolbar:true);
        var clock=Stopwatch.StartNew();int visible=0,hidden=0,unknown=0;
        bool? previous=null;int edges=0;
        Console.WriteLine("Move the pointer over the player, then let its controls hide. Observing for up to 20 seconds.");
        while(clock.ElapsedMilliseconds<20000)
        {
            bool? state=probe.Latest.ControlsVisible;
            if(state==true)visible++;else if(state==false)hidden++;else unknown++;
            if(state.HasValue && previous!=state){edges++;previous=state;}
            if(visible>0 && hidden>0 && edges>=2)break;
            Task.Delay(35).GetAwaiter().GetResult();
        }
        Console.WriteLine($"Toolbar samples: visible={visible} hidden={hidden} unknown={unknown} transitions={edges}");
        DistributionTests.Check(visible>0 && hidden>0 && edges>=2,"No observed show/hide pair from the production toolbar worker.");
    }
}
