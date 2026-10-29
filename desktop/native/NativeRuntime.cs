using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Linq;
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
        foreach(string relative in new[]{"DanmuCompanion.Probe.exe","Microsoft.Web.WebView2.Core.dll","WebView2Loader.dll",
            "WinRT.Runtime.dll","Microsoft.Windows.SDK.NET.dll","assets/app.ico","assets/tray.ico","WebView2Runtime/msedgewebview2.exe","ui/dist/index.html"})
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
        // Explicit private runtime/data paths must not be overridden by inherited environment.
        Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER",paths.FixedBrowser);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",paths.BrowserData);
        foreach(string name in new[]{"WEBVIEW2_RELEASE_CHANNEL_PREFERENCE","WEBVIEW2_CHANNEL_SEARCH_KIND","WEBVIEW2_RELEASE_CHANNELS",
            "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER","WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER"})Environment.SetEnvironmentVariable(name,null);
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",debugPort>0 ?
            "--remote-debugging-port="+debugPort+" --remote-debugging-address=127.0.0.1" : null);
    }
    public static void PrepareBrowserSandbox(ApplicationPaths paths)
    {
        // Fixed Version >=120 requires these private-folder ACLs on Windows 10.
        // https://learn.microsoft.com/microsoft-edge/webview2/concepts/distribution
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))GrantBrowserReadAccess(paths.FixedBrowser);
    }
    internal static void GrantBrowserReadAccess(string folder)
    {
        if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("Browser runtime directory must not be a reparse point.");
        var directory=new DirectoryInfo(folder);var security=directory.GetAccessControl();bool changed=false;
        var inheritance=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        foreach(string value in new[]{"S-1-15-2-1","S-1-15-2-2"})
        {
            bool present=security.GetAccessRules(true,true,typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>().Any(rule=>
                rule.IdentityReference.Value==value && rule.AccessControlType==AccessControlType.Allow &&
                (rule.FileSystemRights&FileSystemRights.ReadAndExecute)==FileSystemRights.ReadAndExecute &&
                (rule.InheritanceFlags&inheritance)==inheritance && rule.PropagationFlags==PropagationFlags.None);
            if(present)continue;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(value),FileSystemRights.ReadAndExecute,
                inheritance,PropagationFlags.None,AccessControlType.Allow));changed=true;
        }
        if(changed)directory.SetAccessControl(security);
    }
}
