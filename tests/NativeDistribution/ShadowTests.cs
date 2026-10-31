using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Popcorn;

internal static class ShadowTests
{
    private static void Check(bool value,string message)=>DistributionTests.Check(value,message);
    private static Sprite Current(OverlaySurface surface)
    {
        var store=(SpriteStore)typeof(OverlaySurface).GetField("shapes",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(surface);
        var entries=(Dictionary<string,Sprite>)typeof(SpriteStore).GetField("entries",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(store);
        return entries.Values.Single(sprite=>sprite.References>0);
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var pixels=new byte[image.PixelWidth*image.PixelHeight*4];
        image.CopyPixels(pixels,image.PixelWidth*4,0);return pixels;
    }
    private static long Darkness(BitmapSource image)
    {
        var pixels=Pixels(image);long darkness=0;
        for(int i=0;i<pixels.Length;i+=4)darkness+=pixels[i+3]-pixels[i+2];
        return darkness;
    }
    private static void CheckEdges(BitmapSource image)
    {
        var pixels=Pixels(image);
        for(int y=0;y<image.PixelHeight;y++)for(int x=0;x<image.PixelWidth;x++)
            if(x==0 || y==0 || x==image.PixelWidth-1 || y==image.PixelHeight-1)
                Check(pixels[(y*image.PixelWidth+x)*4+3]<=1,"Shadow must fade before the reserved bitmap boundary, not be clipped.");
    }
    private static OverlaySurface Surface(double strength,double outline=0,bool scaled=false)
    {
        var surface=new OverlaySurface();
        surface.Measure(new Size(1920,1080));surface.Arrange(new Rect(0,0,1920,1080));
        surface.Configure(new(){FontSize=32,ShadowStrength=strength,OutlineWidth=outline,ScaleWithWindow=scaled});
        surface.Load(new[]{new Comment(10000,1,0xffffff,"\u5f39\u5e55\u9634\u5f71 Shadow")});
        surface.UpdateClock(10000,false,Monotonic.Now,1,false,0);surface.Frame();
        Check(surface.Snapshot(false).ActiveCount==1,"Shadow fixture must use an admitted sprite from the actual overlay surface.");
        return surface;
    }
    internal static void Run()
    {
        Check(new DanmakuSettings().ShadowStrength==.45,"The default should add a restrained shadow, independent of the zero-outline default.");
        foreach(double strength in new[]{0,.25,1})new DanmakuSettings{ShadowStrength=strength}.Validate();
        foreach(double strength in new[]{-.01,1.01,double.NaN,double.PositiveInfinity})
        {
            bool rejected=false;try{new DanmakuSettings{ShadowStrength=strength}.Validate();}catch(ArgumentException){rejected=true;}
            Check(rejected,"Invalid shadow intensity must be rejected by the native settings validator.");
        }
        string path=Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,"{\"schemaVersion\":1,\"settings\":{\"fontSize\":40,\"outlineWidth\":0}}");
            var legacy=SettingsPreferences.Load(path);
            Check(legacy.Notice=="" && legacy.Settings.FontSize==40 && legacy.Settings.ShadowStrength==.45,"Existing preferences must migrate without losing user values.");
            foreach(double strength in new[]{0,.75,1})
            {
                File.WriteAllText(path,SettingsPreferences.Serialize(new(){ShadowStrength=strength,OutlineWidth=1.5}));
                var saved=SettingsPreferences.Load(path);
                Check(saved.Notice=="" && saved.Settings.ShadowStrength==strength && saved.Settings.OutlineWidth==1.5,"Shadow and outline must persist independently, including disabled shadow.");
            }
        }
        finally{File.Delete(path);}
        var surfaces=new[]{Surface(0),Surface(.25),Surface(.9),Surface(.9,1.5),Surface(.9,0,true),Surface(1)};
        try
        {
            var images=surfaces.Select(surface=>Current(surface).Image).ToArray();
            Check(Darkness(images[0])==0,"Zero strength must produce no black shadow when outline is off.");
            long low=Darkness(images[1]),high=Darkness(images[2]);
            Check(low>100 && high>low*2,"Actual native shadow pixels must increase with the strength setting.");
            Check(images[1].PixelWidth==images[2].PixelWidth && images[1].PixelHeight==images[2].PixelHeight,"Intensity changes must not change the blur footprint.");
            Check(Darkness(images[3])>high,"Text outline must still render in addition to shadow.");
            Check(images[4].PixelHeight>images[2].PixelHeight,"Shadow padding must scale with the video window.");
            Check(Darkness(images[5])>high,"Maximum intensity must continue to increase the actual shadow pixels without clipping.");
            foreach(var image in images)CheckEdges(image);
            var surface=surfaces[1];var before=surface.Snapshot(false);
            surface.Configure(new(){FontSize=32,ShadowStrength=.9});surface.Frame();
            var after=surface.Snapshot(false);
            Check(after.ActiveCount==1 && after.SpritesCreated==before.SpritesCreated+1 && Darkness(Current(surface).Image)>low*2,"Live intensity edits must rebuild the cached image without losing active content.");
            surface.Configure(new(){FontSize=32,ShadowStrength=.9,Opacity=.4});surface.Frame();
            Check(surface.Snapshot(false).SpritesCreated==after.SpritesCreated,"Whole-comment opacity must not rebuild the shadow bitmap.");
            Check(Current(surface).AlphaBrush(.4).Opacity==.4,"The shared sprite alpha must apply to both text and shadow.");
            var oldImage=Current(surface).Image;
            surface.Configure(new(){FontSize=32/1.5,ShadowStrength=.9,ScaleWithWindow=true});surface.Frame();
            Check(surface.Snapshot(false).SpritesCreated==after.SpritesCreated+1 && Current(surface).Image.PixelHeight>oldImage.PixelHeight,
                "Changing video scale with the same effective font must not reuse a sprite with the wrong shadow radius.");
            foreach(var item in surfaces)Check(item.Snapshot(false).SpriteBytes<=SpriteLimits.MaxResidentBytes,"Shadow pixels must remain inside the aggregate sprite budget.");
            string output=Path.GetFullPath("artifacts/shadow-breathing/shadow-native.png");Directory.CreateDirectory(Path.GetDirectoryName(output));
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(205,212,218)),null,new Rect(0,0,760,240));
                for(int i=0;i<3;i++)dc.DrawImage(images[i],new Rect(30,20+i*70,images[i].Width,images[i].Height));
            }
            var bitmap=new RenderTargetBitmap(760,240,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(output))encoder.Save(stream);
            Console.WriteLine($"PASS: actual native sprites, 0/25/90% shadow darkness 0/{low}/{high}, persistence, live rebuild, opacity, outline, scaling and padding.");
        }
        finally{foreach(var surface in surfaces)surface.Detach();}
    }
}
