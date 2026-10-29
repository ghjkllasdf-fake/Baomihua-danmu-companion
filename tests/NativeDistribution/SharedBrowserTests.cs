using System;
using Microsoft.Web.WebView2.Core;

namespace Popcorn;

internal static class SharedBrowserTests
{
    internal static void Run()
    {
        DistributionTests.Check(SharedBrowserRuntime.Options.ReleaseChannels==CoreWebView2ReleaseChannels.Stable,"Only the installed stable WebView2 Runtime may be selected.");
        DistributionTests.Check(SharedBrowserRuntime.RequireAvailable(()=>"156.0.4314.50")=="156.0.4314.50","Stable browser detection must retain the actual version.");
        foreach(string value in new[]{null,"","0.0.0.0","123.0.0.0","156.0.0.0 beta","not-a-version"})
        {
            try{SharedBrowserRuntime.RequireAvailable(()=>value);throw new Exception("Missing, old or preview browser was accepted.");}
            catch(InvalidOperationException error)
            {
                DistributionTests.Check(error.Message.Contains("MicrosoftEdgeWebview2Setup.exe"),"Missing Runtime must provide an actionable EXE-only installation instruction.");
            }
        }
        try{SharedBrowserRuntime.RequireAvailable(()=>throw new WebView2RuntimeNotFoundException());throw new Exception("Missing Runtime exception was swallowed.");}
        catch(InvalidOperationException error){DistributionTests.Check(error.Message.Contains("MicrosoftEdgeWebview2Setup.exe"),"SDK runtime-not-found failure must be sanitized and actionable.");}
        SharedBrowserRuntime.ConfigureLoader(AppContext.BaseDirectory);
        string actual=SharedBrowserRuntime.RequireAvailable();
        SharedBrowserRuntime.ConfigureLoader(AppContext.BaseDirectory);
        DistributionTests.Check(actual==SharedBrowserRuntime.RequireAvailable(),"Loader configuration must be idempotent even after the first real SDK environment API call.");
        Console.WriteLine("PASS: stable-only shared browser policy, minimum version and missing-runtime instructions without side effects.");
    }
}
