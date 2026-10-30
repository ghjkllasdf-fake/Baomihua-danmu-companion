using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

public sealed record SkinRuntimeEvidence(string Backdrop,bool DesktopVisualVerified,double GlassPixelDistance,double SolidPixelDistance,double FrostedStripeStep,double BlackBackdropContrast,double ReopenedGlassPixelDistance,string[] Skins,int Width,int Height);
public static class SkinRuntimeProbe
{
    private static int sequence;
    private static bool opticalCapture;
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static T Value<T>(Form host,string name)
    {
        object state=host.GetType().GetProperty("Appearance").GetValue(host);
        return (T)state.GetType().GetProperty(name).GetValue(state);
    }
    private static void Call(Form host,string method)=>host.GetType().GetMethod(method).Invoke(host,null);
    private static async Task<JsonElement> Send(CoreWebView2 core,string type,object payload)
    {
        string id="skin-runtime-"+(++sequence);
        string message=JsonSerializer.Serialize(new {v=1,id,type,payload});
        await core.ExecuteScriptAsync("(()=>{window.__skinReply=null;const b=window.chrome.webview;const id="+JsonSerializer.Serialize(id)+
            ";function receive(e){if(e.data?.id!==id)return;b.removeEventListener('message',receive);window.__skinReply=e.data;}b.addEventListener('message',receive);b.postMessage("+message+");return true;})()");
        for(int i=0;i<160;i++)
        {
            string value=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(window.__skinReply)"));
            if(value!=null && value!="null"){using var doc=JsonDocument.Parse(value);return doc.RootElement.Clone();}
            await Task.Delay(50);
        }
        throw new TimeoutException("The owned appearance command did not return.");
    }
    private static void Assert(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static async Task Apply(Form host,CoreWebView2 core,string skin,bool reduce)
    {
        var response=await Send(core,"set-appearance",new {skin,reduceTransparency=reduce});
        Assert(response.GetProperty("ok").GetBoolean(),"The actual host rejected a bundled skin.");
        Assert(Value<string>(host,"Skin")==skin && Value<bool>(host,"ReduceTransparency")==reduce,"Native appearance was not committed.");
        for(int i=0;i<60;i++)
        {
            string actual=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("[document.documentElement.dataset.skin,document.documentElement.dataset.transparency,document.documentElement.dataset.backdrop].join('|')"));
            if(actual==skin+"|"+(reduce ? "reduced" : "regular")+"|"+Value<string>(host,"Backdrop")){await Task.Delay(350);return;}
            await Task.Delay(40);
        }
        throw new InvalidOperationException("The actual React document did not observe the native skin.");
    }
    private static Bitmap Capture(Form host,string path)
    {
        var point=host.PointToScreen(new Point(host.ClientSize.Width/2,host.ClientSize.Height/3));
        // Never capture another app when this session cannot expose owned windows.
        if(GetAncestor(WindowFromPoint(point),2)!=host.Handle)return null;
        File.WriteAllText(Path.ChangeExtension(path,"json"),JsonSerializer.Serialize(new{Host=host.Handle.ToInt64(),Foreground=GetAncestor(GetForegroundWindow(),2).ToInt64(),host.Visible}));
        if(opticalCapture)Assert(GetAncestor(GetForegroundWindow(),2)==host.Handle,"The owned optical test lost foreground activation; Windows desktop Acrylic intentionally falls back while inactive.");
        var image=new Bitmap(host.Width,host.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(host.PointToScreen(Point.Empty),Point.Empty,host.ClientSize);
            // Never retain a user's detected playback title in diagnostic images.
            int height=Math.Min((int)Math.Ceiling(136*host.DeviceDpi/96.0),image.Height);
            graphics.FillRectangle(Brushes.DimGray,0,image.Height-height,image.Width,height);
            using var font=new Font("Segoe UI",10);
            graphics.DrawString("Native material test - playback metadata masked",font,Brushes.White,8,image.Height-height+8);
        }
        image.Save(path,ImageFormat.Png);return image;
    }
    private static double Distance(Bitmap first,Bitmap second)
    {
        double sum=0;int count=0;
        // A content-free strip samples only the window material, never text or inputs.
        for(int y=170;y<Math.Min(330,first.Height-136);y+=5)
            for(int x=5;x<11;x++)
            {
                Color a=first.GetPixel(x,y),b=second.GetPixel(x,y);
                sum+=(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B))/3.0;count++;
            }
        return count==0 ? 0 : sum/count;
    }
    private static double Luminance(Color colour)
    {
        double Linear(byte channel){double value=channel/255.0;return value<=0.04045 ? value/12.92 : Math.Pow((value+0.055)/1.055,2.4);}
        return 0.2126*Linear(colour.R)+0.7152*Linear(colour.G)+0.0722*Linear(colour.B);
    }
    private static async Task<double> TextContrast(CoreWebView2 core,Bitmap image)
    {
        string json=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify((()=>{const r=document.querySelector('.panel-main').getBoundingClientRect(),s=getComputedStyle(document.documentElement);return {x:r.x+3,top:r.top+8,bottom:r.bottom-8,width:innerWidth,ink:s.getPropertyValue('--ink').trim(),muted:s.getPropertyValue('--muted').trim()};})())"));
        using var doc=JsonDocument.Parse(json);var bounds=doc.RootElement;
        double scale=image.Width/bounds.GetProperty("width").GetDouble(),minimum=21;
        foreach(string role in new[]{"ink","muted"})
        {
            double text=Luminance(ColorTranslator.FromHtml(bounds.GetProperty(role).GetString()));
            int bottom=Math.Min((int)(bounds.GetProperty("bottom").GetDouble()*scale),image.Height-(int)Math.Ceiling(136*scale));
            for(int y=(int)(bounds.GetProperty("top").GetDouble()*scale);y<bottom;y+=5)
            {
                double material=Luminance(image.GetPixel((int)(bounds.GetProperty("x").GetDouble()*scale),y));
                minimum=Math.Min(minimum,(Math.Max(text,material)+0.05)/(Math.Min(text,material)+0.05));
            }
        }
        return minimum;
    }
    private static async Task CaptureBrowser(Form host,CoreWebView2 core,string path)
    {
        using var buffer=new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,buffer);
        buffer.Position=0;using var image=new Bitmap(buffer);using var graphics=Graphics.FromImage(image);
        int height=Math.Min((int)Math.Ceiling(136*host.DeviceDpi/96.0),image.Height);
        graphics.FillRectangle(Brushes.DimGray,0,image.Height-height,image.Width,height);
        graphics.DrawString("Native WebView - playback metadata masked",SystemFonts.DefaultFont,Brushes.White,8,image.Height-height+8);
        image.Save(path,ImageFormat.Png);
    }
    private static void CaptureScene(Form host,Form background,string folder,string name)
    {
        Assert(GetAncestor(GetForegroundWindow(),2)==host.Handle,"The native scene requires an active tray window, not the OS inactive fallback.");
        int margin=(int)Math.Ceiling(32*host.DeviceDpi/96.0);
        var area=new Rectangle(host.Left-margin,host.Top-margin,host.Width+margin*4,host.Height+margin*2);
        Assert(background.Bounds.Contains(area),"The native scene must stay inside the owned backdrop.");
        for(int y=area.Top+4;y<area.Bottom;y+=24)
            for(int x=area.Left+4;x<area.Right;x+=24)
            {
                IntPtr owner=GetAncestor(WindowFromPoint(new Point(x,y)),2);
                Assert(owner==host.Handle || owner==background.Handle,"Another app obscures the owned native scene; no screenshot may be retained.");
            }
        using var image=new Bitmap(area.Width,area.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(area.Location,Point.Empty,area.Size);
            int height=(int)Math.Ceiling(136*host.DeviceDpi/96.0);
            graphics.FillRectangle(Brushes.DimGray,margin,margin+host.Height-height,host.Width,height);
            graphics.DrawString("Native desktop capture / playback metadata masked",SystemFonts.DefaultFont,Brushes.White,margin+8,margin+host.Height-height+8);
        }
        image.Save(Path.Combine(folder,name+"-desktop-scene.png"),ImageFormat.Png);
    }
    public static async Task<SkinRuntimeEvidence> RunAsync(Form host,string folder,string preferenceFile,bool requireDesktopVisual=false)
    {
        opticalCapture=false;
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var controller=(CoreWebView2Controller)host.GetType().GetField("controller",flags).GetValue(host);
        var core=controller.CoreWebView2;
        Assert(!core.Settings.IsGeneralAutofillEnabled && !core.Settings.IsPasswordAutosaveEnabled,"The live skin host must preserve browser privacy settings.");
        core.Settings.IsGeneralAutofillEnabled=true;core.Settings.IsPasswordAutosaveEnabled=true;
        host.GetType().GetMethod("ConfigureBrowserSettings",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{core.Settings});
        Assert(!core.Settings.IsGeneralAutofillEnabled && !core.Settings.IsPasswordAutosaveEnabled,"The actual privacy policy must recover enabled browser settings.");
        using var background=new Form {FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=requireDesktopVisual,Text="Glass material - isolated native test",StartPosition=FormStartPosition.Manual,
            Bounds=Screen.FromHandle(host.Handle).WorkingArea,BackColor=Color.FromArgb(42,112,198)};
        background.Show();Call(host,"OpenWorkbench");await Task.Delay(400);
        var visited=new List<string>();
        foreach(string skin in new[]{"fluent","glass","winui","studio"})
        {
            await Apply(host,core,skin,false);visited.Add(skin);
            Assert(controller.IsVisible && host.Visible,"Switching skin must not hide the visible WebView.");
            await CaptureBrowser(host,core,Path.Combine(folder,"webview-"+skin+".png"));
            using var image=Capture(host,Path.Combine(folder,"native-"+skin+".png"));
            Call(host,"HideWorkbench");Assert(!controller.IsVisible,"Hiding the tray must hide the controller.");
            Call(host,"OpenWorkbench");Assert(controller.IsVisible,"A skin must survive reopening without a white frame.");
        }
        string before=File.ReadAllText(preferenceFile);
        using(var locked=new FileStream(preferenceFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            var response=await Send(core,"set-appearance",new {skin="glass",reduceTransparency=false});
            Assert(!response.GetProperty("ok").GetBoolean() && response.GetProperty("code").GetString()=="appearance-not-applied","Locked save must report not-applied.");
            Assert(Value<string>(host,"Skin")=="studio","Failed save must preserve native appearance.");
        }
        Assert(File.ReadAllText(preferenceFile)==before,"Failed save changed the confirmed file.");
        foreach(object payload in new object[]{new {skin="external",reduceTransparency=false},new {skin="glass",reduceTransparency=false,path="external"},new {skin="glass",reduceTransparency="false"}})
        {
            var response=await Send(core,"set-appearance",payload);
            Assert(!response.GetProperty("ok").GetBoolean(),"Untrusted appearance payload was accepted.");
        }
        await Apply(host,core,"glass",false);
        string backdrop=Value<string>(host,"Backdrop");
        if(requireDesktopVisual)
        {
            // Optical tests must remain exposed while another owned window repaints.
            // Lifecycle dismissal is exercised above, before detaching this handler.
            object flyout=host.GetType().GetField("flyout",flags).GetValue(host);
            var deactivated=flyout.GetType().GetMethod("Deactivated",flags);
            host.Deactivate-=(EventHandler)Delegate.CreateDelegate(typeof(EventHandler),flyout,deactivated);
            ((System.Windows.Forms.Timer)flyout.GetType().GetField("dismiss",flags).GetValue(flyout)).Stop();
            ((System.Windows.Forms.Timer)flyout.GetType().GetField("animation",flags).GetValue(flyout)).Stop();
            var area=Screen.PrimaryScreen.WorkingArea;
            background.Bounds=area;
            var location=new Point(area.Left+(int)(64*host.DeviceDpi/96.0),area.Top+(int)(64*host.DeviceDpi/96.0));
            host.Location=location;
            background.TopMost=true;background.Show();host.TopMost=false;host.TopMost=true;
            Call(host,"OpenWorkbench");host.Location=location;
            await Task.Delay(500);
            using var activate=new Button {Text="Activate test tray",Bounds=new Rectangle(area.Width-420,24,360,60)};
            activate.Click+=(_,_)=>{Call(host,"OpenWorkbench");((System.Windows.Forms.Timer)flyout.GetType().GetField("animation",flags).GetValue(flyout)).Stop();host.Location=location;};
            background.Controls.Add(activate);
            File.WriteAllText(Path.Combine(folder,"awaiting-activation.json"),JsonSerializer.Serialize(new{Host=host.Handle.ToInt64(),Background=background.Handle.ToInt64()}));
            // A real click on this owned test button can activate the process without
            // bypassing Windows' foreground-lock policy or changing the production host.
            for(int i=0;i<600 && GetAncestor(GetForegroundWindow(),2)!=host.Handle;i++)await Task.Delay(50);
            Assert(GetAncestor(GetForegroundWindow(),2)==host.Handle,"Activate the owned test tray before optical acceptance; another app owns foreground input.");
            File.WriteAllText(Path.Combine(folder,"capture-geometry.json"),JsonSerializer.Serialize(new {
                host.Bounds,host.ClientSize,host.Visible,host.TopMost,host.DeviceDpi,Screen=area,
                Target=host.Handle.ToInt64(),AtPoint=GetAncestor(WindowFromPoint(host.PointToScreen(new Point(host.Width/2,host.Height/3))),2).ToInt64()
            }));
            opticalCapture=true;
        }
        background.BackColor=Color.FromArgb(42,112,198);background.Refresh();await Task.Delay(450);
        using var cool=Capture(host,Path.Combine(folder,"glass-cool.png"));
        background.BackColor=Color.FromArgb(201,105,58);background.Refresh();await Task.Delay(450);
        using var warm=Capture(host,Path.Combine(folder,"glass-warm.png"));
        bool glassExposed=cool!=null && warm!=null;
        if(requireDesktopVisual)Assert(glassExposed,"The required native material capture was not exposed; optical verification cannot be skipped.");
        double glassDistance=glassExposed ? Distance(cool,warm) : -1;
        double stripeStep=-1;
        double blackContrast=-1;
        if(requireDesktopVisual)
        {
            using var pattern=new Bitmap(background.Width,background.Height);
            using(var graphics=Graphics.FromImage(pattern))
            {
                graphics.Clear(Color.White);
                for(int y=0;y<pattern.Height;y+=24)graphics.FillRectangle(Brushes.Black,0,y,pattern.Width,12);
            }
            background.BackgroundImage=pattern;background.Refresh();await Task.Delay(650);
            using var stripes=Capture(host,Path.Combine(folder,"glass-stripes.png"));
            Assert(stripes!=null,"The owned striped background was not exposed.");
            stripeStep=0;
            for(int y=180;y<330;y++)
            {
                Color a=stripes.GetPixel(8,y),b=stripes.GetPixel(8,y+1);
                stripeStep=Math.Max(stripeStep,(Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B))/3.0);
            }
            Assert(backdrop=="acrylic","The optical acceptance requires a supported, enabled system backdrop.");
            Assert(stripeStep<6,"The desktop stripes remain sharp: transparency is not proof of frosted glass.");
            background.BackgroundImage=null;
            background.BackColor=Color.Black;background.Refresh();await Task.Delay(500);
            using var black=Capture(host,Path.Combine(folder,"glass-black.png"));
            Assert(black!=null,"The owned black background was not exposed.");
            blackContrast=await TextContrast(core,black);
            File.WriteAllText(Path.Combine(folder,"glass-optical.json"),JsonSerializer.Serialize(new{glassDistance,stripeStep,blackContrast}));
            Assert(blackContrast>=4.5,"Native glass text falls below 4.5:1 on the controlled dark desktop: "+blackContrast.ToString("F2"));
            using var poster=new Bitmap(background.Width,background.Height);
            using(var graphics=Graphics.FromImage(poster))
            {
                graphics.Clear(Color.FromArgb(224,233,238));
                using var teal=new SolidBrush(Color.FromArgb(34,112,129));
                using var sand=new SolidBrush(Color.FromArgb(224,176,112));
                using var blue=new SolidBrush(Color.FromArgb(52,79,128));
                graphics.FillEllipse(teal,host.Left-260,host.Top-170,900,740);
                graphics.FillEllipse(sand,host.Left+170,host.Top+330,1050,840);
                graphics.FillEllipse(blue,host.Left-360,host.Top+700,1000,780);
                using var font=new Font("Segoe UI",48,FontStyle.Bold,GraphicsUnit.Pixel);
                graphics.DrawString("DESKTOP\nBACKGROUND",font,Brushes.White,host.Left+190,host.Top+120);
                using var pen=new Pen(Color.White,8);
                for(int y=host.Top+480;y<host.Bottom-100;y+=32)graphics.DrawLine(pen,host.Left-80,y,host.Right+200,y-140);
            }
            background.BackgroundImage=poster;background.Refresh();await Task.Delay(650);
            CaptureScene(host,background,folder,"glass");
            await Apply(host,core,"glass",true);await Task.Delay(300);
            CaptureScene(host,background,folder,"solid");
            await Apply(host,core,"glass",false);
            background.BackgroundImage=null;
        }
        await Apply(host,core,"glass",true);
        Assert(Value<string>(host,"Backdrop")=="solid" && controller.DefaultBackgroundColor.A==255,"Reduced transparency must restore an opaque host.");
        background.BackColor=Color.FromArgb(42,112,198);background.Refresh();await Task.Delay(450);
        using var solidCool=Capture(host,Path.Combine(folder,"solid-cool.png"));
        background.BackColor=Color.FromArgb(201,105,58);background.Refresh();await Task.Delay(450);
        using var solidWarm=Capture(host,Path.Combine(folder,"solid-warm.png"));
        bool solidExposed=solidCool!=null && solidWarm!=null;
        double solidDistance=solidExposed ? Distance(solidCool,solidWarm) : -1;
        if(solidExposed)Assert(solidDistance<2,"The solid fallback unexpectedly transmits background colours.");
        if(glassExposed)Assert(backdrop!="acrylic" || glassDistance>3,"DWM reported Acrylic but the owned native screenshot did not transmit the controlled backdrop.");
        double reopenedDistance=-1;
        if(requireDesktopVisual)
        {
            Assert(solidExposed,"The required solid-fallback capture was not exposed.");
            await Apply(host,core,"glass",false);
            Point location=host.Location;
            for(int i=0;i<2;i++){Call(host,"HideWorkbench");await Task.Delay(150);Call(host,"OpenWorkbench");host.Location=location;await Task.Delay(350);}
            background.BackColor=Color.FromArgb(42,112,198);background.Refresh();await Task.Delay(450);
            using var reopenedCool=Capture(host,Path.Combine(folder,"reopened-cool.png"));
            background.BackColor=Color.FromArgb(201,105,58);background.Refresh();await Task.Delay(450);
            using var reopenedWarm=Capture(host,Path.Combine(folder,"reopened-warm.png"));
            Assert(reopenedCool!=null && reopenedWarm!=null,"The reopened glass window was not exposed.");
            reopenedDistance=Distance(reopenedCool,reopenedWarm);
            Assert(reopenedDistance>3,"Reopening or toggling solid fallback lost the actual desktop material.");
        }
        await Apply(host,core,"fluent",false);
        Assert(Value<string>(host,"Backdrop")=="solid" && controller.DefaultBackgroundColor.A==255,"Returning to A must remove Acrylic.");
        return new(backdrop,glassExposed && solidExposed,glassDistance,solidDistance,stripeStep,blackContrast,reopenedDistance,visited.ToArray(),host.ClientSize.Width,host.ClientSize.Height);
    }
}
