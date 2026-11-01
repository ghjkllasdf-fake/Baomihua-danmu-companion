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

namespace Popcorn;

// Own native windows and an isolated WebView only; no player, API or OS settings.
internal static class TrayVisualRuntimeTests
{
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    private static readonly JsonSerializerOptions json=new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Capture(Form window,CoreWebView2Controller controller,Rectangle icon,string path)
    {
        Check(controller.IsVisible,"The real browser controller must be visible, not just loaded.");
        Check(GetAncestor(WindowFromPoint(new Point(window.Left+window.Width/2,window.Top+window.Height/2)),2)==window.Handle,
            "The controlled native popup must own the captured pixels.");
        Rectangle crop=Rectangle.Union(window.Bounds,icon);crop.Inflate(24,24);
        using var image=new Bitmap(crop.Width,crop.Height,PixelFormat.Format32bppArgb);
        using var graphics=Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(222,232,240));
        // Copy only our popup and target icon, never surrounding user windows.
        graphics.CopyFromScreen(window.Location,new Point(window.Left-crop.Left,window.Top-crop.Top),window.Size);
        var colours=new HashSet<int>();
        for(int y=window.Top-crop.Top;y<window.Bottom-crop.Top;y+=10)
            for(int x=window.Left-crop.Left;x<window.Right-crop.Left;x+=10)colours.Add(image.GetPixel(x,y).ToArgb());
        Check(colours.Count>=12,"The native popup must contain painted UI, not a blank loaded window.");
        graphics.CopyFromScreen(icon.Location,new Point(icon.Left-crop.Left,icon.Top-crop.Top),icon.Size);
        using var pen=new Pen(Color.FromArgb(58,104,145),2) {DashStyle=System.Drawing.Drawing2D.DashStyle.Dash};
        Point centre=new(icon.Left+icon.Width/2-crop.Left,icon.Top+icon.Height/2-crop.Top);
        Point edge=new(Math.Clamp(centre.X,window.Left-crop.Left,window.Right-crop.Left),
            Math.Clamp(centre.Y,window.Top-crop.Top,window.Bottom-crop.Top));
        graphics.DrawLine(pen,centre,edge);
        image.Save(path,ImageFormat.Png);
    }
    internal static void Run(string root)
    {
        root=Path.GetFullPath(root);
        string output=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","tray-position");
        Directory.CreateDirectory(output);
        string data=Path.Combine(output,"data-"+Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(output,"native.json"),JsonSerializer.Serialize(new{result="RUNNING",pid=Environment.ProcessId},json));
        NativeRuntime.ConfigureBrowserEnvironment(new ApplicationPaths(root,"tray-visual",data));
        SharedBrowserRuntime.ConfigureLoader(root);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        Rectangle board=Screen.PrimaryScreen.WorkingArea;
        using var background=new Form {Text="Tray direction native UI test",FormBorderStyle=FormBorderStyle.None,
            StartPosition=FormStartPosition.Manual,Bounds=board,BackColor=Color.FromArgb(222,232,240),ShowInTaskbar=true,TopMost=true,KeyPreview=true};
        using var window=new Form {Text="Controlled Baomihua popup",FormBorderStyle=FormBorderStyle.None,
            StartPosition=FormStartPosition.Manual,ShowInTaskbar=false};
        using var flyout=new TrayFlyout(window,()=>false,()=>true);
        Point? passedAnchor=null;
        using var tray=new DesktopTray(root,()=>flyout.Open(new Point(board.Right-24,board.Bottom-1)),background.Close,
            (anchor,_)=>passedAnchor=anchor);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var notify=(NotifyIcon)typeof(DesktopTray).GetField("icon",flags).GetValue(tray);
        notify.Text="Baomihua tray positioning test";
        using var artwork=AppIcons.Load(root,true);
        Rectangle marker=Rectangle.Empty;
        background.Paint+=(_,args)=>{if(!marker.IsEmpty)args.Graphics.DrawIcon(artwork,marker);};
        background.KeyDown+=(_,args)=>{if(args.KeyCode==Keys.Escape)background.Close();};
        CoreWebView2Controller controller=null;
        window.VisibleChanged+=(_,_)=>{if(controller!=null)controller.IsVisible=window.Visible;};
        bool ready=false,entered=false,finished=false;Exception failure=null;
        object actual=null;var cases=new List<object>();
        using var watchdog=new Timer {Interval=120000};
        using var pump=new Timer {Interval=80};
        watchdog.Tick+=(_,_)=>{failure=new TimeoutException("The owned tray visual test exceeded 120 seconds.");background.Close();};
        background.Shown+=async (_,_)=>
        {
            try
            {
                var environment=await CoreWebView2Environment.CreateAsync(null,data,SharedBrowserRuntime.Options);
                controller=await environment.CreateCoreWebView2ControllerAsync(window.Handle);
                controller.Bounds=window.ClientRectangle;
                window.ClientSizeChanged+=(_,_)=>controller.Bounds=window.ClientRectangle;
                var core=controller.CoreWebView2;
                var appearance=new{skin="fluent",reduceTransparency=false,revision=0,backdrop="solid",notice=""};
                core.WebMessageReceived+=(_,args)=>
                {
                    using var command=JsonDocument.Parse(args.WebMessageAsJson);
                    string type=command.RootElement.GetProperty("type").GetString();
                    object reply=type switch {
                        "hello"=>new{protocolVersion=1,settings=new DanmakuSettings(),settingsRevision=0,stateSequence=0,apiBase="",apiSaved=false,apiNotice="",
                            capabilities=new[]{"media-context-v1","desktop-lifecycle-v1","tray-flyout-v1","api-persistence-v1","appearance-v1"},appearance},
                        "get-settings"=>new{settings=new DanmakuSettings(),settingsRevision=0},
                        "get-appearance"=>appearance,"get-startup"=>new{enabled=false},_=>null
                    };
                    core.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="response",id=command.RootElement.GetProperty("id").GetString(),ok=reply!=null,data=reply},json));
                };
                core.SetVirtualHostNameToFolderMapping("danmu.local",Path.Combine(root,"ui","dist"),CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationCompleted+=(_,args)=>ready=args.IsSuccess;
                core.Navigate("https://danmu.local/index.html");
            }
            catch(Exception error){failure=error;background.Close();}
        };
        pump.Tick+=async (_,_)=>
        {
            if(entered || !ready || NativeWindows.GetForegroundWindow()!=background.Handle)return;
            entered=true;pump.Stop();
            try
            {
                Rectangle? real=TrayIconLocator.GetBounds(notify);
                if(real is Rectangle icon)
                {
                    Point centre=new(icon.Left+icon.Width/2,icon.Top+icon.Height/2);
                    typeof(DesktopTray).GetField("lastInteraction",flags).SetValue(tray,new Point(board.Left+5,board.Top+5));
                    Check(tray.Anchor==centre,"The real shell icon centre must override an off-centre cached interaction.");
                    typeof(NotifyIcon).GetMethod("OnMouseDown",flags).Invoke(notify,new object[]{new MouseEventArgs(MouseButtons.Left,1,0,0,0)});
                    Check((Point?)typeof(DesktopTray).GetField("lastInteraction",flags).GetValue(tray)==centre,"Mouse-down must capture the icon centre, not the cursor offset.");
                    var captured=new Point(board.Left+board.Width/2,board.Top+board.Height/2);
                    typeof(DesktopTray).GetField("lastInteraction",flags).SetValue(tray,captured);
                    typeof(NotifyIcon).GetMethod("OnMouseClick",flags).Invoke(notify,new object[]{new MouseEventArgs(MouseButtons.Left,1,0,0,0)});
                    Check(passedAnchor==captured,"Tray clicks must retain the mouse-down anchor if a shell overflow popup closes before mouse-up.");
                    flyout.Open(tray.Anchor.Value);await Task.Delay(450);
                    var target=Screen.FromPoint(centre);
                    var expected=TrayPlacement.Compute(target.Bounds,target.WorkingArea,centre,window.Size,(int)Math.Round(8*window.DeviceDpi/96.0));
                    Check(window.Bounds==expected.Bounds,"The live taskbar popup must use the current icon-centred placement.");
                    Capture(window,controller,icon,Path.Combine(output,"actual-taskbar.png"));
                    actual=new{found=true,icon,centre,bounds=window.Bounds,edge=expected.Edge.ToString(),
                        source="Shell-reported tray rectangle; a hidden icon may use the overflow button."};
                    flyout.Hide();background.Activate();
                }
                else actual=new{found=false,reason="Windows did not expose a rectangle for the hidden overflow icon."};
                double scale=window.DeviceDpi/96.0;
                var screen=Rectangle.Inflate(board,-64,-64);
                int bar=(int)Math.Round(40*scale),iconSize=(int)Math.Round(16*scale),gap=(int)Math.Round(8*scale);
                var desired=new Size((int)Math.Round(380*scale),(int)Math.Round(540*scale));
                var placementField=typeof(TrayFlyout).GetField("placement",flags);
                foreach(var edge in new[]{TaskbarEdge.Bottom,TaskbarEdge.Top,TaskbarEdge.Left,TaskbarEdge.Right})
                {
                    Rectangle work=edge switch {
                        TaskbarEdge.Bottom=>new(screen.X,screen.Y,screen.Width,screen.Height-bar),
                        TaskbarEdge.Top=>new(screen.X,screen.Y+bar,screen.Width,screen.Height-bar),
                        TaskbarEdge.Left=>new(screen.X+bar,screen.Y,screen.Width-bar,screen.Height),
                        _=>new(screen.X,screen.Y,screen.Width-bar,screen.Height)
                    };
                    Point anchor=edge switch {
                        TaskbarEdge.Bottom=>new(screen.Left+screen.Width/2,screen.Bottom-bar/2),
                        TaskbarEdge.Top=>new(screen.Left+screen.Width/2,screen.Top+bar/2),
                        TaskbarEdge.Left=>new(screen.Left+bar/2,screen.Top+screen.Height/2),
                        _=>new(screen.Right-bar/2,screen.Top+screen.Height/2)
                    };
                    marker=new Rectangle(anchor.X-iconSize/2,anchor.Y-iconSize/2,iconSize,iconSize);
                    background.Invalidate();background.Update();
                    var next=TrayPlacement.Compute(screen,work,anchor,desired,gap);
                    flyout.Open(anchor);
                    // Inject only monitor geometry; exercise the unchanged real HWND animation.
                    placementField.SetValue(flyout,next);window.Bounds=new Rectangle(next.SlideFrom,next.Bounds.Size);
                    var points=new List<Point>{window.Location};
                    for(int i=0;i<18;i++){await Task.Delay(20);points.Add(window.Location);}
                    Check(window.Bounds==next.Bounds,"The native animation must finish at its computed bounds.");
                    bool vertical=edge is TaskbarEdge.Bottom or TaskbarEdge.Top;
                    int previous=vertical ? points[0].Y : points[0].X;
                    foreach(Point point in points)
                    {
                        Check((vertical ? point.X==next.Bounds.X : point.Y==next.Bounds.Y),"The real HWND must never move diagonally.");
                        int current=vertical ? point.Y : point.X;
                        Check(edge is TaskbarEdge.Bottom or TaskbarEdge.Right ? current<=previous : current>=previous,"The native slide must move toward the screen interior.");
                        previous=current;
                    }
                    await Task.Delay(150);
                    Capture(window,controller,marker,Path.Combine(output,edge.ToString().ToLowerInvariant()+".png"));
                    cases.Add(new{edge=edge.ToString(),anchor,screen,work,bounds=window.Bounds,points,scale});
                    flyout.Hide();background.Activate();
                }
                using(var still=new Form {FormBorderStyle=FormBorderStyle.None,ShowInTaskbar=false})
                using(var noMotion=new TrayFlyout(still,()=>false,()=>false))
                {
                    noMotion.Open(new Point(board.Left+board.Width/2,Screen.PrimaryScreen.Bounds.Bottom-1));
                    var timer=(Timer)typeof(TrayFlyout).GetField("animation",flags).GetValue(noMotion);
                    Check(!timer.Enabled,"Disabled motion must not start an animation timer.");
                    noMotion.Hide();
                }
                finished=true;background.Close();
            }
            catch(Exception error){failure=error;background.Close();}
        };
        pump.Start();watchdog.Start();
        try {Application.Run(background);}
        finally {pump.Stop();watchdog.Stop();flyout.Hide();controller?.Close();}
        File.WriteAllText(Path.Combine(output,"native.json"),JsonSerializer.Serialize(new{
            result=finished && failure==null ? "PASS_NATIVE_TRAY_POSITION" : "FAIL_NATIVE_TRAY_POSITION",
            error=failure?.ToString(),actual,cases,fixture="Four orientations use controlled monitor geometry, not changed OS taskbars."
        },json));
        Check(failure==null && finished,"Native tray visual gate failed: "+failure);
        Console.WriteLine("PASS: real shell anchor (when exposed), four pure-axis native HWND animations, native UI screenshots and no-motion policy.");
        Console.WriteLine("Evidence: "+output);
    }
}
