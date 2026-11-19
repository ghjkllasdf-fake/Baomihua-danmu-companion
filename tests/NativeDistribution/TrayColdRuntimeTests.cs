using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace Popcorn;

internal static class TrayColdRuntimeTests
{
    private static void Check(bool condition,string message)=>DistributionTests.Check(condition,message);

    internal static void Run(string root)
    {
        root=Path.GetFullPath(root);
        string output=Path.GetFullPath(Path.Combine("artifacts","tray-cold-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        const string profile="cold-tray-test";
        var paths=new ApplicationPaths(root,profile,Path.Combine(output,"isolated"));
        AppearancePreferences.Save(paths.AppearancePreferences,new("studio",true));
        NativeRuntime.ValidateApplication(paths);NativeRuntime.ConfigureBrowserEnvironment(paths);SharedBrowserRuntime.ConfigureLoader(root);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var host=new DesktopWindow(root,"",profile,paths.DataRoot){SuppressErrorDialogs=true};
        using var kickoff=new Timer{Interval=15};
        var watch=Stopwatch.StartNew();Exception failure=null;bool passed=false;
        kickoff.Tick+=async (_,_)=>
        {
            kickoff.Stop();
            try
            {
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var loading=(Label)typeof(DesktopWindow).GetField("loadingPanel",flags).GetValue(host);
                var field=typeof(DesktopWindow).GetField("controller",flags);
                watch.Restart();host.OpenWorkbench();
                Check(host.Visible && loading.Visible && !host.Ready && loading.BackColor==WindowMaterial.Fallback("studio"),
                    "Cold open must show the saved skin loading surface before browser creation.");
                double placeholderMs=watch.Elapsed.TotalMilliseconds;
                while(!host.Ready && watch.ElapsedMilliseconds<15000)
                {
                    if(field.GetValue(host) is CoreWebView2Controller pending && pending.IsVisible)
                        throw new InvalidOperationException("Cold browser became visible before the bridge handshake.");
                    await Task.Delay(20);
                }
                Check(host.Ready && !loading.Visible && field.GetValue(host) is CoreWebView2Controller {IsVisible:true},
                    "Verified content must replace the loading surface after handshake.");
                double readyMs=watch.Elapsed.TotalMilliseconds;
                await Task.Delay(250);
                var paint=await TrayContentProbe.CaptureAsync(host,Path.Combine(output,"first-panel.png")).WaitAsync(TimeSpan.FromSeconds(5));
                host.HideWorkbench();host.OpenWorkbench();
                Check(!loading.Visible && field.GetValue(host) is CoreWebView2Controller {IsVisible:true},
                    "Reopening a warm panel must not restore the cold loading surface.");
                File.WriteAllText(Path.Combine(output,"cold.json"),JsonSerializer.Serialize(new{placeholderMs,readyMs,paint},new JsonSerializerOptions{WriteIndented=true}));
                passed=true;
            }
            catch(Exception error){failure=error;}
            finally{host.RequestExit();}
        };
        kickoff.Start();Application.Run(host);
        Check(passed,"Cold tray gate failed: "+(failure?.ToString()??host.Failure));
        Console.WriteLine("PASS: native cold loading surface, actual WebView paint and warm reopen. Evidence: "+output);
    }
}
