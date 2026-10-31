using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Popcorn;

internal static class UpdateMaintenanceTests
{
    internal static void Run()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"DanmuUpdateMaintenance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            var paths=new ApplicationPaths(Path.Combine(fixture,"app")+Path.DirectorySeparatorChar,"unit",Path.Combine(fixture,"data"));
            using(var first=new UpdateRootLock(paths.Root))using(var second=new UpdateRootLock(paths.Root))
            {DistributionTests.Check(first.Acquired && !second.Acquired,"Recovery must not acquire a live root or mutate it.");}
            using(var released=new UpdateRootLock(paths.Root))DistributionTests.Check(released.Acquired,"Root ownership must release across async-capable lifetimes.");
            string cache=Path.Combine(paths.ProfileRoot,"updates");Directory.CreateDirectory(Path.Combine(cache,"foreign"));
            File.WriteAllText(Path.Combine(cache,"last-result.json"),"{}");
            File.WriteAllText(Path.Combine(cache,"preferences.json"),"{\"a\":1,\"b\":2,\"c\":3,\"d\":4}");
            using(var corrupt=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.6","standard","portable",true)))
                DistributionTests.Check(!corrupt.View.AutoCheck && corrupt.View.Message.Length>0,"Malformed update preferences must disable checking, never block construction.");
            DistributionTests.Check(UpdateMaintenance.FindRecovery(paths)==null && UpdateMaintenance.LastResult(paths)=="","Unowned and malformed cache records must not block startup.");
            string broken=UpdateStorage.CreateOperation(cache,"worker");File.WriteAllText(Path.Combine(broken,"recovery.json"),"{}");
            UpdateMaintenance.Cleanup(paths);DistributionTests.Check(Directory.Exists(Path.Combine(cache,"foreign")) && Directory.Exists(broken),"Cleanup must preserve foreign and corrupt recovery directories.");
            var old=new Dictionary<string,string>{{"DanmuCompanion.exe","old"}};var next=new Dictionary<string,string>{{"DanmuCompanion.exe","new"}};
            UpdateTransactionTests.Payload(paths.Root,"0.1.1-beta.6",old);
            UpdatePackageTests.Reject(()=>UpdateLauncher.CheckHelperPathBudget(Path.Combine(fixture,new string('x',230)),UpdatePackage.Read(paths.Root)),"Long native helper paths must fail before launching or stopping the original parent.");
            string transaction=UpdateStorage.CreateOperation(UpdateTransaction.ParentFor(paths.Root),"transaction");UpdateTransactionTests.Payload(Path.Combine(transaction,"stage"),"0.1.1-beta.7",next);
            var prepared=UpdateTransaction.Prepare(paths.Root,transaction,"0.1.1-beta.7","standard");prepared.SetPhase("armed");
            string worker=UpdateStorage.CreateOperation(cache,"worker"),helper=Path.Combine(worker,"helper","DanmuCompanion.Updater.exe");Directory.CreateDirectory(Path.GetDirectoryName(helper));File.WriteAllText(helper,"owned test helper");
            var job=new UpdateJob(paths.Root,paths.DataRoot,"unit",new UpdateDownload("",new string('a',64),1,"0.1.1-beta.7","standard","portable",broken),false,Path.GetFileName(worker));
            UpdateStorage.Atomic(Path.Combine(worker,"recovery.json"),JsonSerializer.SerializeToUtf8Bytes(new{job,transactionDirectory=transaction,workerPid=int.MaxValue,workerStartTicks=0},UpdateStorage.Json));
            var sibling=new ApplicationPaths(Path.Combine(fixture,"other-app"),"unit",paths.DataRoot);
            using(var otherLock=new UpdateRootLock(sibling.Root))UpdatePackageTests.Reject(()=>UpdateWorker.ValidateRecoveryJournal(sibling,job,transaction),"A sibling transaction must not be restored while holding a different root lock.");
            DistributionTests.Check(UpdateMaintenance.FindRecovery(paths)==helper,"Interrupted recovery must match roots with a trailing separator.");
            DistributionTests.Check(Task.Run(()=>UpdateRecoveryEntry.Create(paths,helper)).Wait(TimeSpan.FromSeconds(8)),"Recovery shortcuts must not marshal back to a blocked updater STA entry thread.");
            File.Delete(Path.Combine(paths.Root,"DanmuCompanion.exe"));
            DistributionTests.Check(File.Exists(UpdateRecoveryEntry.PathFor(paths)),"The external recovery entry must survive a broken main launcher.");
            Directory.SetLastWriteTimeUtc(worker,DateTime.UtcNow-TimeSpan.FromDays(2));UpdateMaintenance.Cleanup(paths);
            DistributionTests.Check(Directory.Exists(worker) && Directory.Exists(transaction),"Interrupted transactions must survive stale-cache cleanup.");
            prepared.SetPhase("complete");UpdateMaintenance.Complete(paths,worker);UpdateMaintenance.Cleanup(paths);
            DistributionTests.Check(!Directory.Exists(worker) && !Directory.Exists(transaction),"Complete operations must reclaim helper, stage and backup bytes.");
            DistributionTests.Check(!File.Exists(UpdateRecoveryEntry.PathFor(paths)),"Complete operations must remove only their own recovery shortcut.");
            string locked=UpdateStorage.CreateOperation(cache,"download"),lockedFile=Path.Combine(locked,"locked.bin");File.WriteAllText(lockedFile,"temporarily busy");
            using(var handle=new FileStream(lockedFile,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                UpdatePackageTests.Reject(()=>UpdateStorage.RemoveOwned(cache,locked),"Locked cache bytes must defer cleanup.");
                DistributionTests.Check(UpdateStorage.Owns(cache,locked),"A failed cleanup must retain its owner marker for retry.");
            }
            UpdateStorage.RemoveOwned(cache,locked);DistributionTests.Check(!Directory.Exists(locked),"Unlocked cache cleanup must be retryable.");
        }
        finally{UpdatePackage.Check(Path.GetFullPath(fixture).StartsWith(Path.Combine(Path.GetTempPath(),"DanmuUpdateMaintenance-"),StringComparison.OrdinalIgnoreCase),"Unsafe fixture cleanup.");Directory.Delete(fixture,true);}
        Console.WriteLine("PASS: exclusive recovery ownership, corrupt-cache tolerance, root normalization and deferred cleanup.");
    }
}
