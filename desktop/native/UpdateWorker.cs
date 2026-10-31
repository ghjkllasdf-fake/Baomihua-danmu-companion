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
        var incoming=UpdatePackage.VerifyDirectory(helper,job.Download.Version,job.Download.Variant);UpdateLauncher.VerifyInventory(job.Download,incoming);
        return paths;
    }
    private static string SafeError(Exception error)=>error is InvalidDataException or InvalidOperationException or ArgumentException ? error.Message[..Math.Min(error.Message.Length,500)] : "更新组件遇到文件、权限或启动问题，未确认更新成功。";
    internal static string RepairAdvice(string distribution)=>distribution=="installer" ? "请退出伴侣，用同版本完整安装器在原安装位置修复；遇到外来文件冲突时不要强行覆盖。" :
        "请退出伴侣，将同版本完整 ZIP 解压到新的空目录运行；保留原目录，不要混合覆盖外来文件。安装器不能在 Portable 目录内修复。";
    private static void Outcome(ApplicationPaths paths,string id,string status,string version,string detail="")=>UpdateStorage.Atomic(Path.Combine(paths.ProfileRoot,"updates","last-result.json"),JsonSerializer.SerializeToUtf8Bytes(new{
        operationId=id,status,version,detail,completedAt=DateTimeOffset.UtcNow.ToString("O")}));
    internal static async Task<int> RunAsync(string id,int parent)
    {
        UpdatePackage.Check(Guid.TryParseExact(id,"N",out _) && parent>0,"更新组件启动参数无效。");
        using var executionGuard=new UpdatePayloadGuard(AppContext.BaseDirectory,UpdatePackage.Read(AppContext.BaseDirectory),
            file=>!file.Path.Contains('/') || file.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase));
        using var budget=new CancellationTokenSource(TimeSpan.FromMinutes(3));using var pipe=new NamedPipeClientStream(".","DanmuUpdate-"+id,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(budget.Token);UpdatePipe.CheckPeer(pipe,parent,false);
        using var parentProcess=Process.GetProcessById(parent);_=parentProcess.SafeHandle;
        ApplicationPaths paths=null;UpdateJob job=null;UpdateApply apply=null;string directory="",operation=Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        try
        {
            using var message=await UpdatePipe.ReadAsync(pipe,budget.Token);job=message.RootElement.Deserialize<UpdateJob>(UpdateStorage.Json);paths=Validate(job,AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            UpdatePackage.Check(!parentProcess.HasExited && string.Equals(parentProcess.MainModule?.FileName,paths.MainExecutable,StringComparison.OrdinalIgnoreCase),"更新父进程身份无效。");
            UpdateLauncher.VerifiedArchive(job.Download);
            var incoming=UpdatePackage.Read(AppContext.BaseDirectory);UpdatePackage.Check(new DriveInfo(Path.GetPathRoot(paths.Root)).AvailableFreeSpace>incoming.Bytes+32L*1024*1024,"原安装位置的覆盖更新空间不足。");
            directory=UpdateStorage.CreateOperation(UpdateInstalledOwnership.ParentFor(paths.Root),"direct-update");
            apply=UpdateApply.Prepare(paths.Root,directory,AppContext.BaseDirectory,job.Download.Version,job.Download.Variant);
            using var self=Process.GetCurrentProcess();UpdateStorage.Atomic(Path.Combine(operation,"worker.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=2,kind="direct-overwrite",job,applyDirectory=directory,workerPid=Environment.ProcessId,workerStartTicks=self.StartTime.ToUniversalTime().Ticks},UpdateStorage.Json));
            await UpdatePipe.WriteAsync(pipe,new{ready=true},budget.Token);
        }
        catch(Exception error)
        {
            if(paths!=null && directory.Length>0 && UpdateApply.CleanupUnapplied(paths.Root,directory))try{UpdateStorage.RemoveOwned(UpdateInstalledOwnership.ParentFor(paths.Root),directory);}catch(Exception failure) when(UpdateStorage.MetadataError(failure)){}
            await UpdatePipe.WriteAsync(pipe,new{ready=false,error=SafeError(error)},CancellationToken.None);return 1;
        }
        bool parentExited=false,healthy=false;
        try
        {
            using(var command=await UpdatePipe.ReadAsync(pipe,budget.Token))UpdatePackage.Check(command.RootElement.GetProperty("apply").GetBoolean(),"更新安装未得到主程序确认。");
            apply.SetPhase("armed");await UpdatePipe.WriteAsync(pipe,new{armed=true},budget.Token);
            // Once ACK is sent, retain the exact parent handle until it really exits.
            // A preflight timeout must never strand a parent that is already handing off.
            await WaitForCommittedParent(parentProcess);parentExited=true;
            using(var rootLock=new UpdateRootLock(paths.Root))
            {
                UpdatePackage.Check(rootLock.Acquired,"原目录已有另一个实例启动，未替换运行中的应用。");
                UpdatePackage.Check(UpdateIdentity.DistributionFor(paths.Root,job.Download.Variant)==job.Download.Distribution,"原安装登记已改变，未覆盖。");
                UpdateLauncher.VerifiedArchive(job.Download);UpdateLauncher.VerifyInventory(job.Download,apply.Next);apply.Apply();
                if(job.Download.Distribution=="installer")
                {
                    using var downloadDirectories=new UpdateDirectories();downloadDirectories.Hold(job.Download.Operation);
                    using var executableGuard=UpdateLauncher.GuardExecutable(job.Download.File,new UpdateFile(Path.GetFileName(job.Download.File),job.Download.Size,job.Download.Checksum));
                    var info=new ProcessStartInfo(job.Download.File){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
                    foreach(string arg in new[]{"/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/SP-","/DIR="+paths.Root,"/UPDATEJOURNAL="+directory,"/LOG="+Path.Combine(directory,"setup.log")})info.ArgumentList.Add(arg);
                    using var setup=Process.Start(info)??throw new IOException("更新安装器未能启动。");using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(20));
                    try{await setup.WaitForExitAsync(timeout.Token);}catch{if(!setup.HasExited){setup.Kill(true);await setup.WaitForExitAsync();}throw;}
                    UpdatePackage.Check(setup.ExitCode==0,"更新安装器未成功完成，请重新安装修复。");apply.AcceptInstalled();
                }
            }
            apply.SetPhase("starting");await StartAndVerify(paths,job,apply.Next);healthy=true;
            Outcome(paths,id,"complete",job.Download.Version);apply.CompleteMetadata();Finish(paths,job,directory);return 0;
        }
        catch(Exception)
        {
            if(healthy){Finish(paths,job,directory);return 0;}
            if(!apply.Mutated)
            {
                if(!parentExited && parentProcess.HasExited)parentExited=true;
                try{apply.SetPhase("not-applied");}catch(Exception error) when(UpdateStorage.MetadataError(error)){}
                string detail="";
                if(parentExited)
                {
                    bool free;using(var rootLock=new UpdateRootLock(paths.Root))free=rootLock.Acquired;
                    if(free)try{UpdatePackage.VerifyDirectory(paths.Root,apply.Previous.Version,apply.Previous.Variant);await StartAndVerify(paths,job,apply.Previous);}catch{detail="原文件未替换，但原程序未能重新启动，请手动启动或重新安装。";}
                }
                try{Outcome(paths,id,"not-applied",apply.Previous.Version,detail);}catch(Exception error) when(UpdateStorage.MetadataError(error)){}
                Finish(paths,job,directory,"not-applied");return 1;
            }
            string repair="已开始原位覆盖，未确认新版正常运行；不提供备份或回滚。"+RepairAdvice(job.Download.Distribution);
            try{apply.SetPhase("repair-required");Outcome(paths,id,"repair-required",job.Download.Version,repair);}catch(Exception error) when(UpdateStorage.MetadataError(error)){}
            MessageBox.Show(repair+"\r\n原目录："+paths.Root,"爆米花弹幕伴侣",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;
        }
    }
    internal static Task WaitForCommittedParent(Process parent)=>parent.WaitForExitAsync();
    internal static async Task LaunchInstalled(ApplicationPaths paths,UpdatePackage expected,string stage)
    {
        string cache=Path.Combine(paths.ProfileRoot,"updates"),operation=UpdateStorage.CreateOperation(cache,"installer-start"),id=Path.GetFileName(operation);
        using var self=Process.GetCurrentProcess();
        try
        {
            using(var rootLock=new UpdateRootLock(paths.Root))UpdatePackage.Check(rootLock.Acquired,"程序已在运行，请从托盘打开。未重复启动。");
            UpdateStorage.Atomic(Path.Combine(operation,"launch.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=1,targetRoot=paths.Root,version=expected.Version,variant=expected.Variant,
                stage=UpdatePackage.SafeRoot(stage),parentPid=Environment.ProcessId,parentStartTicks=self.StartTime.ToUniversalTime().Ticks},UpdateStorage.Json));
            await StartAndVerify(paths,new UpdateJob(paths.Root,paths.DataRoot,Path.GetFileName(paths.ProfileRoot),null,true,id),expected);
        }
        catch(Exception error)
        {
            Console.Error.WriteLine("Installed launch readiness failed: "+SafeError(error));
            if(Path.GetFileName(paths.ProfileRoot)=="app")MessageBox.Show("程序文件已安装，但启动验证未通过。请从托盘退出伴侣后使用同版本安装器修复；个人设置保留，不会恢复旧版本。","爆米花弹幕伴侣",MessageBoxButtons.OK,MessageBoxIcon.Error);
            throw;
        }
        finally{try{UpdateStorage.RemoveOwned(cache,operation);}catch(Exception error) when(UpdateStorage.MetadataError(error)){}}
    }
    private static void Finish(ApplicationPaths paths,UpdateJob job,string directory,string terminal="complete")
    {
        try
        {
            UpdateMaintenance.Complete(paths,Path.Combine(paths.ProfileRoot,"updates",job.OperationId),terminal);
            if(terminal=="not-applied" && !UpdateApply.CleanupUnapplied(paths.Root,directory))return;
            UpdateStorage.RemoveOwned(UpdateInstalledOwnership.ParentFor(paths.Root),directory);UpdateMaintenance.TryCleanupOperation(paths,job.Download.Operation);
        }
        catch(Exception error) when(UpdateStorage.MetadataError(error)){}
    }
    private static async Task StartAndVerify(ApplicationPaths paths,UpdateJob job,UpdatePackage expected)
    {
        string version=expected.Version;using var payloadGuard=new UpdatePayloadGuard(paths.Root,expected);
        using var pipe=new NamedPipeServerStream("DanmuUpdateHealth-"+job.OperationId,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var info=new ProcessStartInfo(paths.MainExecutable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=paths.Root,RedirectStandardError=true,StandardErrorEncoding=System.Text.Encoding.UTF8};
        foreach(string arg in new[]{"--profile",job.Profile,"--data-root",job.DataRoot,"--update-handshake",job.OperationId,"--update-parent",Environment.ProcessId.ToString()})info.ArgumentList.Add(arg);if(job.Reopen)info.ArgumentList.Add("--open");
        using var process=Process.Start(info)??throw new IOException("更新后的应用未能启动。");
        var diagnostics=UpdateLauncher.ReadDiagnosticAsync(process.StandardError);
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
        catch{if(!process.HasExited){UpdatePipe.CheckImage(process.Id,paths.MainExecutable);process.Kill(true);await process.WaitForExitAsync();}Console.Error.WriteLine("Native readiness diagnostic: "+await diagnostics);throw;}
    }
    internal static async Task WriteStartedAsync(ApplicationPaths paths,string operationId,int parent)
    {
        string directory=Path.Combine(paths.ProfileRoot,"updates",operationId);UpdatePackage.Check(Guid.TryParseExact(operationId,"N",out _) && UpdateStorage.Owns(Path.Combine(paths.ProfileRoot,"updates"),directory),"更新启动操作身份无效。");
        var identity=UpdateIdentity.Load(paths);UpdatePackage.Check(identity.Supported,"更新后的程序集版本或完整发行包身份无效。");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe=new NamedPipeClientStream(".","DanmuUpdateHealth-"+operationId,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);UpdatePipe.CheckPeer(pipe,parent,false);
        using var owner=UpdateStorage.ReadJson(Path.Combine(directory,"owner.json"),4096);
        if(owner.RootElement.GetProperty("purpose").GetString()=="installer-start")
        {
            using var launch=UpdateStorage.ReadJson(Path.Combine(directory,"launch.json"),4096);var record=launch.RootElement;using var process=Process.GetProcessById(parent);
            string stage=UpdatePackage.SafeRoot(record.GetProperty("stage").GetString());var expected=UpdatePackage.Read(stage,identity.Version,identity.Variant);
            UpdatePackage.Check(record.GetProperty("schemaVersion").GetInt32()==1 && record.GetProperty("parentPid").GetInt32()==parent && record.GetProperty("parentStartTicks").GetInt64()==process.StartTime.ToUniversalTime().Ticks &&
                string.Equals(UpdatePackage.SafeRoot(record.GetProperty("targetRoot").GetString()),UpdatePackage.SafeRoot(paths.Root),StringComparison.OrdinalIgnoreCase) && record.GetProperty("version").GetString()==identity.Version && record.GetProperty("variant").GetString()==identity.Variant &&
                expected.Manifest.AsSpan().SequenceEqual(UpdatePackage.Read(paths.Root).Manifest),"安装后启动记录与可信安装包不匹配。");
            UpdatePipe.CheckImage(parent,Path.Combine(stage,"DanmuCompanion.Updater.exe"));
        }
        else UpdatePipe.CheckImage(parent,Path.Combine(directory,"helper","DanmuCompanion.Updater.exe"));
        await UpdatePipe.WriteAsync(pipe,new{operationId,version=identity.Version,ready=true},timeout.Token);
        using var response=await UpdatePipe.ReadAsync(pipe,timeout.Token);UpdatePackage.Check(response.RootElement.GetProperty("accepted").GetBoolean(),"更新启动验证未被确认。");
    }
}
