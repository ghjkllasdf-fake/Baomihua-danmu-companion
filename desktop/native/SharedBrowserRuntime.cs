using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace Popcorn;

internal static class SharedBrowserRuntime
{
    internal const int MinimumMajor=124;
    private static string loaderRoot;
    internal static void ConfigureLoader(string root)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if(loaderRoot!=null)
        {
            if(string.Equals(loaderRoot,root,StringComparison.OrdinalIgnoreCase))return;
            throw new InvalidOperationException("WebView2 loader cannot change application directories after initialization.");
        }
        CoreWebView2Environment.SetLoaderDllFolderPath(root);
        loaderRoot=root;
    }
    internal static CoreWebView2EnvironmentOptions Options=>new(){ReleaseChannels=CoreWebView2ReleaseChannels.Stable};
    internal static string RequireAvailable(Func<string> detect=null)
    {
        const string instruction="需要 Microsoft Edge WebView2 Evergreen Runtime（124 或更新版本）。请运行程序目录中的 MicrosoftEdgeWebview2Setup.exe 联网安装／更新后重试；不需要 PowerShell。";
        try
        {
            string value=detect!=null ? detect() : CoreWebView2Environment.GetAvailableBrowserVersionString(null,Options);
            if(!Version.TryParse(value,out var version) || version.Major<MinimumMajor || version.Build<0 || version.Revision<0)throw new InvalidOperationException(instruction);
            return value;
        }
        catch(Exception error) when(error is WebView2RuntimeNotFoundException or COMException)
        {throw new InvalidOperationException(instruction);}
    }
}
