using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

public sealed record ColdGlassEvidence(double ColdNormal,double ColdTopmost,double ReappliedTopmost,double ReappliedNormal,bool InitiallyHidden);
public static class ColdGlassProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    private static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static void Open(Form host)
    {
        host.GetType().GetMethod("OpenWorkbench").Invoke(host,null);
        object flyout=host.GetType().GetField("flyout",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(host);
        ((System.Windows.Forms.Timer)flyout.GetType().GetField("animation",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(flyout)).Stop();
        var area=Screen.PrimaryScreen.WorkingArea;
        host.Location=new Point(area.Left+120,area.Top+120);
        host.Activate();
    }
    private static Bitmap Capture(Form host,string folder,string name)
    {
        Assert(GetAncestor(GetForegroundWindow(),2)==host.Handle,"The cold-start optical host must actually own foreground input.");
        Assert(GetAncestor(WindowFromPoint(host.PointToScreen(new Point(host.Width/2,host.Height/3))),2)==host.Handle,"Another app obscures the owned cold-start host.");
        var image=new Bitmap(host.Width,host.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(host.PointToScreen(Point.Empty),Point.Empty,host.ClientSize);
            int height=(int)Math.Ceiling(136*host.DeviceDpi/96.0);
            graphics.FillRectangle(Brushes.DimGray,0,image.Height-height,image.Width,height);
        }
        image.Save(Path.Combine(folder,name+".png"),ImageFormat.Png);return image;
    }
    private static async Task<double> Measure(Form host,Form background,string folder,string name)
    {
        background.BackColor=Color.FromArgb(42,112,198);background.Refresh();await Task.Delay(500);
        using var first=Capture(host,folder,name+"-cool");
        background.BackColor=Color.FromArgb(201,105,58);background.Refresh();await Task.Delay(500);
        using var second=Capture(host,folder,name+"-warm");
        double sum=0;int count=0;
        for(int y=180;y<330;y+=5)for(int x=5;x<11;x++)
        {
            var a=first.GetPixel(x,y);var b=second.GetPixel(x,y);
            sum+=(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B))/3.0;count++;
        }
        return sum/count;
    }
    public static async Task<ColdGlassEvidence> RunAsync(Form host,Form background,string folder)
    {
        bool hidden=!host.Visible;
        Assert(hidden,"This regression must start from a loaded, hidden, saved-glass host.");
        Open(host);await Task.Delay(500);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var controller=(CoreWebView2Controller)host.GetType().GetField("controller",flags).GetValue(host);
        var core=controller.CoreWebView2;
        string skin=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.documentElement.dataset.skin"));
        Assert(skin=="glass","The initial saved glass must be restored without any skin command.");
        double coldNormal=await Measure(host,background,folder,"cold-normal");
        background.TopMost=true;Open(host);await Task.Delay(300);
        double coldTopmost=await Measure(host,background,folder,"cold-topmost");
        await core.ExecuteScriptAsync("window.chrome.webview.postMessage({v:1,id:'cold-glass-reapply',type:'set-appearance',payload:{skin:'glass',reduceTransparency:false}})");
        await Task.Delay(600);
        double reappliedTopmost=await Measure(host,background,folder,"reapplied-topmost");
        background.TopMost=false;Open(host);await Task.Delay(300);
        double reappliedNormal=await Measure(host,background,folder,"reapplied-normal");
        return new(coldNormal,coldTopmost,reappliedTopmost,reappliedNormal,hidden);
    }
}
