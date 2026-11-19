using System;
using System.Drawing;

namespace Popcorn;

internal static class PlayerToolbarGeometryTests
{
    private static int assertions;
    internal static void Run()
    {
        var target=new IntPtr(1234);
        var sample=new VideoWindow(target,"fixture",0,0,800,400){ProcessId=7,DpiScale=1};
        var current=new PlayerClientGeometry(new Rectangle(200,100,1600,900),120,7);
        int reads=0;
        PlayerClientGeometry? Read(IntPtr handle){Check(handle==target,"The geometry reader must receive the sampled window handle.");reads++;return current;}
        Check(PlayerToolbarPlacement.TryResolve(sample,Read,out var client,out var dpi,out var buttons,out _) && client==current.Bounds && dpi==120,
            "The host geometry must override the smaller Probe rectangle and its stale DPI.");
        Check(PlayerRow.TryPlace(new Rectangle(sample.X,sample.Y,sample.Width,sample.Height),96,out var oldButtons,out _) && oldButtons!=buttons,
            "The old Probe-based placement must reproduce the mismatch in this fixture.");
        Check(buttons.Top+buttons.Height/2==client.Bottom-(int)Math.Round(23.5*1.25),
            "At 125% DPI the button centre must align with the host's current player row.");
        var first=buttons;
        current=new PlayerClientGeometry(new Rectangle(-1200,240,1600,900),120,7);
        Check(PlayerToolbarPlacement.TryResolve(sample,Read,out client,out _,out buttons,out _) && client==current.Bounds && buttons.X-first.X==-1400 && buttons.Y-first.Y==140,
            "Moving a player must update the buttons before the Probe geometry changes.");
        current=new PlayerClientGeometry(new Rectangle(-1200,240,1600,900),192,7);
        Check(PlayerToolbarPlacement.TryResolve(sample,Read,out client,out dpi,out buttons,out _) && dpi==192 &&
            buttons.Top+buttons.Height/2==client.Bottom-(int)Math.Round(23.5*2),
            "A new host DPI must control layout without waiting for a Probe snapshot.");
        Check(PlayerToolbarPlacement.TryResolve(sample with{DpiScale=null},Read,out _,out _,out _,out _),
            "Missing Probe DPI must not disable valid host geometry.");
        Check(!PlayerToolbarPlacement.TryResolve(sample,_=>null,out _,out _,out _,out _),"A failed geometry read must not retain old coordinates.");
        Check(!PlayerToolbarPlacement.TryResolve(sample,_=>new PlayerClientGeometry(current.Bounds,120,8),out _,out _,out _,out _),"A different PID must not be trusted.");
        Check(!PlayerToolbarPlacement.TryResolve(sample,_=>new PlayerClientGeometry(current.Bounds,0,7),out _,out _,out _,out _),"An unavailable DPI must hide the buttons.");
        Check(!PlayerToolbarPlacement.TryResolve(sample,_=>new PlayerClientGeometry(current.Bounds,1537,7),out _,out _,out _,out _),"An unreasonable DPI must hide the buttons.");
        Check(!PlayerToolbarPlacement.TryResolve(sample,_=>new PlayerClientGeometry(Rectangle.Empty,120,7),out _,out _,out _,out _),"An empty client rectangle must hide the buttons.");
        Check(!PlayerToolbarPlacement.TryResolve(sample,_=>new PlayerClientGeometry(new Rectangle(0,0,80,40),120,7),out _,out _,out _,out _),"A player too small for the controls must not show them.");
        Check(!PlayerToolbarPlacement.TryResolve(sample with{ProcessId=0},Read,out _,out _,out _,out _),"Unknown identity must not query or show the buttons.");
        Check(!PlayerToolbarPlacement.TryResolve(sample with{Handle=IntPtr.Zero},Read,out _,out _,out _,out _),"An invalid handle must not query or show the buttons.");
        Check(reads==4,"Only valid sampled identities should reach the geometry reader.");
        Check(PlayerToolbarNativeGeometry.Read(IntPtr.Zero)==null,"An invalid native window must fail closed.");
        Console.WriteLine($"PASS: {assertions} toolbar geometry assertions, including 125%/200%, stale Probe and failure fallback.");
    }
    private static void Check(bool value,string message){assertions++;DistributionTests.Check(value,message);}
}
