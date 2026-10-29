using System;
using System.Drawing;

namespace Popcorn;

internal enum TaskbarEdge { Bottom,Left,Right,Top }
internal readonly record struct FlyoutBounds(Rectangle Bounds,Point SlideFrom,TaskbarEdge Edge);

internal static class TrayPlacement
{
    internal static FlyoutBounds Compute(Rectangle screen,Rectangle work,Point anchor,Size desired,int gap=8)
    {
        if(work.Width<=0 || work.Height<=0)throw new ArgumentException("No usable display area.");
        int[] strips={Math.Max(0,screen.Bottom-work.Bottom),Math.Max(0,work.Left-screen.Left),
            Math.Max(0,screen.Right-work.Right),Math.Max(0,work.Top-screen.Top)};
        int[] distances={Math.Abs(screen.Bottom-anchor.Y),Math.Abs(anchor.X-screen.Left),
            Math.Abs(screen.Right-anchor.X),Math.Abs(anchor.Y-screen.Top)};
        int edge=0;
        for(int i=1;i<4;i++)if(strips[i]>strips[edge])edge=i;
        // Auto-hide returns the entire monitor as WorkingArea. The actual tray
        // click is then the nearest-edge anchor rather than a guessed primary bar.
        if(strips[edge]==0)for(int i=1;i<4;i++)if(distances[i]<distances[edge])edge=i;
        int width=Math.Clamp(desired.Width,1,work.Width),height=Math.Clamp(desired.Height,1,work.Height);
        int dx=Math.Clamp(gap,0,work.Width-width),dy=Math.Clamp(gap,0,work.Height-height);
        if(strips[edge]==0)
        {
            int iconGap=Math.Max(gap,distances[edge]+12);
            if((TaskbarEdge)edge is TaskbarEdge.Left or TaskbarEdge.Right)dx=Math.Clamp(iconGap,0,work.Width-width);
            else dy=Math.Clamp(iconGap,0,work.Height-height);
        }
        int x=anchor.X-width+16,y=anchor.Y-height+16;
        switch((TaskbarEdge)edge)
        {
            case TaskbarEdge.Bottom:y=work.Bottom-height-dy;break;
            case TaskbarEdge.Top:y=work.Top+dy;break;
            case TaskbarEdge.Left:x=work.Left+dx;break;
            case TaskbarEdge.Right:x=work.Right-width-dx;break;
        }
        x=Math.Clamp(x,work.Left,work.Right-width);y=Math.Clamp(y,work.Top,work.Bottom-height);
        var from=(TaskbarEdge)edge switch {
            TaskbarEdge.Bottom=>new Point(x,y+20),TaskbarEdge.Top=>new Point(x,y-20),
            TaskbarEdge.Left=>new Point(x-20,y+12),_=>new Point(x+20,y+12)
        };
        return new(new Rectangle(x,y,width,height),from,(TaskbarEdge)edge);
    }
}
