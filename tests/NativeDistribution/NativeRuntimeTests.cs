using System;
using System.Collections.Generic;
using System.IO;

namespace Popcorn;

internal static class NativeRuntimeTests
{
    private static void Fails(Action action,string message)
    {
        try{action();}catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or NotSupportedException){return;}
        throw new Exception(message);
    }
    internal static void Run()
    {
        string folder=Path.Combine(Path.GetTempPath(),"DanmuNativePayload-"+Guid.NewGuid().ToString("N"));
        var paths=new ApplicationPaths(folder,"runtime-test",Path.Combine(folder,"isolated-data"));
        string[] names={"WEBVIEW2_BROWSER_EXECUTABLE_FOLDER","WEBVIEW2_USER_DATA_FOLDER","WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
            "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER","WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER","WEBVIEW2_RELEASE_CHANNEL_PREFERENCE","WEBVIEW2_CHANNEL_SEARCH_KIND","WEBVIEW2_RELEASE_CHANNELS"};
        var previous=new Dictionary<string,string>();foreach(string name in names)previous[name]=Environment.GetEnvironmentVariable(name);
        try
        {
            NativeRuntime.ValidateLocalLocation(folder,DriveType.Fixed);
            NativeRuntime.ValidateLocalLocation(folder,DriveType.Removable);
            Fails(()=>NativeRuntime.ValidateLocalLocation(@"\\example.invalid\share\app",DriveType.Fixed),"UNC must not be accepted as a local runtime path.");
            Fails(()=>NativeRuntime.ValidateLocalLocation(@"Z:\app",DriveType.Network),"Mapped network drives must be rejected before browser startup.");
            Fails(()=>NativeRuntime.ValidateLocalLocation(@"Z:\app",DriveType.NoRootDirectory),"Missing drives must be rejected.");
            Fails(()=>NativeRuntime.ValidateApplication(paths),"Incomplete application payload must fail before startup.");
            foreach(string name in new[]{"DanmuCompanion.Probe.exe","Microsoft.Web.WebView2.Core.dll","WebView2Loader.dll","WinRT.Runtime.dll",
                "Microsoft.Windows.SDK.NET.dll","assets/app.ico","assets/tray.ico","ui/dist/assets/main.js"})
            {
                string path=Path.Combine(folder,name);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"controlled placeholder");
            }
            string index=Path.Combine(folder,"ui","dist","index.html");
            File.WriteAllText(index,"<script src=\"assets/main.js\"></script>");NativeRuntime.ValidateApplication(paths);
            Directory.CreateDirectory(paths.FixedBrowser);
            Fails(()=>NativeRuntime.ValidateApplication(paths),"Shared Runtime distribution must reject mixed-in private browser folders.");
            Directory.Delete(paths.FixedBrowser);
            File.Delete(Path.Combine(folder,"ui","dist","assets","main.js"));
            Fails(()=>NativeRuntime.ValidateApplication(paths),"Missing UI script must not pass payload validation.");
            File.WriteAllText(Path.Combine(folder,"ui","dist","assets","main.js"),"controlled placeholder");
            File.WriteAllText(index,"<script src=\"assets/../../escape.js\"></script>");
            Fails(()=>NativeRuntime.ValidateApplication(paths),"UI asset traversal must be rejected.");
            File.WriteAllText(index,"<html></html>");
            Fails(()=>NativeRuntime.ValidateApplication(paths),"Missing production UI entry must be rejected.");
            foreach(string name in names)Environment.SetEnvironmentVariable(name,"inherited-unsafe-override");
            NativeRuntime.ConfigureBrowserEnvironment(paths);
            DistributionTests.Check(Environment.GetEnvironmentVariable(names[0])==null,"Inherited fixed-browser paths must not override the system Runtime.");
            DistributionTests.Check(Environment.GetEnvironmentVariable(names[1])==paths.BrowserData,"Inherited profile must not redirect browser data.");
            foreach(string name in names[2..^1])DistributionTests.Check(Environment.GetEnvironmentVariable(name)==null,"Inherited browser debug/channel flags must not affect ordinary launch.");
            DistributionTests.Check(Environment.GetEnvironmentVariable(names[^1])=="0","Environment channel selection must be stable Runtime only, never Edge preview.");
            NativeRuntime.ConfigureBrowserEnvironment(paths,9237);
            DistributionTests.Check(Environment.GetEnvironmentVariable(names[2])=="--remote-debugging-port=9237 --remote-debugging-address=127.0.0.1","Explicit test debugging must be loopback-only.");
            Fails(()=>NativeRuntime.ConfigureBrowserEnvironment(paths,70000),"Invalid debug port must be rejected.");
        }
        finally
        {
            foreach(var pair in previous)Environment.SetEnvironmentVariable(pair.Key,pair.Value);
            if(Directory.Exists(folder))Directory.Delete(folder,true);
        }
        Console.WriteLine("PASS: incomplete/traversal UI payload rejection and stable shared browser/data environment.");
    }
}
