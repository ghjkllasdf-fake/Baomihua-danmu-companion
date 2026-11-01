using System;
using System.Linq;
using System.Windows;

namespace Popcorn;

internal static class FixedCommentTests
{
    private static void Check(bool value,string message)=>DistributionTests.Check(value,message);
    internal static void Run()
    {
        foreach(double area in new[]{.25,.5,.75,1})
        {
            var surface=new OverlaySurface();
            try
            {
                surface.Measure(new Size(1280,720));surface.Arrange(new Rect(0,0,1280,720));
                surface.Configure(new(){AreaFraction=area,ScaleWithWindow=false});
                surface.Load(new[]{new Comment(10000,5,0xffffff,"top"),new Comment(10000,1,0xffffff,"rolling"),new Comment(10000,4,0xffffff,"bottom")});
                surface.UpdateClock(10000,false,Monotonic.Now,1,false,0);surface.Frame();
                var before=surface.Snapshot(false);
                Check(before.Text.Contains("top"),"Top comments must be admitted in every display area.");
                Check(before.Text.Contains("bottom")== (area==1),"Bottom comments require the full-screen display area.");
                int top=Array.IndexOf(before.Text,"top"),rolling=Array.IndexOf(before.Text,"rolling");
                Check(before.X[top]>0 && before.X[top]<1280,"Top comments must start centred, not outside the right edge.");
                Check(before.Y[top]<40,"Top comments must start in the top lane.");
                surface.UpdateClock(10500,false,Monotonic.Now,1,false,0);surface.Frame();var after=surface.Snapshot(false);
                Check(after.X[top]==before.X[top] && after.Y[top]==before.Y[top],"Top comments must remain fixed while media time advances.");
                Check(after.X[rolling]<before.X[rolling],"Ordinary comments must still move left.");
                if(area==1){int bottom=Array.IndexOf(before.Text,"bottom");Check(before.Y[bottom]>600 && after.X[bottom]==before.X[bottom],"Bottom comments must stay fixed near the full-screen bottom.");}
                surface.Configure(new(){AreaFraction=area,ScaleWithWindow=false,Top=false,Bottom=false});surface.Frame();
                Check(surface.Snapshot(false).Text.SequenceEqual(new[]{"rolling"}),"Type switches must remove only fixed comments.");
            }
            finally{surface.Detach();}
        }
        Console.WriteLine("PASS: fixed top/bottom positions, full-area bottom gate, and unchanged rolling motion.");
    }
}
