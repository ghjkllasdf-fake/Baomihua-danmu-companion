using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    private sealed record Configuration(string ApplicationRoot,string DataRoot,string Report);
    [STAThread]
    private static int Main()
    {
        Configuration config=null;
        try
        {
            config=JsonSerializer.Deserialize<Configuration>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"glass-test.json")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            foreach(string path in new[]{config.ApplicationRoot,config.DataRoot,config.Report})
                if(!Path.IsPathFullyQualified(path))throw new ArgumentException("The isolated visual runner requires absolute paths.");
            Directory.CreateDirectory(config.DataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(config.Report));
            File.WriteAllText(config.Report,JsonSerializer.Serialize(new{result="RUNNING",pid=Environment.ProcessId,config.DataRoot}));
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
            string binary=Path.Combine(config.ApplicationRoot,"DanmuCompanion.dll");
            var assembly=Assembly.LoadFrom(binary);
            var type=assembly.GetType("Popcorn.DesktopWindow",true);
            using var host=(Form)Activator.CreateInstance(type,config.ApplicationRoot,"","skin-test",config.DataRoot);
            type.GetProperty("SuppressErrorDialogs",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(host,true);
            void Exit()=>type.GetMethod("RequestExit").Invoke(host,null);
            bool Ready()=>(bool)type.GetProperty("Ready").GetValue(host);
            using var timer=new System.Windows.Forms.Timer{Interval=100};
            var watch=Stopwatch.StartNew();Task<SkinRuntimeEvidence> task=null;SkinRuntimeEvidence evidence=null;Exception failure=null;
            timer.Tick+=(_,_)=>
            {
                try
                {
                    if(watch.Elapsed.TotalSeconds>90)throw new TimeoutException("Owned native visual acceptance exceeded its deadline.");
                    if(!Ready())return;
                    if(task==null){task=SkinRuntimeProbe.RunAsync(host,config.DataRoot,Path.Combine(config.DataRoot,"skin-test","appearance.json"),true);return;}
                    if(!task.IsCompleted)return;
                    evidence=task.GetAwaiter().GetResult();timer.Stop();Exit();
                }
                catch(Exception error){failure=error;timer.Stop();Exit();}
            };
            type.GetMethod("OpenWorkbench").Invoke(host,null);timer.Start();Application.Run(host);
            if(failure!=null)throw failure;
            if(evidence==null || !evidence.DesktopVisualVerified)throw new InvalidOperationException("No complete native optical evidence was produced.");
            using(var restored=(Form)Activator.CreateInstance(type,config.ApplicationRoot,"","skin-test",config.DataRoot))
            {
                object state=type.GetProperty("Appearance").GetValue(restored);
                if((string)state.GetType().GetProperty("Skin").GetValue(state)!="fluent" || (bool)state.GetType().GetProperty("ReduceTransparency").GetValue(state))
                    throw new InvalidOperationException("The final confirmed appearance did not survive a fresh native host.");
            }
            foreach(string name in new[]{"preferences.json","api.dat"})
                if(File.Exists(Path.Combine(config.DataRoot,"skin-test",name)))throw new InvalidOperationException("Visual tests must not write display or API preferences.");
            File.WriteAllText(config.Report,JsonSerializer.Serialize(new {
                result="PASS_NATIVE_GLASS",pid=Environment.ProcessId,evidence,config.ApplicationRoot,config.DataRoot,
                hostSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))),
                runtime=Environment.Version.ToString(),osBuild=Environment.OSVersion.Version.Build,playbackMetadataMasked=true
            },new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error)
        {
            if(config!=null && Path.IsPathFullyQualified(config.Report))
                File.WriteAllText(config.Report,JsonSerializer.Serialize(new{result="FAIL",pid=Environment.ProcessId,config.ApplicationRoot,config.DataRoot,failure=error.GetBaseException().Message},new JsonSerializerOptions{WriteIndented=true}));
            return 1;
        }
    }
}
