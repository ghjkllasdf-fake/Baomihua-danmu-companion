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

public sealed record SkinRuntimeEvidence(string Backdrop,bool DesktopVisualVerified,double GlassPixelDistance,double SolidPixelDistance,string[] Skins,int Width,int Height);
public static class SkinRuntimeProbe
{
    private static int sequence;
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
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
            string actual=JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.documentElement.dataset.skin"));
            if(actual==skin){await Task.Delay(350);return;}
            await Task.Delay(40);
        }
        throw new InvalidOperationException("The actual React document did not observe the native skin.");
    }
    private static Bitmap Capture(Form host,string path)
    {
        var point=host.PointToScreen(new Point(host.ClientSize.Width/2,host.ClientSize.Height/3));
        // Never capture another app when this session cannot expose owned windows.
        if(GetAncestor(WindowFromPoint(point),2)!=host.Handle)return null;
        var image=new Bitmap(host.Width,host.Height,PixelFormat.Format32bppArgb);
        using(var graphics=Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(host.PointToScreen(Point.Empty),Point.Empty,host.ClientSize);
            // Never retain a user's detected playback title in diagnostic images.
            int height=Math.Min(136,image.Height);
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
    private static async Task CaptureBrowser(CoreWebView2 core,string path)
    {
        using var buffer=new MemoryStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,buffer);
        buffer.Position=0;using var image=new Bitmap(buffer);using var graphics=Graphics.FromImage(image);
        graphics.FillRectangle(Brushes.DimGray,0,Math.Max(0,image.Height-136),image.Width,136);
        graphics.DrawString("Native WebView - playback metadata masked",SystemFonts.DefaultFont,Brushes.White,8,Math.Max(0,image.Height-128));
        image.Save(path,ImageFormat.Png);
    }
    public static async Task<SkinRuntimeEvidence> RunAsync(Form host,string folder,string preferenceFile)
    {
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var controller=(CoreWebView2Controller)host.GetType().GetField("controller",flags).GetValue(host);
        var core=controller.CoreWebView2;
        Assert(!core.Settings.IsGeneralAutofillEnabled && !core.Settings.IsPasswordAutosaveEnabled,"The live skin host must preserve browser privacy settings.");
        core.Settings.IsGeneralAutofillEnabled=true;core.Settings.IsPasswordAutosaveEnabled=true;
        host.GetType().GetMethod("ConfigureBrowserSettings",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{core.Settings});
        Assert(!core.Settings.IsGeneralAutofillEnabled && !core.Settings.IsPasswordAutosaveEnabled,"The actual privacy policy must recover enabled browser settings.");
        using var background=new Form {FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,
            Bounds=Screen.FromHandle(host.Handle).WorkingArea,BackColor=Color.FromArgb(42,112,198)};
        background.Show();Call(host,"OpenWorkbench");await Task.Delay(400);
        var visited=new List<string>();
        foreach(string skin in new[]{"fluent","glass","winui","studio"})
        {
            await Apply(host,core,skin,false);visited.Add(skin);
            Assert(controller.IsVisible && host.Visible,"Switching skin must not hide the visible WebView.");
            await CaptureBrowser(core,Path.Combine(folder,"webview-"+skin+".png"));
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
        background.BackColor=Color.FromArgb(42,112,198);background.Refresh();await Task.Delay(450);
        using var cool=Capture(host,Path.Combine(folder,"glass-cool.png"));
        background.BackColor=Color.FromArgb(201,105,58);background.Refresh();await Task.Delay(450);
        using var warm=Capture(host,Path.Combine(folder,"glass-warm.png"));
        bool glassExposed=cool!=null && warm!=null;
        double glassDistance=glassExposed ? Distance(cool,warm) : -1;
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
        await Apply(host,core,"fluent",false);
        Assert(Value<string>(host,"Backdrop")=="solid" && controller.DefaultBackgroundColor.A==255,"Returning to A must remove Acrylic.");
        return new(backdrop,glassExposed && solidExposed,glassDistance,solidDistance,visited.ToArray(),host.ClientSize.Width,host.ClientSize.Height);
    }
}
