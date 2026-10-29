using System;
using System.IO;

namespace Popcorn;

internal static class DistributionTests
{
    private static int assertions;
    internal static void Check(bool value,string message)
    {
        assertions++;
        if(!value)throw new Exception(message);
    }
    private static void Reject(Action action,string message)
    {
        try {action();}
        catch(ArgumentException) {assertions++;return;}
        throw new Exception(message);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length>0 && args[0]=="--video-companion-target")return VideoCompanionTests.RunTarget();
            if(args.Length>0 && args[0]=="--video-companion-tests"){VideoCompanionTests.Run();return 0;}
            if(args.Length>0 && args[0]=="--retry-match-tests"){RetryMatchTests.Run().GetAwaiter().GetResult();return 0;}
            if(args.Length>0 && args[0]=="--video-notice-tests"){VideoNoticeTests.Run();return 0;}
            if(args.Length>0 && args[0]=="--video-notice-runtime"){VideoNoticeTests.RunRuntime();return 0;}
            if(args.Length>0 && args[0]=="--tray-placement-tests"){TrayPlacementTests.Run();return 0;}
            if(args.Length==2 && args[0]=="--tray-host-runtime"){TrayHostRuntimeTests.Run(args[1]);return 0;}
            if(args.Length>0 && args[0]=="--image-fixture"){Console.WriteLine("ready");Console.Out.Flush();Console.ReadLine();return 0;}
            if(args.Length>0 && args[0]=="--process-image-tests"){NativeProcessImageTests.Run();return 0;}
            if(args.Length>0 && args[0]=="--fixture")return NativeChildTests.Emit(args[^1]);
            if(args.Length>0 && args[0]=="--api-fixture")return EmbeddedApiTests.Emit(args[^1]).GetAwaiter().GetResult();
            if(args.Length>0 && args[0]=="--api-tests"){EmbeddedApiTests.Run().GetAwaiter().GetResult();return 0;}
            string root=Path.Combine(Path.GetTempPath(),"danmu-native-tests","\u793a\u4f8b portable");
            string data=Path.Combine(Path.GetTempPath(),"danmu-native-tests","isolated-data");
            var paths=new ApplicationPaths(root,"app",data);
            Check(paths.Root==Path.GetFullPath(root),"Application root must not depend on the working directory.");
            Check(paths.ProfileRoot==Path.Combine(data,"app"),"Mutable profile must be outside application files.");
            Check(paths.ApiPreferences==Path.Combine(data,"api.dat"),"App API storage must retain its fixed current-user location.");
            Check(paths.Preferences==Path.Combine(data,"app","preferences.json"),"Display preferences must use the profile directory.");
            Check(paths.ProbeExecutable==Path.Combine(root,"DanmuCompanion.Probe.exe"),"Sampling must target the compiled worker, not a script.");
            Check(paths.FixedBrowser==Path.Combine(root,"WebView2Runtime"),"Legacy private-runtime layout detection must remain deterministic.");
            Check(paths.BrowserData==Path.Combine(data,"app","webview-evergreen"),"Shared Runtime must not reuse beta.1's fixed-browser profile.");
            var isolated=new ApplicationPaths(root,"smoke-123",data);
            Check(isolated.ApiPreferences==Path.Combine(data,"smoke-123","api.dat"),"Test profiles must not read or overwrite real API data.");
            var normal=new ApplicationPaths(root);
            string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BaomihuaDanmuCompanion","api.dat");
            Check(normal.ApiPreferences==expected,"Existing preview.15 encrypted API location must remain compatible.");
            foreach(string profile in new[]{"../app","a/b","a\\b","",new string('a',49)})
                Reject(()=>new ApplicationPaths(root,profile,data),"Unsafe profile path was accepted.");
            Reject(()=>new ApplicationPaths("relative-path","app",data),"Relative executable roots must be rejected.");
            Reject(()=>new ApplicationPaths(root,"app","relative-data"),"Relative data roots must be rejected.");
            NativeRuntimeTests.Run();
            NativeProcessImageTests.Run();
            SharedBrowserTests.Run();
            NativeSandboxTests.Run();
            NativeInstanceTests.Run();
            NativeStartupTests.Run();
            NativePreferenceTests.Run();
            VideoNoticeTests.Run();
            TrayPlacementTests.Run();
            PlaybackEventSignalTests.Run();
            ProbeOperationTests.Run();
            ProbeTransportTests.Run("All").GetAwaiter().GetResult();
            NativeChildTests.Run().GetAwaiter().GetResult();
            ProbeSupervisorTests.Run(Environment.ProcessPath,"--fixture","unused").GetAwaiter().GetResult();
            AutomaticMatchTests.Run().GetAwaiter().GetResult();
            SampledMatchTests.Run().GetAwaiter().GetResult();
            MatchingDeadlineTests.Run().GetAwaiter().GetResult();
            RetryMatchTests.Run().GetAwaiter().GetResult();
            EmbeddedApiTests.Run().GetAwaiter().GetResult();
            ApiTimeoutTests.Run().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {assertions} native distribution path assertions.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
