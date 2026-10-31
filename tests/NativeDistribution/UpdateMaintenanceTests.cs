using System;
using System.IO;
using System.Text.Json;

namespace Popcorn;

internal static class UpdateMaintenanceTests
{
    internal static void Run()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"DanmuUpdateMaintenance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            var paths=new ApplicationPaths(Path.Combine(fixture,"app"),"unit",Path.Combine(fixture,"data"));string cache=Path.Combine(paths.ProfileRoot,"updates");Directory.CreateDirectory(cache);
            UpdateTransactionTests.Payload(paths.Root,"0.1.1-beta.6",new(){{"DanmuCompanion.exe","old"}});
            DistributionTests.Check(UpdateMaintenance.Running(JsonSerializer.SerializeToElement(new{}),"",(_,_)=>throw new System.ComponentModel.Win32Exception()),"Inaccessible worker identity must not be treated as exited.");
            string legacy=UpdateStorage.CreateOperation(cache,"worker");Directory.CreateDirectory(Path.Combine(legacy,"backup"));File.WriteAllText(Path.Combine(legacy,"backup","old.exe"),"historic original");File.WriteAllText(Path.Combine(legacy,"recovery.json"),"{}");
            UpdateMaintenance.Complete(paths,legacy);Directory.SetLastWriteTimeUtc(legacy,DateTime.UtcNow-TimeSpan.FromDays(3));UpdateMaintenance.Cleanup(paths);
            DistributionTests.Check(File.ReadAllText(Path.Combine(legacy,"backup","old.exe"))=="historic original","The new updater must not delete or execute user legacy backups.");
            string scopedLegacy=UpdateStorage.CreateOperation(cache,"worker");Directory.CreateDirectory(Path.Combine(scopedLegacy,"backup"));File.WriteAllText(Path.Combine(scopedLegacy,"backup","old.exe"),"existing old program");
            var legacyJob=new UpdateJob(paths.Root,paths.DataRoot,"unit",null,false,Path.GetFileName(scopedLegacy));
            UpdateStorage.Atomic(Path.Combine(scopedLegacy,"recovery.json"),JsonSerializer.SerializeToUtf8Bytes(new{job=legacyJob,workerPid=int.MaxValue,workerStartTicks=0},UpdateStorage.Json));
            string foreignLink=UpdateLegacyEntry.PathFor(paths);Directory.CreateDirectory(Path.GetDirectoryName(foreignLink));File.WriteAllText(foreignLink,"foreign shortcut-shaped file");UpdateLegacyEntry.Retire(paths);
            DistributionTests.Check(!File.Exists(Path.Combine(scopedLegacy,"recovery.json")) && File.Exists(Path.Combine(scopedLegacy,"legacy-disabled-recovery.json")),"Proven stopped legacy recovery must be disabled without retaining a runnable default recovery task.");
            UpdateMaintenance.Complete(paths,scopedLegacy);UpdateMaintenance.Cleanup(paths);
            DistributionTests.Check(File.ReadAllText(Path.Combine(scopedLegacy,"backup","old.exe"))=="existing old program" && File.ReadAllText(foreignLink)=="foreign shortcut-shaped file","Disabling a legacy task must preserve historical program bytes and unowned shortcuts.");
            foreach(string recordName in new[]{"recovery.json","worker.json"})
            {
                string malformed=UpdateStorage.CreateOperation(cache,"worker");
                var badJob=legacyJob with{ApplicationRoot=@"\\server\share\x",OperationId=Path.GetFileName(malformed)};
                UpdateStorage.Atomic(Path.Combine(malformed,recordName),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=2,kind="direct-overwrite",job=badJob,workerPid=int.MaxValue,workerStartTicks=0},UpdateStorage.Json));
                UpdateLegacyEntry.Retire(paths);UpdateMaintenance.ObserveWorkers(paths,()=>{},System.Threading.CancellationToken.None).GetAwaiter().GetResult();UpdateMaintenance.TryCleanupOperation(paths,malformed);
                DistributionTests.Check(File.Exists(Path.Combine(malformed,recordName)),"Invalid legacy/worker UNC roots must be preserved without blocking startup or scheduling.");
            }
            foreach(string phase in new[]{"prepared","armed","applying","repair-required","complete"})
            {
                string worker=UpdateStorage.CreateOperation(cache,"worker"),apply=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(paths.Root),"direct-update");
                _=new UpdateMetadataAuthority(paths.Root,apply,true);
                var job=new UpdateJob(paths.Root,paths.DataRoot,"unit",new UpdateDownload("",new string('a',64),1,"0.1.1-beta.7","standard","portable",legacy),false,Path.GetFileName(worker));
                UpdateStorage.Atomic(Path.Combine(worker,"worker.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=2,kind="direct-overwrite",job,applyDirectory=apply,workerPid=int.MaxValue,workerStartTicks=0},UpdateStorage.Json));
                UpdateStorage.Atomic(Path.Combine(apply,"journal.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=2,kind="direct-overwrite",targetRoot=paths.Root,phase}));
                UpdateMaintenance.TryCleanupOperation(paths,worker);
                DistributionTests.Check(Directory.Exists(apply)==(phase is "armed" or "applying" or "repair-required"),"Interrupted direct writes retain metadata/new download until repair, without old bytes.");
            }
            string locked=UpdateStorage.CreateOperation(cache,"download"),file=Path.Combine(locked,"locked.bin");File.WriteAllText(file,"busy");
            using(var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None)){UpdateMaintenance.TryCleanupOperation(paths,locked);DistributionTests.Check(UpdateStorage.Owns(cache,locked),"Locked cleanup must retain its owner marker for retry.");}
            UpdateMaintenance.TryCleanupOperation(paths,locked);DistributionTests.Check(!Directory.Exists(locked),"Unlocked download cleanup must be retryable.");
            File.WriteAllText(Path.Combine(cache,"last-result.json"),"broken");DistributionTests.Check(UpdateMaintenance.LastResult(paths)=="","Malformed history must not block startup.");
            UpdateStorage.Atomic(Path.Combine(cache,"last-result.json"),JsonSerializer.SerializeToUtf8Bytes(new{operationId=Guid.NewGuid().ToString("N"),status="repair-required",version="0.1.1-beta.7",completedAt=DateTimeOffset.UtcNow.ToString("O")}));
            DistributionTests.Check(UpdateMaintenance.LastResult(paths).Contains("重新安装") && !UpdateMaintenance.LastResult(paths).Contains("已恢复"),"Partial update results must truthfully require repair, never claim rollback or success.");
        }
        finally{UpdatePackage.Check(Path.GetFullPath(fixture).StartsWith(Path.Combine(Path.GetTempPath(),"DanmuUpdateMaintenance-"),StringComparison.OrdinalIgnoreCase),"Unsafe fixture cleanup.");Directory.Delete(fixture,true);}
        Console.WriteLine("PASS: metadata-only cleanup, legacy-backup preservation, process-query failure, locked retry and truthful repair notices.");
    }
}
