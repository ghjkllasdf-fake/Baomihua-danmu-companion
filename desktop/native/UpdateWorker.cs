using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Popcorn;

internal static class UpdateWorker
{
    internal static ApplicationPaths Validate(UpdateJob job,string helper)
    {
        UpdatePackage.Check(job!=null && job.Download!=null && Guid.TryParseExact(job.OperationId,"N",out _),"更新任务格式无效。");
        var paths=new ApplicationPaths(UpdatePackage.SafeRoot(job.ApplicationRoot),job.Profile,UpdatePackage.SafeRoot(job.DataRoot));
        string operation=Path.GetDirectoryName(UpdatePackage.SafeRoot(helper)),cache=Path.Combine(paths.ProfileRoot,"updates");
        UpdatePackage.Check(Path.GetFileName(operation)==job.OperationId && UpdateStorage.Owns(cache,operation) && Path.GetFileName(helper)=="helper","更新组件目录与任务身份不匹配。");
        UpdatePackage.Check(UpdateStorage.Owns(cache,job.Download.Operation) && Path.GetFullPath(job.Download.File)==Path.Combine(job.Download.Operation,UpdateCatalog.ArtifactName(job.Download.Version,job.Download.Variant=="includeAPI",job.Download.Distribution)) &&
            job.Download.Size>0 && job.Download.Size<=UpdateCatalog.MaxDownload && UpdateCatalog.HashValid(job.Download.Checksum),"更新下载任务的所属证明无效。");
        var current=UpdatePackage.Read(paths.Root);UpdatePackage.Check(current.Variant==job.Download.Variant && UpdateVersion.Newer(job.Download.Version,current.Version) &&
            UpdateIdentity.DistributionFor(paths.Root,current.Variant)==job.Download.Distribution,"更新任务不能改变发行版、安装位置或降低版本。");
        return paths;
    }
    private static string SafeError(Exception error)=>error is InvalidDataException or InvalidOperationException or ArgumentException ? error.Message[..Math.Min(error.Message.Length,500)] : "更新组件遇到文件、权限或启动问题，未确认更新成功。";
    private static void Outcome(ApplicationPaths paths,string id,string status,string version,string detail="")=>UpdateStorage.Atomic(Path.Combine(paths.ProfileRoot,"updates","last-result.json"),JsonSerializer.SerializeToUtf8Bytes(new{
        operationId=id,status,version,detail,completedAt=DateTimeOffset.UtcNow.ToString("O")}));
    internal static async Task<int> RunAsync(string id,int parent)
    {
        UpdatePackage.Check(Guid.TryParseExact(id,"N",out _) && parent>0,"更新组件启动参数无效。");
        using var budget=new CancellationTokenSource(TimeSpan.FromMinutes(3));using var pipe=new NamedPipeClientStream(".","DanmuUpdate-"+id,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(budget.Token);UpdatePipe.CheckPeer(pipe,parent,false);
        using var parentProcess=Process.GetProcessById(parent);_ = parentProcess.SafeHandle;
        ApplicationPaths paths=null;UpdateJob job=null;UpdateTransaction portable=null;UpdateInstallerTransaction installed=null;string transactionDirectory="";
        try
        {
            using var message=await UpdatePipe.ReadAsync(pipe,budget.Token);job=message.RootElement.Deserialize<UpdateJob>(UpdateStorage.Json);
            paths=Validate(job,AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            UpdatePackage.Check(!parentProcess.HasExited && string.Equals(parentProcess.MainModule?.FileName,paths.MainExecutable,StringComparison.OrdinalIgnoreCase),"更新父进程身份无效。");
            UpdatePackage.VerifyFile(job.Download.File,new UpdateFile(Path.GetFileName(job.Download.File),job.Download.Size,job.Download.Checksum));
            UpdatePackage.Check(new DriveInfo(Path.GetPathRoot(paths.Root)).AvailableFreeSpace>UpdatePackage.Read(paths.Root).Bytes+(job.Download.Variant=="includeAPI" ? 400L : 210L)*1024*1024*2+32L*1024*1024,"更新替换所需的磁盘空间不足。");
            transactionDirectory=UpdateStorage.CreateOperation(UpdateTransaction.ParentFor(paths.Root),"transaction");
            UpdateStorage.Atomic(Path.Combine(transactionDirectory,"journal.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,targetRoot=paths.Root,version=job.Download.Version,variant=job.Download.Variant,phase="preparing"}));
            using var self=Process.GetCurrentProcess();
            string recovery=Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),"recovery.json");
            UpdateStorage.Atomic(recovery,JsonSerializer.SerializeToUtf8Bytes(new{job,transactionDirectory,workerPid=Environment.ProcessId,workerStartTicks=self.StartTime.ToUniversalTime().Ticks},UpdateStorage.Json));
            if(job.Download.Distribution=="portable")
            {
                string stage=Path.Combine(transactionDirectory,"stage");UpdatePackage.Extract(job.Download.File,stage,job.Download.Version,job.Download.Variant);NativeRuntime.ValidateApplication(new ApplicationPaths(stage));
                portable=UpdateTransaction.Prepare(paths.Root,transactionDirectory,job.Download.Version,job.Download.Variant);
            }
            else
            {
                UpdatePackage.Check(UpdateCatalog.HashValid(job.Download.InventoryChecksum),"安装更新缺少目标清单的校验值。");
                string inventory=Path.Combine(job.Download.Operation,UpdateCatalog.InventoryName(job.Download.Version,job.Download.Variant=="includeAPI"));
                using var input=File.OpenRead(UpdatePackage.SafeFile(job.Download.Operation,Path.GetFileName(inventory)));byte[] raw=UpdatePackage.ReadLimited(input,4*1024*1024);
                UpdatePackage.Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw)).Equals(job.Download.InventoryChecksum,StringComparison.OrdinalIgnoreCase),"安装更新目标清单校验失败。");
                UpdatePackage.Check(UpdateCatalog.HashValid(job.Download.ArchiveChecksum) && job.Download.ArchiveSize>0 && job.Download.ArchiveSize<=UpdateCatalog.MaxDownload,"安装更新缺少校验过的配套归档。");
                string archiveName=UpdateCatalog.ArtifactName(job.Download.Version,job.Download.Variant=="includeAPI","portable"),archive=UpdatePackage.SafeFile(job.Download.Operation,archiveName);
                UpdatePackage.VerifyFile(archive,new UpdateFile(archiveName,job.Download.ArchiveSize,job.Download.ArchiveChecksum));
                string stage=Path.Combine(transactionDirectory,"stage");var incoming=UpdatePackage.Extract(archive,stage,job.Download.Version,job.Download.Variant);NativeRuntime.ValidateApplication(new ApplicationPaths(stage));
                UpdatePackage.Check(incoming.Manifest.AsSpan().SequenceEqual(raw),"安装器目标清单和配套归档不一致。");
                installed=UpdateInstallerTransaction.Prepare(paths.Root,transactionDirectory,job.Download.Version,job.Download.Variant,UpdateRegistration.Capture(paths.Root,job.Download.Variant),incoming);
            }
            UpdateRecoveryEntry.Create(paths,Path.Combine(AppContext.BaseDirectory,"DanmuCompanion.Updater.exe"));
            await UpdatePipe.WriteAsync(pipe,new{ready=true},budget.Token);
        }
        catch(Exception error)
        {
            if(paths!=null && transactionDirectory.Length>0)try{UpdateStorage.RemoveOwned(UpdateTransaction.ParentFor(paths.Root),transactionDirectory);}catch(IOException){}catch(UnauthorizedAccessException){}
            string recovery=Path.Combine(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),"recovery.json");try{if(File.Exists(recovery))File.Delete(recovery);}catch(IOException){}catch(UnauthorizedAccessException){}
            await UpdatePipe.WriteAsync(pipe,new{ready=false,error=SafeError(error)},CancellationToken.None);return 1;
        }
        bool parentExited=false,applyStarted=false,healthy=false;
        try
        {
            using(var command=await UpdatePipe.ReadAsync(pipe,budget.Token))UpdatePackage.Check(command.RootElement.GetProperty("apply").GetBoolean(),"更新安装未得到主程序确认。");
            portable?.SetPhase("armed");installed?.SetPhase("armed");
            await UpdatePipe.WriteAsync(pipe,new{armed=true},budget.Token);
            // Keep the validated process handle, even if exit precedes this continuation.
            await parentProcess.WaitForExitAsync();
            parentExited=true;
            using(var rootLock=new UpdateRootLock(paths.Root))
            {
            UpdatePackage.Check(rootLock.Acquired,"原程序退出后已有另一个实例启动，未替换运行中的应用。");
            // Recheck mutable staging bytes after the parent exits; do not trust a ready flag.
            UpdatePackage.VerifyFile(job.Download.File,new UpdateFile(Path.GetFileName(job.Download.File),job.Download.Size,job.Download.Checksum));
            installed?.PreflightApply();
            applyStarted=true;
            if(portable!=null)portable.Apply();
            else
            {
                installed.SetPhase("applying");installed.CreateNewFiles();var info=new ProcessStartInfo(job.Download.File){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
                foreach(string arg in new[]{"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-","/DIR="+paths.Root,"/UPDATEJOURNAL="+transactionDirectory,"/LOG="+Path.Combine(transactionDirectory,"setup.log")})info.ArgumentList.Add(arg);
                using var setup=Process.Start(info)??throw new IOException("更新安装器未能启动。");using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(20));
                try{await setup.WaitForExitAsync(timeout.Token);}catch{if(!setup.HasExited){setup.Kill(true);await setup.WaitForExitAsync();}throw;}
                UpdatePackage.Check(setup.ExitCode==0,"更新安装器未成功完成。");installed.AcceptInstalled();
            }
            }
            portable?.SetPhase("starting");installed?.SetPhase("starting");
            await StartAndVerify(paths,job,job.Download.Version);healthy=true;
            portable?.SetPhase("complete");installed?.SetPhase("complete");Outcome(paths,id,"complete",job.Download.Version,installed?.RecoveryNotice??"");Finish(paths,job,transactionDirectory);return 0;
        }
        catch(Exception)
        {
            // A healthy new child is the commit point; bookkeeping failure cannot undo it.
            if(healthy){Finish(paths,job,transactionDirectory);return 0;}
            if(!parentExited || !applyStarted)
            {
                string version=UpdatePackage.Read(paths.Root).Version;portable?.SetPhase("prepared");installed?.SetPhase("prepared");
                if(parentExited)
                {
                    bool free;using(var rootLock=new UpdateRootLock(paths.Root))free=rootLock.Acquired;
                    if(free)try{await StartAndVerify(paths,job,version);}catch{Outcome(paths,id,"not-applied",version,"未修改应用文件，但原程序未能重新启动，请手动启动。");return 1;}
                }
                Outcome(paths,id,"not-applied",version);return 1;
            }
            try
            {
                using(var rootLock=new UpdateRootLock(paths.Root))
                {UpdatePackage.Check(rootLock.Acquired,"应用仍在运行，未覆盖其文件，请退出后恢复更新。");portable?.Restore();installed?.Restore();}
                string version=UpdatePackage.Read(paths.Root).Version;
                await StartAndVerify(paths,job,version);healthy=true;Outcome(paths,id,"rolledback",version,installed?.RecoveryNotice??"");Finish(paths,job,transactionDirectory,installed?.RecoveryNotice.Length>0);
            }
            catch{if(healthy){Finish(paths,job,transactionDirectory,installed?.RecoveryNotice.Length>0);return 1;}portable?.SetPhase("rollback-failed");installed?.SetPhase("rollback-failed");Outcome(paths,id,"recovery-required","");
                MessageBox.Show("更新未完成，原版本备份已保留。请退出弹幕伴侣后运行恢复组件：\r\n"+Path.Combine(paths.ProfileRoot,"updates",id,"helper","DanmuCompanion.Updater.exe"), "爆米花弹幕伴侣",MessageBoxButtons.OK,MessageBoxIcon.Error);}
            return 1;
        }
    }
    private static void Finish(ApplicationPaths paths,UpdateJob job,string transaction,bool retain=false)
    {
        try
        {
            string operation=Path.Combine(paths.ProfileRoot,"updates",job.OperationId);UpdateMaintenance.Complete(paths,operation,retain);
            if(!retain)UpdateStorage.RemoveOwned(UpdateTransaction.ParentFor(paths.Root),transaction);
            UpdateMaintenance.TryCleanupOperation(paths,job.Download.Operation);
            if(!retain)UpdateRecoveryEntry.Remove(paths,Path.Combine(operation,"helper","DanmuCompanion.Updater.exe"));
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private static async Task StartAndVerify(ApplicationPaths paths,UpdateJob job,string version)
    {
        using var pipe=new NamedPipeServerStream("DanmuUpdateHealth-"+job.OperationId,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var info=new ProcessStartInfo(paths.MainExecutable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=paths.Root};
        foreach(string arg in new[]{"--profile",job.Profile,"--data-root",job.DataRoot,"--update-handshake",job.OperationId,"--update-parent",Environment.ProcessId.ToString()})info.ArgumentList.Add(arg);if(job.Reopen)info.ArgumentList.Add("--open");
        using var process=Process.Start(info)??throw new IOException("更新后的应用未能启动。");
        try
        {
            var connection=pipe.WaitForConnectionAsync(timeout.Token);using var exitBudget=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var exit=process.WaitForExitAsync(exitBudget.Token);
            if(await Task.WhenAny(connection,exit)==exit){timeout.Cancel();throw new InvalidDataException("更新后的应用在完成启动握手前退出。");}
            await connection;exitBudget.Cancel();try{await exit;}catch(OperationCanceledException){}
            UpdatePipe.CheckPeer(pipe,process.Id,true);UpdatePipe.CheckImage(process.Id,paths.MainExecutable);
            using var response=await UpdatePipe.ReadAsync(pipe,timeout.Token);var value=response.RootElement;
            UpdatePackage.Check(!process.HasExited && value.GetProperty("operationId").GetString()==job.OperationId && value.GetProperty("version").GetString()==version && value.GetProperty("ready").GetBoolean(),"更新后的应用未完成原生界面与采样握手。");
            await UpdatePipe.WriteAsync(pipe,new{accepted=true},timeout.Token);
        }
        catch{if(!process.HasExited){UpdatePipe.CheckImage(process.Id,paths.MainExecutable);process.Kill(true);await process.WaitForExitAsync();}throw;}
    }
    internal static async Task WriteStartedAsync(ApplicationPaths paths,string operationId,int parent)
    {
        string directory=Path.Combine(paths.ProfileRoot,"updates",operationId);UpdatePackage.Check(Guid.TryParseExact(operationId,"N",out _) && UpdateStorage.Owns(Path.Combine(paths.ProfileRoot,"updates"),directory),"更新启动操作身份无效。");
        var identity=UpdateIdentity.Load(paths);UpdatePackage.Check(identity.Supported,"更新后的程序集版本或完整发行包身份无效。");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe=new NamedPipeClientStream(".","DanmuUpdateHealth-"+operationId,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);UpdatePipe.CheckPeer(pipe,parent,false);UpdatePipe.CheckImage(parent,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe"));
        await UpdatePipe.WriteAsync(pipe,new{operationId,version=identity.Version,ready=true},timeout.Token);
        using var response=await UpdatePipe.ReadAsync(pipe,timeout.Token);UpdatePackage.Check(response.RootElement.GetProperty("accepted").GetBoolean(),"更新启动验证未被确认。");
    }
    internal static async Task<int> RecoverAsync()
    {
        string directory=Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)),recovery=Path.Combine(directory,"recovery.json");
        if(!File.Exists(recovery)){MessageBox.Show("此组件由主程序启动，无需单独运行。","爆米花弹幕伴侣");return 0;}
        using var doc=UpdateStorage.ReadJson(recovery,16384);
        var job=doc.RootElement.GetProperty("job").Deserialize<UpdateJob>(UpdateStorage.Json);var paths=ValidateRecovery(job,directory);
        string transactionDirectory=UpdatePackage.SafeRoot(doc.RootElement.GetProperty("transactionDirectory").GetString());UpdatePackage.Check(UpdateStorage.Owns(UpdateTransaction.ParentFor(paths.Root),transactionDirectory),"更新恢复目录不匹配。");
        ValidateRecoveryJournal(paths,job,transactionDirectory);
        using var journal=UpdateStorage.ReadJson(Path.Combine(transactionDirectory,"journal.json"),4096);string phase=journal.RootElement.GetProperty("phase").GetString();
        UpdatePackage.Check(phase is "armed" or "applying" or "installed" or "starting" or "rolledback" or "rollback-failed","该操作不需要恢复原版本。");
        if(MessageBox.Show("恢复此次更新前的原版本？将保留个人设置和非应用文件。","爆米花弹幕伴侣",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return 0;
        UpdateInstallerTransaction recoveredInstaller=null;
        using(var rootLock=new UpdateRootLock(paths.Root))
        {UpdatePackage.Check(rootLock.Acquired,"弹幕伴侣仍在运行，请从托盘退出后再恢复，未更改任何应用文件。");
        if(job.Download.Distribution=="portable")UpdateTransaction.Load(transactionDirectory).Restore();else {recoveredInstaller=UpdateInstallerTransaction.Load(transactionDirectory);recoveredInstaller.Restore();}}
        string version=UpdatePackage.Read(paths.Root).Version;await StartAndVerify(paths,job,version);Outcome(paths,job.OperationId,"rolledback",version,recoveredInstaller?.RecoveryNotice??"");Finish(paths,job,transactionDirectory,recoveredInstaller?.RecoveryNotice.Length>0);return 0;
    }
    internal static void ValidateRecoveryJournal(ApplicationPaths paths,UpdateJob job,string directory)
    {
        using var doc=UpdateStorage.ReadJson(Path.Combine(directory,"journal.json"),4096);var journal=doc.RootElement;
        UpdatePackage.Check(string.Equals(UpdatePackage.SafeRoot(journal.GetProperty("targetRoot").GetString()),UpdatePackage.SafeRoot(paths.Root),StringComparison.OrdinalIgnoreCase) &&
            journal.GetProperty("version").GetString()==job.Download.Version && journal.GetProperty("variant").GetString()==job.Download.Variant &&
            (job.Download.Distribution=="installer" ? journal.TryGetProperty("kind",out var kind) && kind.GetString()=="installer" : !journal.TryGetProperty("kind",out _)),"更新恢复任务与事务目标不匹配，未修改任何目录。");
    }
    private static ApplicationPaths ValidateRecovery(UpdateJob job,string directory)
    {
        UpdatePackage.Check(job!=null && job.Download!=null && Guid.TryParseExact(job.OperationId,"N",out _),"恢复任务无效。");var paths=new ApplicationPaths(UpdatePackage.SafeRoot(job.ApplicationRoot),job.Profile,UpdatePackage.SafeRoot(job.DataRoot));
        UpdatePackage.Check(Path.GetFileName(directory)==job.OperationId && UpdateStorage.Owns(Path.Combine(paths.ProfileRoot,"updates"),directory) && job.Download.Variant is "standard" or "includeAPI" && job.Download.Distribution is "installer" or "portable","恢复任务所属证明无效。");return paths;
    }
}
