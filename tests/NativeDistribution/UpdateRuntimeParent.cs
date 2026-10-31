using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Popcorn;

internal static class UpdateRuntimeParent
{
    internal static async Task<int> Run(string instruction)
    {
        using var input=UpdateStorage.ReadJson(instruction,16384);var data=input.RootElement;
        var paths=new ApplicationPaths(AppContext.BaseDirectory,"update-runtime",data.GetProperty("dataRoot").GetString());
        using var rootLock=new UpdateRootLock(paths.Root);DistributionTests.Check(rootLock.Acquired,"Fixture parent must own its root.");
        var download=data.GetProperty("download").Deserialize<UpdateDownload>(UpdateStorage.Json);
        Console.WriteLine("Fixture preparing worker.");Console.Out.Flush();
        using var lease=await UpdateLauncher.PrepareAsync(paths,download,false,default);
        Console.WriteLine("Fixture worker ready.");Console.Out.Flush();
        int worker=lease.WorkerId;string directory=lease.Directory;
        if(data.GetProperty("mode").GetString().StartsWith("rollback"))UpdateStorage.Atomic(Path.Combine(directory,"started.json"),JsonSerializer.SerializeToUtf8Bytes(new{operationId=Path.GetFileName(directory),version=download.Version,ready=true}));
        if(data.GetProperty("mode").GetString()=="worker-death")
        {
            using var process=Process.GetProcessById(worker);process.Kill(true);await process.WaitForExitAsync();bool rejected=false;
            try{await lease.CommitAsync();}catch{rejected=true;}
            DistributionTests.Check(rejected,"A dead worker must not be committed.");
            UpdateStorage.Atomic(data.GetProperty("result").GetString(),JsonSerializer.SerializeToUtf8Bytes(new{survived=true,worker,directory}));
            await Task.Delay(1500);return 0;
        }
        if(data.GetProperty("mode").GetString()=="preflight-failure")File.AppendAllText(download.File,"changed after ready");
        await lease.CommitAsync();UpdateStorage.Atomic(data.GetProperty("result").GetString(),JsonSerializer.SerializeToUtf8Bytes(new{armed=true,worker,directory}));return 0;
    }
    internal static int Health(string[] args)
    {
        string Value(string name)=>args[Array.IndexOf(args,name)+1];
        var paths=new ApplicationPaths(AppContext.BaseDirectory,Value("--profile"),Value("--data-root"));
        using var rootLock=new UpdateRootLock(paths.Root);DistributionTests.Check(rootLock.Acquired,"Rollback fixture child must own its root.");
        NativeRuntime.ValidateApplication(paths);NativeRuntime.LoadMediaProjection(paths.Root);NativeRuntime.ConfigureBrowserEnvironment(paths);SharedBrowserRuntime.ConfigureLoader(paths.Root);SharedBrowserRuntime.RequireAvailable();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var window=new DesktopWindow(paths.Root,"",Value("--profile"),paths.DataRoot){SuppressErrorDialogs=true};
        using var timer=new System.Windows.Forms.Timer{Interval=250};
        timer.Tick+=async (_,_)=>{if(!window.UpdateHealthReady)return;timer.Stop();try{await UpdateWorker.WriteStartedAsync(paths,Value("--update-handshake"),int.Parse(Value("--update-parent")));}catch{window.RequestExit();}};
        timer.Start();Application.Run(window);return 0;
    }
    internal static int Stop(string root)
    {
        root=UpdatePackage.SafeRoot(root);UpdatePackage.Check(root.Contains("updater-runtime-tests",StringComparison.OrdinalIgnoreCase),"Fixture stop is restricted to updater runtime test directories.");
        string helperRoot=Path.Combine(Path.GetDirectoryName(root),"data","update-runtime","updates")+Path.DirectorySeparatorChar;
        foreach(var process in Process.GetProcessesByName("DanmuCompanion").Concat(Process.GetProcessesByName("DanmuCompanion.Updater")))using(process)
        {
            try
            {
                string image=ProcessImage.Read(process);
                bool helper=image!=null && image.StartsWith(helperRoot,StringComparison.OrdinalIgnoreCase) && image.EndsWith("\\helper\\DanmuCompanion.Updater.exe",StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(image[helperRoot.Length..].Split('\\')[0],"N",out _);
                if(process.Id!=Environment.ProcessId && (helper || string.Equals(image,Path.Combine(root,"DanmuCompanion.exe"),StringComparison.OrdinalIgnoreCase)))
                {if(!helper || !process.WaitForExit(20000)){process.Kill(true);process.WaitForExit(10000);}}
            }
            catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception){}
        }
        return 0;
    }
}
