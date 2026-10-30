using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

public sealed record TrayContentEvidence(int Width,int Height,double HeaderPaintFraction,int HeaderContrastRange);

public static class TrayContentProbe
{
    private static (double Fraction,int Range) VerifyHeader(Bitmap image,Rectangle region)
    {
        int samples=0,painted=0,min=255,max=0;
        var colors=new Dictionary<int,int>();
        for(int y=region.Top;y<region.Bottom;y+=2)
            for(int x=region.Left;x<region.Right;x+=2)
            {
                var pixel=image.GetPixel(x,y);samples++;
                int key=pixel.ToArgb();colors.TryGetValue(key,out int count);colors[key]=count+1;
                int intensity=(pixel.R+pixel.G+pixel.B)/3;
                min=Math.Min(min,intensity);max=Math.Max(max,intensity);
            }
        int most=0,backgroundKey=0;
        foreach(var pair in colors)if(pair.Value>most){most=pair.Value;backgroundKey=pair.Key;}
        var background=Color.FromArgb(backgroundKey);
        for(int y=region.Top;y<region.Bottom;y+=2)
            for(int x=region.Left;x<region.Right;x+=2)
            {
                var pixel=image.GetPixel(x,y);
                int difference=Math.Max(Math.Abs(pixel.R-background.R),Math.Max(Math.Abs(pixel.G-background.G),Math.Abs(pixel.B-background.B)));
                if(pixel.A>200 && difference>=50)painted++;
            }
        double fraction=samples>0 ? (double)painted/samples : 0;
        if(fraction<.02 || max-min<50)
            throw new InvalidOperationException("The browser frame is blank or missing the panel's painted toolbar controls: "+fraction+" / "+(max-min));
        return(fraction,max-min);
    }
    public static void AssertBlankFramesRejected()
    {
        using var image=new Bitmap(64,64);
        foreach(var color in new[]{Color.White,Color.Black})
        {
            using(var drawing=Graphics.FromImage(image))drawing.Clear(color);
            try{VerifyHeader(image,new Rectangle(0,0,64,64));}
            catch(InvalidOperationException){continue;}
            throw new InvalidOperationException("A uniform blank browser frame passed the paint guard.");
        }
    }
    public static async Task<TrayContentEvidence> CaptureAsync(Form host,string path)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var controller=(CoreWebView2Controller)host.GetType().GetField("controller",flags).GetValue(host);
        var surface=(Panel)host.GetType().GetField("surface",flags).GetValue(host);
        if(!host.Visible || host.WindowState==FormWindowState.Minimized || !controller.IsVisible ||
            controller.ParentWindow!=surface.Handle || controller.Bounds!=surface.ClientRectangle)
            throw new InvalidOperationException("The visible workbench is not presenting its current browser surface.");
        string raw=await controller.CoreWebView2.ExecuteScriptAsync(
            "JSON.stringify({visible:document.visibilityState==='visible',shell:!!document.querySelector('#root>.mini-app'),header:document.querySelector('.mini-toolbar')?.getBoundingClientRect().toJSON(),sliders:document.querySelectorAll('.display-view input[type=range]').length,width:innerWidth,height:innerHeight})");
        using var document=JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw));
        var dom=document.RootElement;
        if(!dom.GetProperty("visible").GetBoolean() || !dom.GetProperty("shell").GetBoolean() ||
            !dom.TryGetProperty("header",out var header) || header.GetProperty("height").GetDouble()<=0 || dom.GetProperty("sliders").GetInt32()!=4 ||
            dom.GetProperty("width").GetInt32()<300 || dom.GetProperty("height").GetInt32()<300)
            throw new InvalidOperationException("The actual browser document has not become visible and laid out: "+raw);
        using(var file=File.Create(path))
            await controller.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);
        using var image=new Bitmap(path);
        // The current compact toolbar replaces the retired full-width dark
        // playback strip. Its blue switches and ink must differ from the actual
        // background over at least the original 2% gate; uniform white/black fail.
        double sx=(double)image.Width/dom.GetProperty("width").GetInt32(),sy=(double)image.Height/dom.GetProperty("height").GetInt32();
        var region=Rectangle.Intersect(new Rectangle((int)Math.Floor(header.GetProperty("x").GetDouble()*sx),
            (int)Math.Floor(header.GetProperty("y").GetDouble()*sy),(int)Math.Ceiling(header.GetProperty("width").GetDouble()*sx),
            (int)Math.Ceiling(header.GetProperty("height").GetDouble()*sy)),new Rectangle(0,0,image.Width,image.Height));
        var paint=VerifyHeader(image,region);
        return new(image.Width,image.Height,paint.Fraction,paint.Range);
    }
}
