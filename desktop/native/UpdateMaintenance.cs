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
                    if(!UpdateStorage.Owns(cache,directory) || !File.Exists(Path.Combine(directory,"worker.json")))continue;
                    using var record=UpdateStorage.ReadJson(Path.Combine(directory,"worker.json"),16384);var job=record.RootElement.GetProperty("job").Deserialize<UpdateJob>(UpdateStorage.Json);
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
    private static void ValidateWorker(ApplicationPaths paths,string directory,JsonElement record)
    {
        UpdatePackage.Unique(record);var job=record.GetProperty("job").Deserialize<UpdateJob>(UpdateStorage.Json);
        UpdatePackage.Check(record.GetProperty("schemaVersion").GetInt32()==2 && record.GetProperty("kind").GetString()=="direct-overwrite" && job!=null && job.OperationId==Path.GetFileName(directory) &&
            string.Equals(UpdatePackage.SafeRoot(job.ApplicationRoot),UpdatePackage.SafeRoot(paths.Root),StringComparison.OrdinalIgnoreCase),"更新操作不属于此应用目录。");
    }
    internal static void Complete(ApplicationPaths paths,string directory,string terminal="complete")
    {
        UpdatePackage.Check(UpdateStorage.Owns(Cache(paths),directory),"更新清理操作身份无效。");
        UpdateStorage.Atomic(Path.Combine(directory,"complete.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=2,operationId=Path.GetFileName(directory),terminal}));
    }
    internal static void TryCleanupOperation(ApplicationPaths paths,string directory)
    {
        try
        {
            UpdatePackage.Check(UpdateStorage.Owns(Cache(paths),directory),"更新清理操作身份无效。");
            // Legacy recovery records and their old bytes are never executed or deleted.
            if(File.Exists(Path.Combine(directory,"recovery.json")) || File.Exists(Path.Combine(directory,"legacy-disabled-recovery.json")))return;
            string worker=Path.Combine(directory,"worker.json");
            if(File.Exists(worker))
            {
                using var record=UpdateStorage.ReadJson(worker,16384);ValidateWorker(paths,directory,record.RootElement);
                if(Running(record.RootElement,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe")))return;
                string apply=UpdatePackage.SafeRoot(record.RootElement.GetProperty("applyDirectory").GetString());
                if(System.IO.Directory.Exists(apply))
                {
                    UpdatePackage.Check(UpdateStorage.Owns(UpdateInstalledOwnership.ParentFor(paths.Root),apply),"覆盖更新清理记录身份无效。");
                    using var journal=UpdateStorage.ReadJson(Path.Combine(apply,"journal.json"),4096);var value=journal.RootElement;
                    UpdatePackage.Check(value.GetProperty("schemaVersion").GetInt32()==2 && value.GetProperty("kind").GetString()=="direct-overwrite" &&
                        string.Equals(UpdatePackage.SafeRoot(value.GetProperty("targetRoot").GetString()),paths.Root,StringComparison.OrdinalIgnoreCase),"覆盖更新清理目标无效。");
                    if(value.GetProperty("phase").GetString() is not ("prepared" or "not-applied" or "complete"))
                    {
                        using var completion=UpdateStorage.ReadJson(Path.Combine(directory,"complete.json"),4096);var flag=completion.RootElement;
                        if(flag.GetProperty("schemaVersion").GetInt32()!=2 || flag.GetProperty("operationId").GetString()!=Path.GetFileName(directory) || flag.GetProperty("terminal").GetString()!="not-applied")return;
                    }
                    UpdateStorage.RemoveOwned(UpdateInstalledOwnership.ParentFor(paths.Root),apply);
                }
                else
                {
                    using var complete=UpdateStorage.ReadJson(Path.Combine(directory,"complete.json"),4096);
                    UpdatePackage.Check(complete.RootElement.GetProperty("schemaVersion").GetInt32()==2 && complete.RootElement.GetProperty("operationId").GetString()==Path.GetFileName(directory),"覆盖更新清理完成记录无效。");
                }
            }
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
            if(string.Equals(directory,preserve,StringComparison.OrdinalIgnoreCase))continue;
            try
            {
                if(File.Exists(Path.Combine(directory,"complete.json")) || Directory.GetLastWriteTimeUtc(directory)<DateTime.UtcNow-TimeSpan.FromDays(1))TryCleanupOperation(paths,directory);
            }
            catch(Exception error) when(UpdateStorage.MetadataError(error)){}
        }
    }
    internal static string LastResult(ApplicationPaths paths)
    {
        try
        {
            using var doc=UpdateStorage.ReadJson(Path.Combine(Cache(paths),"last-result.json"),4096);var result=doc.RootElement;UpdatePackage.Unique(result);
            UpdatePackage.Check(Guid.TryParseExact(result.GetProperty("operationId").GetString(),"N",out _) && DateTimeOffset.TryParse(result.GetProperty("completedAt").GetString(),out _),"更新结果记录无效。");
            string version=result.GetProperty("version").GetString();if(version==null || version.Length>96)return "";
            string detail=result.TryGetProperty("detail",out var value) && value.ValueKind==JsonValueKind.String ? value.GetString() : "";if(detail==null || detail.Length>250)detail="";
            return (result.GetProperty("status").GetString() switch {"complete"=>"上次更新已完成："+version,"repair-required"=>"上次更新未通过启动检查，请重新安装或完整解压修复。","not-applied"=>"上次更新交接未完成，原文件未替换。","rolledback" or "recovery-required"=>"检测到旧版更新记录；本版本不提供恢复旧版，请检查当前安装并按需重新安装。",_=>""})+(detail.Length>0 ? "。"+detail : "");
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error)){return "";}
    }
}
