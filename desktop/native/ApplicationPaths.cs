using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Popcorn;

public sealed class ApplicationPaths
{
    public string Root {get;}
    public string DataRoot {get;}
    public string ProfileRoot {get;}
    public string ApiPreferences {get;}
    public string Preferences=>Path.Combine(ProfileRoot,"preferences.json");
    public string BrowserData=>Path.Combine(ProfileRoot,"webview-evergreen");
    public string FixedBrowser=>Path.Combine(Root,"WebView2Runtime");
    public string MainExecutable=>Path.Combine(Root,"DanmuCompanion.exe");
    public string ProbeExecutable=>Path.Combine(Root,"DanmuCompanion.Probe.exe");
    public ApplicationPaths(string applicationRoot,string profile="app",string userDataRoot=null)
    {
        if(string.IsNullOrEmpty(applicationRoot) || !Path.IsPathFullyQualified(applicationRoot))
            throw new ArgumentException("Application root must be an absolute path.");
        if(profile==null || !Regex.IsMatch(profile,@"^[a-z0-9][a-z0-9-]{0,47}$"))
            throw new ArgumentException("Invalid data profile.");
        if(userDataRoot!=null && !Path.IsPathFullyQualified(userDataRoot))
            throw new ArgumentException("Data root must be an absolute path.");
        Root=Path.GetFullPath(applicationRoot);
        DataRoot=Path.GetFullPath(userDataRoot??Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BaomihuaDanmuCompanion"));
        ProfileRoot=Path.Combine(DataRoot,profile);
        ApiPreferences=Path.Combine(profile=="app" ? DataRoot : ProfileRoot,"api.dat");
    }
}
