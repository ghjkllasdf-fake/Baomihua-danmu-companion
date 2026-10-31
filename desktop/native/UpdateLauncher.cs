using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed class UpdatePayloadGuard : IDisposable
{
    private readonly List<FileStream> streams=new();
    private readonly UpdateDirectories directories=new();
    internal UpdatePayloadGuard(string root,UpdatePackage package,Func<UpdateFile,bool> include=null)
    {
        try
        {
            directories.Hold(root);
            foreach(var file in package.Owned)
                if(include==null || include(file))
                {
                    string path=directories.File(root,file.Path);streams.Add(UpdateLauncher.GuardExecutable(path,file));
                }
        }
        catch{Dispose();throw;}
    }
    public void Dispose(){foreach(var stream in streams)stream.Dispose();streams.Clear();directories.Dispose();}
}

internal sealed class UpdateLease : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process worker;
    private bool committed;
    internal int WorkerId=>worker.Id;
    internal string Directory=>Path.GetDirectoryName(Path.GetDirectoryName(worker.MainModule.FileName));
    internal UpdateLease(NamedPipeServerStream pipe,Process worker){this.pipe=pipe;this.worker=worker;}
    internal async Task CommitAsync()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        UpdatePackage.Check(!worker.HasExited,"更新组件已停止，原程序继续运行。");
        await UpdatePipe.WriteAsync(pipe,new{apply=true},timeout.Token);
        using var response=await UpdatePipe.ReadAsync(pipe,timeout.Token);
        UpdatePipe.CheckPeer(pipe,worker.Id,true);
        UpdatePackage.Check(response.RootElement.TryGetProperty("armed",out var armed) && armed.GetBoolean() && !worker.HasExited,"更新组件未确认交接，原程序继续运行。");
        committed=true;
    }
    public void Dispose()
    {
        pipe.Dispose();if(!committed && !worker.HasExited){try{worker.Kill(true);worker.WaitForExit(5000);}catch(InvalidOperationException){}}
        worker.Dispose();
    }
}

internal static class UpdateLauncher
{
    internal static FileStream GuardExecutable(string path,UpdateFile expected)
    {
        var stream=UpdateFileIdentity.OpenGuard(path);
        try{UpdatePackage.Check(UpdatePackage.Hash(stream,expected.Length).Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase),"将运行的更新组件校验失败。");stream.Position=0;return stream;}
        catch{stream.Dispose();throw;}
    }
    internal static string VerifiedArchive(UpdateDownload download)
    {
        using var held=GuardArchive(download);return held.Name;
    }
    internal static FileStream GuardArchive(UpdateDownload download)
    {
        var primary=GuardExecutable(download.File,new UpdateFile(Path.GetFileName(download.File),download.Size,download.Checksum));
        if(download.Distribution=="portable")return primary;
        using(primary)
        {
        UpdatePackage.Check(UpdateCatalog.HashValid(download.InventoryChecksum) && UpdateCatalog.HashValid(download.ArchiveChecksum) && download.ArchiveSize>0 && download.ArchiveSize<=UpdateCatalog.MaxDownload,"安装更新缺少已校验的配套归档和清单。");
        string name=UpdateCatalog.ArtifactName(download.Version,download.Variant=="includeAPI","portable"),archive=UpdatePackage.SafeFile(download.Operation,name);
        return GuardExecutable(archive,new UpdateFile(name,download.ArchiveSize,download.ArchiveChecksum));
        }
    }
    internal static UpdatePayloadGuard PrepareHelper(UpdateDownload download,string helper,string cache,Action afterArchiveVerified=null,Action afterExtracted=null)
    {
        UpdatePackage incoming;
        // Hash and extract the same non-writable/non-renamable ZIP, never reopen its path.
        using(var archive=GuardArchive(download))
        {
            afterArchiveVerified?.Invoke();incoming=UpdatePackage.Extract(archive,helper,download.Version,download.Variant);
            VerifyInventory(download,incoming);CheckHelperPathBudget(cache,incoming);
        }
        afterExtracted?.Invoke();
        // Keep the trusted ZIP manifest, including its own hash, across the execution boundary.
        return new UpdatePayloadGuard(helper,incoming,file=>!file.Path.Contains('/') || file.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase));
    }
    internal static void VerifyInventory(UpdateDownload download,UpdatePackage incoming)
    {
        if(download.Distribution!="installer")return;
        using var input=File.OpenRead(UpdatePackage.SafeFile(download.Operation,UpdateCatalog.InventoryName(download.Version,download.Variant=="includeAPI")));
        byte[] raw=UpdatePackage.ReadLimited(input,4*1024*1024);
        UpdatePackage.Check(Convert.ToHexString(SHA256.HashData(raw)).Equals(download.InventoryChecksum,StringComparison.OrdinalIgnoreCase) && incoming.Manifest.AsSpan().SequenceEqual(raw),"安装器、归档和目标清单不一致。");
    }
    internal static async Task<string> ReadDiagnosticAsync(StreamReader input)
    {
        var text=new StringBuilder();var buffer=new char[256];
        try
        {
            int count;while((count=await input.ReadAsync(buffer).ConfigureAwait(false))>0)
                if(text.Length<4096)text.Append(buffer,0,Math.Min(count,4096-text.Length));
        }
        catch(IOException){}catch(ObjectDisposedException){}
        return text.ToString();
    }
    internal static void CheckHelperPathBudget(string cache,UpdatePackage package)
    {
        string helper=Path.Combine(cache,new string('0',32),"helper");
        UpdatePackage.Check(package.Files.Where(f=>!f.Path.Contains('/') && f.Path.EndsWith(".dll",StringComparison.Ordinal)).All(f=>Path.Combine(helper,f.Path).Length<260) &&
            Path.Combine(helper,"DanmuCompanion.Updater.runtimeconfig.json").Length<260,"更新数据目录过长，Windows 原生组件不能安全启动；请使用较短的数据目录或手动安装更新。原程序继续运行。");
    }
    internal static async Task<UpdateLease> PrepareAsync(ApplicationPaths paths,UpdateDownload download,bool reopen,CancellationToken token)
    {
        var previous=await Task.Run(()=>UpdatePackage.VerifyDirectory(paths.Root,UpdateIdentity.Load(paths).Version,download.Variant),token).ConfigureAwait(false);
        UpdatePackage.Check(UpdateVersion.Newer(download.Version,previous.Version) && UpdateIdentity.DistributionFor(paths.Root,previous.Variant)==download.Distribution,"待安装的更新与当前发行版不匹配。");
        string cache=Path.Combine(paths.ProfileRoot,"updates");UpdatePackage.Check(UpdateStorage.Owns(cache,download.Operation),"更新下载缺少所属证明。");
        UpdatePackage.Check(Path.GetFullPath(download.File)==Path.Combine(download.Operation,UpdateCatalog.ArtifactName(download.Version,download.Variant=="includeAPI",download.Distribution)),"更新附件路径无效。");
        UpdatePackage.Check(new DriveInfo(Path.GetPathRoot(cache)).AvailableFreeSpace>(download.Variant=="includeAPI" ? 400L : 210L)*1024*1024+32L*1024*1024,"更新组件暂存空间不足。");
        string directory=UpdateStorage.CreateOperation(cache,"worker"),helper=Path.Combine(directory,"helper");
        UpdatePayloadGuard executableGuard=null;
        try
        {
            executableGuard=await Task.Run(()=>
            {
                token.ThrowIfCancellationRequested();return PrepareHelper(download,helper,cache);
            },token).ConfigureAwait(false);
            NativeRuntime.ValidateApplication(new ApplicationPaths(helper));
        }
        catch{executableGuard?.Dispose();UpdateMaintenance.TryCleanupOperation(paths,directory);throw;}
        string id=Path.GetFileName(directory),executable=Path.Combine(helper,"DanmuCompanion.Updater.exe");
        var pipe=new NamedPipeServerStream("DanmuUpdate-"+id,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        // Give the worker independent, bounded startup diagnostics instead of the app's console.
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=helper};
        foreach(string arg in new[]{"--pipe",id,"--parent",Environment.ProcessId.ToString()})info.ArgumentList.Add(arg);
        Process worker=null;
        try
        {
            worker=Process.Start(info)??throw new InvalidOperationException("原生更新组件未能启动。");
            var diagnostics=ReadDiagnosticAsync(worker.StandardError);_ = ReadDiagnosticAsync(worker.StandardOutput);
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(TimeSpan.FromMinutes(3));
            var connection=pipe.WaitForConnectionAsync(budget.Token);using var exitBudget=CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            var exit=worker.WaitForExitAsync(exitBudget.Token);
            if(await Task.WhenAny(connection,exit).ConfigureAwait(false)==exit)
            {
                budget.Cancel();throw new InvalidOperationException("更新组件在连接前退出，原程序继续运行。退出码："+worker.ExitCode+" "+await diagnostics.ConfigureAwait(false));
            }
            await connection.ConfigureAwait(false);exitBudget.Cancel();try{await exit.ConfigureAwait(false);}catch(OperationCanceledException){}
            UpdatePipe.CheckPeer(pipe,worker.Id,true);UpdatePipe.CheckImage(worker.Id,executable);
            await UpdatePipe.WriteAsync(pipe,new UpdateJob(paths.Root,paths.DataRoot,Path.GetFileName(paths.ProfileRoot),download,reopen,id),budget.Token).ConfigureAwait(false);
            using var response=await UpdatePipe.ReadAsync(pipe,budget.Token).ConfigureAwait(false);
            UpdatePackage.Check(response.RootElement.TryGetProperty("ready",out var ready) && ready.GetBoolean(),response.RootElement.TryGetProperty("error",out var error) ? error.GetString() : "更新预检查未通过，原程序仍在运行。");
            return new UpdateLease(pipe,worker);
        }
        catch{pipe.Dispose();if(worker!=null){if(!worker.HasExited){worker.Kill(true);worker.WaitForExit(5000);}worker.Dispose();}UpdateMaintenance.TryCleanupOperation(paths,directory);throw;}
        finally{executableGuard?.Dispose();}
    }
}
