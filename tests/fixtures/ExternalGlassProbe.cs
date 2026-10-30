using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public static class ExternalGlassProbe
{
    private delegate bool EnumProc(IntPtr h,IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc f,IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h,int attribute,out int value,int size);
    private static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static IntPtr Find(int pid)
    {
        IntPtr found=IntPtr.Zero;
        EnumWindows((h,p)=>{GetWindowThreadProcessId(h,out uint owner);GetWindowRect(h,out var r);if(owner==pid && IsWindowVisible(h) && r.Right-r.Left>=300 && r.Bottom-r.Top>=400)found=h;return true;},IntPtr.Zero);
        return found;
    }
    private static Bitmap Capture(IntPtr host,string path)
    {
        Assert(GetAncestor(GetForegroundWindow(),2)==host,"The actual EXE is not foreground during optical measurement.");
        GetWindowRect(host,out var r);
        Assert(GetAncestor(WindowFromPoint(new Point((r.Left+r.Right)/2,r.Top+(r.Bottom-r.Top)/3)),2)==host,"Another window obscures the actual EXE.");
        var image=new Bitmap(r.Right-r.Left,r.Bottom-r.Top,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(image))
        {
            g.CopyFromScreen(new Point(r.Left,r.Top),Point.Empty,image.Size);
            int height=(int)Math.Ceiling(136*GetDpiForWindow(host)/96.0);
            g.FillRectangle(Brushes.DimGray,0,image.Height-height,image.Width,height);
        }
        image.Save(path,ImageFormat.Png);return image;
    }
    private static async Task<double> Measure(IntPtr host,Form background,string folder,string name)
    {
        background.BackColor=Color.FromArgb(42,112,198);background.Refresh();await Task.Delay(500);
        using var a=Capture(host,Path.Combine(folder,name+"-cool.png"));
        background.BackColor=Color.FromArgb(201,105,58);background.Refresh();await Task.Delay(500);
        using var b=Capture(host,Path.Combine(folder,name+"-warm.png"));
        double sum=0;int count=0;
        for(int y=180;y<330;y+=5)for(int x=5;x<11;x++){var c=a.GetPixel(x,y);var d=b.GetPixel(x,y);sum+=(Math.Abs(c.R-d.R)+Math.Abs(c.G-d.G)+Math.Abs(c.B-d.B))/3.0;count++;}
        return sum/count;
    }
    private static async Task<string> Evaluate(ClientWebSocket socket,int id,string expression)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(4));
        byte[] request=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id,method="Runtime.evaluate",@params=new{expression,returnByValue=true}}));
        await socket.SendAsync(new ArraySegment<byte>(request),WebSocketMessageType.Text,true,deadline.Token);
        var buffer=new byte[32768];
        while(true)
        {
            using var stream=new MemoryStream();WebSocketReceiveResult message;
            do{message=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),deadline.Token);stream.Write(buffer,0,message.Count);}while(!message.EndOfMessage);
            using var doc=JsonDocument.Parse(stream.ToArray());var root=doc.RootElement;
            if(root.TryGetProperty("id",out var actual) && actual.GetInt32()==id)return root.GetProperty("result").GetProperty("result").GetProperty("value").GetString();
        }
    }
    public static int Observe(int targetPid,string folder,string report)
    {
        using var target=Process.GetProcessById(targetPid);
        Assert(target.ProcessName=="DanmuCompanion","The passive observer must target the existing companion process only.");
        using var background=new Form{Text="Glass diagnosis - existing tray window",ShowInTaskbar=true,TopMost=true,StartPosition=FormStartPosition.Manual,Bounds=Screen.PrimaryScreen.WorkingArea,BackColor=Color.FromArgb(42,112,198),KeyPreview=true};
        background.Controls.Add(new Label{Text="请从托盘打开原来的弹幕面板，保持约 3 秒。\n自动采集像素与焦点，不修改设置。\n按 Esc 可结束测试。",AutoSize=true,Location=new Point(16,16),ForeColor=Color.White,Font=new Font("Microsoft YaHei UI",12)});
        bool complete=false,observed=false;var states=new System.Collections.Generic.List<object>();
        background.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape)background.Close();};
        background.FormClosed+=(_,_)=>{if(!complete)File.WriteAllText(report,JsonSerializer.Serialize(new{result="ABORTED",targetPid,states}));};
        byte[] Pixels(IntPtr host)
        {
            GetWindowRect(host,out var r);using var image=new Bitmap(6,150);
            for(int y=180;y<330;y+=25)Assert(GetAncestor(WindowFromPoint(new Point(r.Left+8,r.Top+y)),2)==host,"The existing panel is obscured; no other-app pixels may be sampled.");
            using(var g=Graphics.FromImage(image))g.CopyFromScreen(new Point(r.Left+5,r.Top+180),Point.Empty,image.Size);
            var bytes=new byte[6*30*3];int index=0;
            for(int y=0;y<150;y+=5)for(int x=0;x<6;x++){var c=image.GetPixel(x,y);bytes[index++]=c.R;bytes[index++]=c.G;bytes[index++]=c.B;}
            return bytes;
        }
        background.Shown+=async(_,_)=>
        {
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="WAITING_FOR_EXISTING_TRAY",pid=Environment.ProcessId,targetPid,deadlineUtc=DateTime.UtcNow.AddMinutes(5)}));
            try
            {
                var until=DateTime.UtcNow.AddMinutes(5);
                while(!background.IsDisposed && DateTime.UtcNow<until && !target.HasExited)
                {
                    IntPtr host=Find(targetPid);
                    if(host!=IntPtr.Zero)
                    {
                        GetWindowRect(host,out var r);DwmGetWindowAttribute(host,38,out int material,4);
                        Assert(background.RectangleToScreen(background.ClientRectangle).Contains(new Rectangle(r.Left+5,r.Top+180,6,150)),"The diagnostic background does not cover the sample strip; no transmission conclusion may be drawn.");
                        states.Add(new{utc=DateTime.UtcNow,foreground=GetAncestor(GetForegroundWindow(),2)==host,material,bounds=r});
                        await Task.Delay(350);
                        background.BackColor=Color.FromArgb(42,112,198);background.Refresh();await Task.Delay(650);bool coolActive=GetAncestor(GetForegroundWindow(),2)==host;var cool=Pixels(host);
                        background.BackColor=Color.FromArgb(201,105,58);background.Refresh();await Task.Delay(650);bool warmActive=GetAncestor(GetForegroundWindow(),2)==host;var warm=Pixels(host);
                        double difference=0;for(int i=0;i<cool.Length;i++)difference+=Math.Abs(cool[i]-warm[i]);difference/=cool.Length;
                        complete=true;observed=true;File.WriteAllText(report,JsonSerializer.Serialize(new{result="OBSERVED_EXISTING_TRAY",targetPid,difference,coolActive,warmActive,states,foregroundAfter=GetAncestor(GetForegroundWindow(),2)==host},new JsonSerializerOptions{WriteIndented=true}));
                        background.Close();return;
                    }
                    await Task.Delay(100);
                }
                complete=true;File.WriteAllText(report,JsonSerializer.Serialize(new{result="NO_EXISTING_TRAY_OBSERVED",targetPid,states}));background.Close();
            }
            catch(Exception error){complete=true;File.WriteAllText(report,JsonSerializer.Serialize(new{result="FAIL_OBSERVATION",targetPid,failure=error.GetBaseException().Message,states}));background.Close();}
        };
        Application.Run(background);return observed ? 0 : 1;
    }
    public static int Run(string app,string folder,string report)
    {
        Directory.CreateDirectory(Path.Combine(folder,"skin-test"));
        File.WriteAllText(Path.Combine(folder,"skin-test","appearance.json"),"{\"schemaVersion\":1,\"settings\":{\"skin\":\"glass\",\"reduceTransparency\":false}}");
        using var background=new Form{Text="External desktop - native EXE material test",FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=true,TopMost=true,StartPosition=FormStartPosition.Manual,Bounds=Screen.PrimaryScreen.WorkingArea,BackColor=Color.FromArgb(42,112,198)};
        Process child=null;Exception failure=null;double before=-1,after=-1;string dom="";
        background.Shown+=async(_,_)=>
        {
            try
            {
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
                var info=new ProcessStartInfo(Path.Combine(app,"DanmuCompanion.exe")){UseShellExecute=false,CreateNoWindow=true};
                info.Environment.Remove("DANMU_API_BASE");
                foreach(string arg in new[]{"--open","--profile","skin-test","--data-root",folder,"--debug-port",port.ToString(),"--smoke-seconds","25","--result",Path.Combine(folder,"exe-smoke.json")})info.ArgumentList.Add(arg);
                child=Process.Start(info);
                File.WriteAllText(report,JsonSerializer.Serialize(new{result="RUNNING",pid=Environment.ProcessId,childPid=child.Id,dataRoot=folder}));
                using var http=new HttpClient();string endpoint=null;
                for(int i=0;i<100 && endpoint==null;i++)
                {
                    try{using var doc=JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:"+port+"/json/list"));foreach(var page in doc.RootElement.EnumerateArray())if(page.GetProperty("url").GetString().StartsWith("https://danmu.local/",StringComparison.Ordinal))endpoint=page.GetProperty("webSocketDebuggerUrl").GetString();}catch(HttpRequestException){}
                    if(endpoint==null)await Task.Delay(100);
                }
                Assert(endpoint!=null,"The owned native EXE did not expose its requested local diagnostic endpoint.");
                using var socket=new ClientWebSocket();await socket.ConnectAsync(new Uri(endpoint),CancellationToken.None);
                for(int i=0;i<40;i++)
                {
                    dom=await Evaluate(socket,i+1,"JSON.stringify({skin:document.documentElement.dataset.skin,backdrop:document.documentElement.dataset.backdrop,transparency:document.documentElement.dataset.transparency,body:getComputedStyle(document.body).backgroundColor})");
                    if(dom.Contains("\"skin\":\"glass\"",StringComparison.Ordinal))break;
                    await Task.Delay(100);
                }
                await Task.Delay(500);IntPtr host=Find(child.Id);Assert(host!=IntPtr.Zero,"The native EXE has no exposed tray window.");
                before=await Measure(host,background,folder,"exe-before");
                await Evaluate(socket,100,"window.chrome.webview.postMessage({v:1,id:'external-glass-reapply',type:'set-appearance',payload:{skin:'glass',reduceTransparency:false}});'sent'");
                await Task.Delay(500);after=await Measure(host,background,folder,"exe-reapplied");
                File.WriteAllText(report,JsonSerializer.Serialize(new{result=before>3 ? "PASS_EXTERNAL_GLASS" : "FAIL_EXTERNAL_GLASS",pid=Environment.ProcessId,childPid=child.Id,before,after,dom,applicationRoot=app,dataRoot=folder},new JsonSerializerOptions{WriteIndented=true}));
                await child.WaitForExitAsync();
            }
            catch(Exception error){failure=error;File.WriteAllText(report,JsonSerializer.Serialize(new{result="FAIL",failure=error.GetBaseException().Message,before,after,dom,dataRoot=folder}));if(child!=null && !child.HasExited)await child.WaitForExitAsync();}
            finally{background.Close();}
        };
        Application.Run(background);child?.Dispose();return failure==null && before>3 ? 0 : 1;
    }
}
