using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Popcorn;

// Explicit GUI gate: real production host + real WebView, isolated user data.
internal static class TrayHostRuntimeTests
{
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task<JsonElement> Inspect(CoreWebView2 core,string expression)
    {
        string value=await core.ExecuteScriptAsync("JSON.stringify("+expression+")");
        using var document=JsonDocument.Parse(JsonSerializer.Deserialize<string>(value));return document.RootElement.Clone();
    }
    internal static void Run(string root)
    {
        root=Path.GetFullPath(root);
        string evidence=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","tray-host-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var paths=new ApplicationPaths(root,"flyout-runtime",Path.Combine(evidence,"data"));
        NativeRuntime.ValidateApplication(paths);NativeRuntime.ConfigureBrowserEnvironment(paths);SharedBrowserRuntime.ConfigureLoader(root);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var host=new DesktopWindow(root,"","flyout-runtime",paths.DataRoot) {SuppressErrorDialogs=true};
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var controllerField=typeof(DesktopWindow).GetField("controller",flags);
        var tray=typeof(DesktopWindow).GetField("tray",flags).GetValue(host);
        var icon=(NotifyIcon)tray.GetType().GetField("icon",flags).GetValue(tray);
        using var activation=new Form {Text="Tray mini-app runtime verification",ClientSize=new Size(340,90),
            StartPosition=FormStartPosition.CenterScreen,MinimizeBox=false,MaximizeBox=false};
        activation.Controls.Add(new Label {Text="Controlled UI verification. Waiting for foreground...",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter});
        using var watchdog=new Timer {Interval=45000};
        using var pump=new Timer {Interval=80};
        Exception failure=null;bool entered=false,finished=false;
        watchdog.Tick+=(_,_)=>{watchdog.Stop();failure=new TimeoutException("Tray host runtime gate exceeded 45 seconds.");host.RequestExit();};
        pump.Tick+=async (_,_)=>
        {
            if(entered || !host.Ready || NativeWindows.GetForegroundWindow()!=activation.Handle)return;
            entered=true;pump.Stop();
            activation.Hide();
            try
            {
                var controller=(CoreWebView2Controller)controllerField.GetValue(host);
                Check(!host.Visible && !controller.IsVisible && icon.Visible,"Warm startup must stay in the tray with a hidden real browser.");
                host.OpenWorkbench();var slideFrom=host.Location;await Task.Delay(350);
                Check(host.Visible && NativeWindows.IsWindowVisible(host.Handle) && controller.IsVisible,"Opening the flyout must show its actual browser, not just a loaded DOM.");
                Check(!host.ShowInTaskbar && host.FormBorderStyle==FormBorderStyle.None && Screen.FromHandle(host.Handle).WorkingArea.Contains(host.Bounds),"The flyout must be borderless, off the taskbar and wholly in its monitor's work area.");
                Check(host.Width<=Math.Ceiling(390*host.DeviceDpi/96.0),"The host must not reopen the former full-size workbench.");
                if(SystemInformation.IsMenuAnimationEnabled && NativeWindows.ClientAreaAnimations)Check(slideFrom!=host.Location,"The real newly opened flyout must complete its slide-in.");
                var dom=await Inspect(controller.CoreWebView2,"({panel:!!document.querySelector('.mini-app'),toggles:document.querySelectorAll('.mini-toolbar input[role=switch]').length,preview:document.querySelectorAll('.preview').length,width:innerWidth,height:innerHeight})");
                Check(dom.GetProperty("panel").GetBoolean() && dom.GetProperty("toggles").GetInt32()==2 && dom.GetProperty("preview").GetInt32()==0 && dom.GetProperty("width").GetInt32()>=300,"The native host must paint the compact two-toggle UI without a preview.");
                using(var file=File.Create(Path.Combine(evidence,"actual-native-panel.png")))await controller.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);

                using(var stillWindow=new Form {Owner=host,ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None})
                using(var stillFlyout=new TrayFlyout(stillWindow,()=>false,()=>false))
                {
                    stillFlyout.Open(new Point(Screen.PrimaryScreen.Bounds.Right-24,Screen.PrimaryScreen.Bounds.Bottom-1));
                    var animation=(Timer)typeof(TrayFlyout).GetField("animation",flags).GetValue(stillFlyout);
                    Check(!animation.Enabled,"A disabled client-area animation policy must open the native panel without a sliding timer.");
                    stillFlyout.Hide();host.Activate();
                }

                using(var owned=new Form {Text="Owned native flyout popup",StartPosition=FormStartPosition.Manual,Size=new Size(160,80),Location=host.Location+new Size(30,60),ShowInTaskbar=false})
                {
                    owned.Show(host);owned.Activate();
                    Console.WriteLine($"Owned popup: host={host.Handle}, popup={owned.Handle}, owner={GetWindow(owned.Handle,4)}, foreground={NativeWindows.GetForegroundWindow()}, rootOwner={GetAncestor(NativeWindows.GetForegroundWindow(),3)}");
                    await Task.Delay(250);
                    Check(NativeWindows.GetForegroundWindow()==owned.Handle,"The GUI fixture could not establish its owned popup as foreground; this is not a product occlusion result.");
                    Check(host.Visible,"An owned native popup must not dismiss its flyout.");owned.Close();host.Activate();
                }
                await controller.CoreWebView2.ExecuteScriptAsync("document.querySelector('.manual-source-link').click()");
                var sources=await Inspect(controller.CoreWebView2,"!document.querySelector('.source-view').hidden");
                Check(sources.GetBoolean(),"Manual source selection must use the same actual flyout.");
                await controller.CoreWebView2.ExecuteScriptAsync("document.querySelector('.panel-navigation button').click()");
                using(var listener=new TcpListener(IPAddress.Loopback,0))
                {
                    listener.Start();
                    string api="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;
                    var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var server=Task.Run(async ()=>
                    {
                        using var client=await listener.AcceptTcpClientAsync();
                        var bytes=new byte[4096];int read=await client.GetStream().ReadAsync(bytes);
                        if(read<=0)throw new Exception("The slow reload fixture never received HTTP bytes.");
                        received.SetResult();await release.Task;
                    });
                    try
                    {
                        await controller.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.postMessage({v:1,id:'runtime-config',type:'configure-api',payload:{apiBase:"+JsonSerializer.Serialize(api)+"}})");
                        await Task.Delay(50);
                        await controller.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.postMessage({v:1,id:'runtime-slow',type:'search',payload:{apiBase:"+JsonSerializer.Serialize(api)+",value:'controlled slow request'}})");
                        await received.Task.WaitAsync(TimeSpan.FromSeconds(4));
                        var overlay=(OverlayHost)typeof(DesktopWindow).GetField("overlay",flags).GetValue(host);
                        Check(overlay.NoticeSnapshot().Visible,"A real in-flight HTTP search must publish its native video notice.");
                        controller.CoreWebView2.Reload();await Task.Delay(200);
                        Check(!overlay.NoticeSnapshot().Visible,"A real WebView document reload must clear the cancelled manual notice.");
                        for(int i=0;i<80 && !host.Ready;i++)await Task.Delay(50);
                        Check(host.Ready,"The real WebView must safely handshake again after reload.");
                    }
                    finally {release.TrySetResult();listener.Stop();await server.WaitAsync(TimeSpan.FromSeconds(3));}
                }
                await controller.CoreWebView2.ExecuteScriptAsync("window.chrome.webview.addEventListener('message',e=>{if(e.data?.type==='response' && e.data?.data?.hidden===true)document.documentElement.dataset.hideAck='yes'});document.querySelector('.hide-panel').click()");
                await Task.Delay(180);
                Check(!host.Visible && !controller.IsVisible && !host.IsDisposed && icon.Visible,"Typed hide must retain the tray and hide WebView without exiting.");
                Check((await Inspect(controller.CoreWebView2,"document.documentElement.dataset.hideAck")).GetString()=="yes","The actual React hide request must receive its native acknowledgement while hidden.");
                host.OpenWorkbench();await Task.Delay(250);
                Check(host.Visible && controller.IsVisible,"A hidden flyout must reopen with a visible controller.");
                await controller.CoreWebView2.ExecuteScriptAsync("document.querySelector('#density').focus();document.querySelector('#density').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}))");
                await Task.Delay(180);
                Check(!host.Visible && !controller.IsVisible,"Escape from a focused closed select must hide the panel, not exit or do nothing.");
                host.OpenWorkbench();await Task.Delay(250);
                host.Close();await Task.Delay(100);
                Check(!host.IsDisposed && !host.Visible,"Window close must still mean minimize to tray, not exit.");
                host.OpenWorkbench();await Task.Delay(250);
                using(var outside=new Form {Text="Tray mini-app outside-click verification",ShowInTaskbar=true})
                {
                    outside.Show();outside.Activate();
                    Console.WriteLine("Waiting for the controlled outside window to become foreground.");
                    for(int i=0;i<150 && NativeWindows.GetForegroundWindow()!=outside.Handle;i++)await Task.Delay(100);
                    Check(NativeWindows.GetForegroundWindow()==outside.Handle,"The GUI fixture could not establish its independent window as foreground.");
                    await Task.Delay(350);
                    Check(!host.Visible && !controller.IsVisible,"Activating an independent window must dismiss the flyout.");outside.Close();
                }
                finished=true;watchdog.Stop();host.RequestExit();
            }
            catch(Exception error){failure=error;watchdog.Stop();host.RequestExit();}
        };
        activation.Show();activation.Activate();pump.Start();watchdog.Start();Application.Run(host);
        Check(failure==null,"Actual tray host runtime gate failed: "+failure);
        Check(finished && !icon.Visible,"Explicit exit must finish the gate and remove the tray icon.");
        Console.WriteLine("PASS: actual compact WebView paint, tray warm start/reopen, owned popup, typed hide, close-to-tray, outside activation and clean exit.");
        Console.WriteLine("Evidence: "+evidence);
    }
}
