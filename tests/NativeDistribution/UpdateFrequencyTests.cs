using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Popcorn;

internal static class UpdateFrequencyTests
{
    private static void Check(bool value,string message)=>DistributionTests.Check(value,message);
    private static void Preferences(string root)
    {
        string file=Path.Combine(root,"preferences.json");
        File.WriteAllText(file,"{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkedAt\":\"\"}");
        var legacy=UpdateStorage.Load(file,"stable",out string notice);
        Check(notice=="" && legacy.AutoCheck && legacy.Channel=="beta" && legacy.CheckIntervalHours==6,"Legacy preferences must migrate to six hours without losing values.");
        foreach(int hours in new[]{1,6,12,24})
        {
            File.WriteAllText(file,"{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkedAt\":\"\",\"checkIntervalHours\":"+hours+"}");
            var loaded=UpdateStorage.Load(file,"stable",out notice);
            Check(notice=="" && loaded.CheckIntervalHours==hours,"Every allowed interval must survive persisted loading.");
            UpdateStorage.Save(file,loaded);
            using var saved=JsonDocument.Parse(File.ReadAllBytes(file));
            Check(saved.RootElement.GetProperty("checkIntervalHours").GetInt32()==hours,"Saving must explicitly persist the interval.");
        }
        foreach(string value in new[]{"0","2","48","-1","1.5","\"6\"","null","true"})
        {
            string bad="{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkedAt\":\"\",\"checkIntervalHours\":"+value+"}";
            File.WriteAllText(file,bad);var loaded=UpdateStorage.Load(file,"beta",out notice);
            Check(!loaded.AutoCheck && notice.Length>0 && File.ReadAllText(file)==bad,"Invalid interval must fail closed without rewriting saved bytes.");
        }
        var paths=new ApplicationPaths(Path.Combine(root,"app"),"unit",Path.Combine(root,"data"));
        using var service=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true));
        using(var update=JsonDocument.Parse("{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkIntervalHours\":12}"))service.SetPreferences(update.RootElement);
        using(var old=JsonDocument.Parse("{\"autoCheck\":false,\"autoDownload\":false,\"channel\":\"stable\"}"))service.SetPreferences(old.RootElement);
        using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(service.View,UpdateStorage.Json));
        Check(snapshot.RootElement.GetProperty("checkIntervalHours").GetInt32()==12,"A legacy bridge save must preserve the currently selected interval.");
        var before=service.View;
        using(var bad=JsonDocument.Parse("{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkIntervalHours\":2}"))
            UpdatePackageTests.Reject(()=>service.SetPreferences(bad.RootElement),"Unsupported bridge interval must be rejected.");
        Check(service.View==before,"Rejected preferences must not change the published schedule.");
    }
    internal static Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"DanmuUpdateFrequency-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{Preferences(root);}
        finally
        {
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath())+"DanmuUpdateFrequency-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe frequency fixture cleanup.");
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: update frequency defaults, migration, validation and legacy bridge saves.");
        return Task.CompletedTask;
    }
}
