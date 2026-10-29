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
            Fails(()=>NativeRuntime.ValidateApplication(paths),"Incomplete application payload must fail before startup.");
            foreach(string name in new[]{"DanmuCompanion.Probe.exe","Microsoft.Web.WebView2.Core.dll","WebView2Loader.dll","WinRT.Runtime.dll",
                "Microsoft.Windows.SDK.NET.dll","assets/app.ico","assets/tray.ico","WebView2Runtime/msedgewebview2.exe","ui/dist/assets/main.js"})
            {
                string path=Path.Combine(folder,name);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"controlled placeholder");
            }
            string index=Path.Combine(folder,"ui","dist","index.html");
            File.WriteAllText(index,"<script src=\"assets/main.js\"></script>");NativeRuntime.ValidateApplication(paths);
            File.Delete(Path.Combine(folder,"ui","dist","assets","main.js"));
            Fails(()=>NativeRuntime.ValidateApplication(paths),"Missing UI script must not pass payload validation.");
            File.WriteAllText(Path.Combine(folder,"ui","dist","assets","main.js"),"controlled placeholder");
            File.WriteAllText(index,"<script src=\"assets/../../escape.js\"></script>");
            Fails(()=>NativeRuntime.ValidateApplication(paths),"UI asset traversal must be rejected.");
            File.WriteAllText(index,"<html></html>");
            Fails(()=>NativeRuntime.ValidateApplication(paths),"Missing production UI entry must be rejected.");
            foreach(string name in names)Environment.SetEnvironmentVariable(name,"inherited-unsafe-override");
            NativeRuntime.ConfigureBrowserEnvironment(paths);
            DistributionTests.Check(Environment.GetEnvironmentVariable(names[0])==paths.FixedBrowser,"Inherited browser path must not override the shipped runtime.");
            DistributionTests.Check(Environment.GetEnvironmentVariable(names[1])==paths.BrowserData,"Inherited profile must not redirect browser data.");
            foreach(string name in names[2..])DistributionTests.Check(Environment.GetEnvironmentVariable(name)==null,"Inherited browser debug/channel flags must not affect ordinary launch.");
            NativeRuntime.ConfigureBrowserEnvironment(paths,9237);
            DistributionTests.Check(Environment.GetEnvironmentVariable(names[2])=="--remote-debugging-port=9237 --remote-debugging-address=127.0.0.1","Explicit test debugging must be loopback-only.");
            Fails(()=>NativeRuntime.ConfigureBrowserEnvironment(paths,70000),"Invalid debug port must be rejected.");
        }
        finally
        {
            foreach(var pair in previous)Environment.SetEnvironmentVariable(pair.Key,pair.Value);
            if(Directory.Exists(folder))Directory.Delete(folder,true);
        }
        Console.WriteLine("PASS: incomplete/traversal UI payload rejection and explicit private browser/data environment.");
    }
}
