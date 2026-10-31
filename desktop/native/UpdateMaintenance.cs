using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class UpdateMaintenance
{
    private static string Cache(ApplicationPaths paths)=>Path.Combine(paths.ProfileRoot,"updates");
    private static bool Completed(string directory)
    {
        try{using var doc=UpdateStorage.ReadJson(Path.Combine(directory,"complete.json"),4096);return doc.RootElement.GetProperty("schemaVersion").GetInt32()==1 && doc.RootElement.GetProperty("operationId").GetString()==Path.GetFileName(directory);}
        catch(Exception error) when(UpdateStorage.MetadataError(error)){return false;}
    }
    private static Process OpenWorker(JsonElement record,string helper)
    {
        Process worker=null;
        try
        {
            int pid=record.GetProperty("workerPid").GetInt32();long ticks=record.GetProperty("workerStartTicks").GetInt64();
            worker=Process.GetProcessById(pid);_=worker.Handle;
            if(!worker.HasExited && worker.StartTime.ToUniversalTime().Ticks==ticks &&
                string.Equals(worker.MainModule?.FileName,helper,StringComparison.OrdinalIgnoreCase))return worker;
        }
        catch(System.ComponentModel.Win32Exception){worker?.Dispose();throw;}
        catch(Exception error) when(error is ArgumentException or InvalidOperationException){}
        worker?.Dispose();return null;
    }
    internal static bool Running(JsonElement record,string helper,Func<JsonElement,string,Process> query=null)
    {
        try{using var worker=(query??OpenWorker)(record,helper);return worker!=null;}
        // Inaccessible is not exited: keep recovery/cleanup fail-closed on process-query errors.
        catch(System.ComponentModel.Win32Exception){return true;}
    }
    internal static Task ObserveWorkers(ApplicationPaths paths,Action completed,CancellationToken token,Action failed=null,IEnumerable<string> directories=null)
    {
        string cache=Cache(paths);if(!Directory.Exists(cache))return Task.CompletedTask;
        var waits=new List<Task>();
        try
        {
            foreach(string directory in directories??Directory.EnumerateDirectories(cache))
            {
                if(token.IsCancellationRequested)break;
                try
                {
                    if(!UpdateStorage.Owns(cache,directory) || !File.Exists(Path.Combine(directory,"recovery.json")))continue;
                    using var record=UpdateStorage.ReadJson(Path.Combine(directory,"recovery.json"),16384);var job=record.RootElement.GetProperty("job").Deserialize<UpdateJob>(UpdateStorage.Json);
                    UpdatePackage.Check(job!=null && job.OperationId==Path.GetFileName(directory) && string.Equals(UpdatePackage.SafeRoot(job.ApplicationRoot),UpdatePackage.SafeRoot(paths.Root),StringComparison.OrdinalIgnoreCase),"更新观察操作不属于此应用目录。");
                    var worker=OpenWorker(record.RootElement,UpdatePackage.SafeFile(directory,"helper/DanmuCompanion.Updater.exe"));
                    if(worker!=null)waits.Add(Observe(worker));
                }
                catch(Exception error) when(UpdateStorage.MetadataError(error)){}
                catch(System.ComponentModel.Win32Exception){failed?.Invoke();}
            }
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error)){failed?.Invoke();}
        return Task.WhenAll(waits);
        async Task Observe(Process worker)
        {
            using(worker)
            {
                try{await worker.WaitForExitAsync(token).ConfigureAwait(false);if(!token.IsCancellationRequested)completed();}
                catch(OperationCanceledException) when(token.IsCancellationRequested){}
                catch(Exception error) when(UpdateStorage.MetadataError(error) || error is System.ComponentModel.Win32Exception){}
            }
        }
    }
    private static (string Transaction,string Phase,bool Running) Inspect(ApplicationPaths paths,string directory)
    {
        UpdatePackage.Check(UpdateStorage.Owns(Cache(paths),directory),"更新操作所属证明无效。");
        using var input=File.OpenRead(Path.Combine(directory,"recovery.json"));using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,16384));var record=doc.RootElement;UpdatePackage.Unique(record);
        var job=record.GetProperty("job").Deserialize<UpdateJob>(UpdateStorage.Json);
        UpdatePackage.Check(job!=null && job.OperationId==Path.GetFileName(directory) && string.Equals(UpdatePackage.SafeRoot(job.ApplicationRoot),UpdatePackage.SafeRoot(paths.Root),StringComparison.OrdinalIgnoreCase),"恢复操作不属于此应用目录。");
        string transaction=UpdatePackage.SafeRoot(record.GetProperty("transactionDirectory").GetString());
        UpdatePackage.Check(UpdateStorage.Owns(UpdateTransaction.ParentFor(paths.Root),transaction),"更新事务所属证明无效。");
        using var file=File.OpenRead(Path.Combine(transaction,"journal.json"));using var journal=JsonDocument.Parse(UpdatePackage.ReadLimited(file,4096));UpdatePackage.Unique(journal.RootElement);
        UpdatePackage.Check(string.Equals(UpdatePackage.SafeRoot(journal.RootElement.GetProperty("targetRoot").GetString()),UpdatePackage.SafeRoot(paths.Root),StringComparison.OrdinalIgnoreCase),"恢复事务目标不匹配。");
        return (transaction,journal.RootElement.GetProperty("phase").GetString(),Running(record,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe")));
    }
    internal static string FindRecovery(ApplicationPaths paths)
    {
        string cache=Cache(paths);if(!Directory.Exists(cache))return null;
        foreach(string directory in Directory.EnumerateDirectories(cache))
        {
            try
            {
                var operation=Inspect(paths,directory);
                if(Completed(directory))continue;
                if(!operation.Running && operation.Phase is "armed" or "applying" or "installed" or "starting" or "rolledback" or "rollback-failed")
                {
                    string helper=UpdatePackage.SafeFile(directory,"helper/DanmuCompanion.Updater.exe");if(File.Exists(helper))return helper;
                }
            }
            catch(Exception error) when(UpdateStorage.MetadataError(error)){}
        }
        return null;
    }
    internal static void Complete(ApplicationPaths paths,string directory,bool retain=false)
    {
        UpdatePackage.Check(UpdateStorage.Owns(Cache(paths),directory),"更新清理操作身份无效。");
        UpdateStorage.Atomic(Path.Combine(directory,"complete.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,operationId=Path.GetFileName(directory),retain}));
    }
    internal static void TryCleanupOperation(ApplicationPaths paths,string directory)
    {
        try
        {
            UpdatePackage.Check(UpdateStorage.Owns(Cache(paths),directory),"更新清理操作身份无效。");
            string recovery=Path.Combine(directory,"recovery.json");
            if(File.Exists(recovery))
            {
                var operation=Inspect(paths,directory);if(operation.Running || operation.Phase is not ("preparing" or "prepared" or "complete" or "rolledback"))return;
                UpdateStorage.RemoveOwned(UpdateTransaction.ParentFor(paths.Root),operation.Transaction);
            }
            UpdateRecoveryEntry.Remove(paths,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe"));
            UpdateStorage.RemoveOwned(Cache(paths),directory);
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error)){}
    }
    internal static void Cleanup(ApplicationPaths paths,string preserve=null,CancellationToken token=default)
    {
        string cache=Cache(paths);if(!Directory.Exists(cache))return;
        foreach(string directory in Directory.EnumerateDirectories(cache))
        {
            if(token.IsCancellationRequested)break;
            try
            {
                if(!UpdateStorage.Owns(cache,directory))continue;
                if(string.Equals(directory,preserve,StringComparison.OrdinalIgnoreCase))continue;
                if(File.Exists(Path.Combine(directory,"complete.json")))
                {
                    using var doc=UpdateStorage.ReadJson(Path.Combine(directory,"complete.json"),4096);
                    if(doc.RootElement.GetProperty("schemaVersion").GetInt32()!=1 || doc.RootElement.GetProperty("operationId").GetString()!=Path.GetFileName(directory))continue;
                    if(doc.RootElement.TryGetProperty("retain",out var retain) && retain.GetBoolean())continue;
                    // A completed worker may have already removed its large transaction.
                    if(File.Exists(Path.Combine(directory,"recovery.json")))
                    {
                        using var recovery=UpdateStorage.ReadJson(Path.Combine(directory,"recovery.json"),16384);
                        if(Running(recovery.RootElement,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe")))continue;
                        string transaction=recovery.RootElement.GetProperty("transactionDirectory").GetString();
                        if(Directory.Exists(transaction))
                        {
                            var operation=Inspect(paths,directory);if(operation.Phase is not ("complete" or "rolledback"))continue;
                            UpdateStorage.RemoveOwned(UpdateTransaction.ParentFor(paths.Root),operation.Transaction);
                        }
                    }
                    UpdateRecoveryEntry.Remove(paths,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe"));
                    UpdateStorage.RemoveOwned(cache,directory);
                }
                else if(Directory.GetLastWriteTimeUtc(directory)<DateTime.UtcNow-TimeSpan.FromDays(1))TryCleanupOperation(paths,directory);
            }
            catch(Exception error) when(UpdateStorage.MetadataError(error)){}
        }
    }
    internal static string LastResult(ApplicationPaths paths)
    {
        try
        {
            using var input=File.OpenRead(Path.Combine(Cache(paths),"last-result.json"));using var doc=JsonDocument.Parse(UpdatePackage.ReadLimited(input,4096));var result=doc.RootElement;UpdatePackage.Unique(result);
            UpdatePackage.Check(Guid.TryParseExact(result.GetProperty("operationId").GetString(),"N",out _) && DateTimeOffset.TryParse(result.GetProperty("completedAt").GetString(),out _),"更新结果记录无效。");
            string version=result.GetProperty("version").GetString();if(version==null || version.Length>96)return "";
            string detail=result.TryGetProperty("detail",out var value) && value.ValueKind==JsonValueKind.String ? value.GetString() : "";if(detail==null || detail.Length>250)detail="";
            return (result.GetProperty("status").GetString() switch {"complete"=>"上次更新已完成："+version,"rolledback"=>"上次更新未完成，已恢复原版本："+version,"recovery-required"=>"上次更新未完成，备份已保留，请退出应用后运行恢复组件。","not-applied"=>"上次更新交接未完成，原文件未替换。",_=>""})+(detail.Length>0 ? "。"+detail : "");
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error)){return "";}
    }
}
