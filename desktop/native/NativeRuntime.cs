using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Popcorn;

public static class NativeRuntime
{
    internal static void ValidateLocalLocation(string root,DriveType? driveType=null)
    {
        if(!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\",StringComparison.Ordinal) ||
            (driveType??new DriveInfo(Path.GetPathRoot(root)).DriveType) is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
            throw new NotSupportedException("Extract or install to an available local drive, not a network share or mapped network drive.");
    }
    public static void LoadMediaProjection(string root)
    {
        Assembly.LoadFrom(Path.Combine(root,"WinRT.Runtime.dll"));
        Assembly.LoadFrom(Path.Combine(root,"Microsoft.Windows.SDK.NET.dll"));
    }
    public static void ValidateApplication(ApplicationPaths paths)
    {
        ValidateLocalLocation(paths.Root);
        if(Directory.Exists(paths.FixedBrowser))throw new InvalidDataException("精简版目录混有旧版 WebView2Runtime。请先卸载完整离线版，或解压到新的空目录，不要混合覆盖。");
        foreach(string relative in new[]{"DanmuCompanion.Probe.exe","Microsoft.Web.WebView2.Core.dll","WebView2Loader.dll",
            "WinRT.Runtime.dll","Microsoft.Windows.SDK.NET.dll","assets/app.ico","assets/tray.ico","ui/dist/index.html"})
            if(!File.Exists(Path.Combine(paths.Root,relative)))throw new FileNotFoundException("Application package file is missing: "+relative);
        string dist=Path.GetFullPath(Path.Combine(paths.Root,"ui","dist"))+Path.DirectorySeparatorChar;
        var assets=Regex.Matches(File.ReadAllText(Path.Combine(dist,"index.html")),"(?:src|href)=\"(?:\\./|/)?(?<asset>assets/[^\"\\r\\n]+)\"");
        bool script=false;
        foreach(Match match in assets)
        {
            string relative=match.Groups["asset"].Value,path=Path.GetFullPath(Path.Combine(dist,relative));
            if(!path.StartsWith(dist,StringComparison.OrdinalIgnoreCase) || !File.Exists(path))throw new InvalidDataException("Application UI asset is missing or invalid.");
            script|=relative.EndsWith(".js",StringComparison.Ordinal);
        }
        if(!script)throw new InvalidDataException("Application UI script entry is missing.");
    }
    public static void ConfigureBrowserEnvironment(ApplicationPaths paths,int debugPort=0)
    {
        if(debugPort is <0 or >65535)throw new ArgumentOutOfRangeException(nameof(debugPort));
        // Use the shared stable Runtime, with app-owned data and no inherited browser overrides.
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",null);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",paths.BrowserData);
        foreach(string name in new[]{"WEBVIEW2_RELEASE_CHANNEL_PREFERENCE","WEBVIEW2_CHANNEL_SEARCH_KIND",
            "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER","WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER"})Environment.SetEnvironmentVariable(name,null);
        Environment.SetEnvironmentVariable("WEBVIEW2_RELEASE_CHANNELS","0");
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",debugPort>0 ?
            "--remote-debugging-port="+debugPort+" --remote-debugging-address=127.0.0.1" : null);
    }
}
