using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

public sealed record TrayContentEvidence(int Width,int Height,double DarkFraction);

public static class TrayContentProbe
{
    public static async Task<TrayContentEvidence> CaptureAsync(Form host,string path)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var controller=(CoreWebView2Controller)host.GetType().GetField("controller",flags).GetValue(host);
        var surface=(Panel)host.GetType().GetField("surface",flags).GetValue(host);
        if(!host.Visible || host.WindowState==FormWindowState.Minimized || !controller.IsVisible ||
            controller.ParentWindow!=surface.Handle || controller.Bounds!=surface.ClientRectangle)
            throw new InvalidOperationException("The visible workbench is not presenting its current browser surface.");
        string raw=await controller.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify({visible:document.visibilityState==='visible',shell:!!document.querySelector('#root>.app-shell'),header:!!document.querySelector('.app-header'),width:innerWidth,height:innerHeight})");
        using var document=JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw));
        var dom=document.RootElement;
        if(!dom.GetProperty("visible").GetBoolean() || !dom.GetProperty("shell").GetBoolean() ||
            !dom.GetProperty("header").GetBoolean() || dom.GetProperty("width").GetInt32()<300 || dom.GetProperty("height").GetInt32()<300)
            throw new InvalidOperationException("The actual browser document has not become visible and laid out.");
        using(var file=File.Create(path))
            await controller.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
        using var image=new Bitmap(path);
        int samples=0,dark=0;
        for(int y=0;y<image.Height;y+=8)
            for(int x=0;x<image.Width;x+=8)
            {
                var pixel=image.GetPixel(x,y);samples++;
                if(pixel.A>200 && pixel.R<90 && pixel.G<90 && pixel.B<90)dark++;
            }
        double fraction=(double)dark/samples;
        if(fraction<.02)throw new InvalidOperationException("The browser frame is blank or missing the workbench's dark playback strip.");
        return new(image.Width,image.Height,fraction);
    }
}
