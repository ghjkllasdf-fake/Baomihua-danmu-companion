using System;
using System.Drawing;

namespace Popcorn;

internal static class TrayPlacementTests
{
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }

    internal static void Run()
    {
        var display=new Rectangle(0,0,1920,1080);
        var desired=new Size(380,540);
        Verify(display,new Rectangle(0,0,1920,1040),new Point(1795,1060),desired,TaskbarEdge.Bottom);
        Verify(display,new Rectangle(0,40,1920,1040),new Point(1795,20),desired,TaskbarEdge.Top);
        Verify(display,new Rectangle(48,0,1872,1080),new Point(24,990),desired,TaskbarEdge.Left);
        Verify(display,new Rectangle(0,0,1872,1080),new Point(1896,990),desired,TaskbarEdge.Right);

        var secondDisplay=new Rectangle(-1920,-200,1920,1080);
        Verify(secondDisplay,new Rectangle(-1872,-200,1872,1080),new Point(-1896,820),desired,TaskbarEdge.Left);
        Verify(secondDisplay,new Rectangle(-1920,-200,1872,1080),new Point(-24,820),desired,TaskbarEdge.Right);
        Verify(secondDisplay,new Rectangle(-1920,-200,1920,1040),new Point(-150,860),desired,TaskbarEdge.Bottom);
        Verify(secondDisplay,new Rectangle(-1920,-160,1920,1040),new Point(-150,-180),desired,TaskbarEdge.Top);

        // Auto-hidden taskbars may leave no excluded work-area strip.
        Verify(display,display,new Point(1250,1078),desired,TaskbarEdge.Bottom);
        Verify(display,display,new Point(1250,1),desired,TaskbarEdge.Top);
        Verify(display,display,new Point(1,450),desired,TaskbarEdge.Left);
        Verify(display,display,new Point(1918,450),desired,TaskbarEdge.Right);
        Verify(secondDisplay,secondDisplay,new Point(-1919,250),desired,TaskbarEdge.Left);
        var autoHidden=TrayPlacement.Compute(display,display,new Point(1795,1060),desired);
        Check(autoHidden.Bounds.Bottom<=1048,"An auto-hidden taskbar's clicked icon must not be covered by the final flyout.");

        // Final bounds must fit even when the work area is smaller than the panel.
        Verify(new Rectangle(0,0,320,160),new Rectangle(0,0,320,120),new Point(260,140),desired,TaskbarEdge.Bottom);
        Verify(new Rectangle(-600,-60,80,40),new Rectangle(-592,-60,72,40),new Point(-596,-40),desired,TaskbarEdge.Left);
        Verify(display,new Rectangle(0,0,1920,1040),new Point(1795,1060),desired,TaskbarEdge.Bottom,16);
        Console.WriteLine("PASS: tray flyout edge selection, clamped work areas, negative coordinates, auto-hide and slide direction.");
    }

    private static void Verify(Rectangle screen,Rectangle work,Point anchor,Size desired,TaskbarEdge edge,int gap=8)
    {
        var placement=TrayPlacement.Compute(screen,work,anchor,desired,gap);
        Rectangle bounds=placement.Bounds;
        Point from=placement.SlideFrom;
        string context=$"edge={edge}, screen={screen}, work={work}, anchor={anchor}, desired={desired}";
        Check(placement.Edge==edge,$"Incorrect taskbar edge: {context}; got {placement.Edge}.");
        Check(bounds.Width==Math.Min(desired.Width,work.Width) && bounds.Height==Math.Min(desired.Height,work.Height),
            $"Panel dimensions must preserve the requested size unless capped by the available work area: {context}; got {bounds}.");
        Check(bounds.Width>0 && bounds.Height>0 && work.Contains(bounds),
            $"Final panel must remain wholly inside the work area: {context}; got {bounds}.");

        int crossDistance=edge is TaskbarEdge.Bottom or TaskbarEdge.Top
            ? DistanceToRange(anchor.X,bounds.Left,bounds.Right)
            : DistanceToRange(anchor.Y,bounds.Top,bounds.Bottom);
        Check(crossDistance<=gap,$"The flyout must remain anchored near the tray icon along the taskbar: {context}; got {bounds}.");

        int distanceFromEdge=edge switch {
            TaskbarEdge.Bottom=>work.Bottom-bounds.Bottom,
            TaskbarEdge.Top=>bounds.Top-work.Top,
            TaskbarEdge.Left=>bounds.Left-work.Left,
            TaskbarEdge.Right=>work.Right-bounds.Right,
            _=>throw new Exception("Unexpected taskbar edge.")
        };
        int freeSpace=edge is TaskbarEdge.Bottom or TaskbarEdge.Top ? work.Height-bounds.Height : work.Width-bounds.Width;
        Check(distanceFromEdge>=Math.Min(gap,freeSpace),$"The panel must keep the requested edge gap when space permits: {context}; got {bounds}.");

        bool correctSlide=edge switch {
            TaskbarEdge.Bottom=>from.X==bounds.X && from.Y>bounds.Y,
            TaskbarEdge.Top=>from.X==bounds.X && from.Y<bounds.Y,
            TaskbarEdge.Left=>from.X<bounds.X && from.Y>bounds.Y,
            TaskbarEdge.Right=>from.X>bounds.X && from.Y>bounds.Y,
            _=>false
        };
        Check(correctSlide,$"The panel must slide into the work area in the taskbar-specific direction: {context}; from {from}, to {bounds.Location}.");
    }

    private static int DistanceToRange(int value,int lower,int upper)=>value<lower ? lower-value : value>upper ? value-upper : 0;
}
