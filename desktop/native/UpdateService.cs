using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed class UpdateService : IDisposable
{
    private readonly ApplicationPaths paths;
    private readonly UpdateIdentity identity;
    private readonly UpdateTransport transport;
    private readonly string preferencesFile,cacheRoot;
    private readonly object sync=new();
    private readonly SemaphoreSlim operation=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource active;
    private UpdatePreferences preferences;
    private UpdateSnapshot view;
    private UpdateRelease selected;
    private UpdateDownload downloaded;
    private bool started,disposed;
    public event Action Changed;
    public UpdateSnapshot View {get{lock(sync)return view;}}
    public UpdateDownload Downloaded {get{lock(sync)return downloaded;}}
    public UpdateService(ApplicationPaths paths,UpdateIdentity identity=null,UpdateTransport transport=null)
    {
        this.paths=paths;this.identity=identity??UpdateIdentity.Load(paths);this.transport=transport??new UpdateTransport();
        cacheRoot=Path.Combine(paths.ProfileRoot,"updates");preferencesFile=Path.Combine(cacheRoot,"preferences.json");
        preferences=UpdateStorage.Load(preferencesFile,UpdateVersion.TryParse(this.identity.Version,out var version) && version.Stable ? "stable" : "beta",out string notice);
        view=new(this.identity.Supported ? "idle" : "unavailable",this.identity.Version,"",this.identity.Variant,this.identity.Distribution,preferences.Channel,
            preferences.AutoCheck,preferences.AutoDownload,preferences.CheckedAt,0,0,"",this.identity.Supported ? notice : "开发、候选或不完整的发行包不执行自动更新，请使用 GitHub 正式发行包。",0,this.identity.Supported,false,UpdateMaintenance.LastResult(paths));
    }
    private void Change(Func<UpdateSnapshot,UpdateSnapshot> change)
    {
        lock(sync){if(disposed)return;view=change(view) with{Revision=view.Revision+1};}Changed?.Invoke();
    }
    private void State(string state,string message="")=>Change(v=>v with{State=state,Message=message,CanInstall=state=="ready" && identity.Supported && File.Exists(Path.Combine(paths.Root,"DanmuCompanion.Updater.exe"))});
    public UpdateSnapshot SetPreferences(JsonElement payload)
    {
        if(View.State=="applying")throw new InvalidOperationException("正在交接更新安装，不能修改更新设置。");
        UpdatePackage.Unique(payload);UpdatePackage.Check(payload.ValueKind==JsonValueKind.Object && payload.EnumerateObject().Count()==3,"更新设置参数无效。");
        string channel=payload.GetProperty("channel").GetString();UpdatePackage.Check(channel is "beta" or "stable","不支持的更新通道。");
        if(!operation.Wait(0))throw new InvalidOperationException("更新操作进行中，请完成或取消后再修改设置。");
        try
        {
            var next=new UpdatePreferences(payload.GetProperty("autoCheck").GetBoolean(),payload.GetProperty("autoDownload").GetBoolean(),channel,preferences.CheckedAt);
            UpdateStorage.Save(preferencesFile,next);bool changed=next.Channel!=preferences.Channel;preferences=next;
            if(changed){UpdateDownload discarded;lock(sync){selected=null;discarded=downloaded;downloaded=null;}if(discarded!=null)UpdateMaintenance.TryCleanupOperation(paths,discarded.Operation);}
            Change(v=>v with{Channel=channel,AutoCheck=next.AutoCheck,AutoDownload=next.AutoDownload,Message="",State=changed ? identity.Supported ? "idle" : "unavailable" : v.State,
                AvailableVersion=changed ? "" : v.AvailableVersion,Notes=changed ? "" : v.Notes,DownloadedBytes=changed ? 0 : v.DownloadedBytes,TotalBytes=changed ? 0 : v.TotalBytes,CanInstall=changed ? false : v.CanInstall});
            return View;
        }
        finally{operation.Release();}
    }
    private async Task<UpdateSnapshot> Execute(string state,Func<CancellationToken,Task> action)
    {
        if(View.State=="applying")throw new InvalidOperationException("正在准备安装更新，请稍候。");
        if(!identity.Supported)throw new InvalidOperationException("此开发、候选或不完整的发行包不支持自动更新。");
        if(!await operation.WaitAsync(0).ConfigureAwait(false))throw new InvalidOperationException("更新操作正在进行，请稍候。");
        using var token=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);lock(sync)active=token;
        State(state);
        try{await action(token.Token).ConfigureAwait(false);}
        catch(OperationCanceledException)
        {State(token.IsCancellationRequested ? selected==null ? "idle" : "available" : "failed",token.IsCancellationRequested ? "更新操作已取消。" : "更新请求超时，请重试。");}
        catch(Exception error) when(UpdateStorage.MetadataError(error) || error is System.Net.Http.HttpRequestException)
        {State("failed",error is InvalidDataException or InvalidOperationException ? error.Message[..Math.Min(error.Message.Length,500)] : "检查或下载失败，请确认网络与磁盘空间后重试。");}
        finally{lock(sync)active=null;operation.Release();}
        return View;
    }
    public Task<UpdateSnapshot> CheckAsync()=>Execute("checking",async token=>
    {
        var release=await transport.DiscoverAsync(identity.Version,preferences.Channel,identity.Variant=="includeAPI",identity.Distribution,token).ConfigureAwait(false);
        lock(sync){selected=release;if(release==null || downloaded?.Version!=release.Version)downloaded=null;}
        if(release!=null && Downloaded==null)await RestoreDownload(release,token).ConfigureAwait(false);
        string stamp=DateTimeOffset.UtcNow.ToString("O");preferences=preferences with{CheckedAt=stamp};
        try{UpdateStorage.Save(preferencesFile,preferences);}catch(IOException){}catch(UnauthorizedAccessException){}
        Change(v=>v with{AvailableVersion=release?.Version??"",Notes=release?.Notes??"",CheckedAt=stamp,DownloadedBytes=downloaded==null ? 0 : v.DownloadedBytes,TotalBytes=release==null ? 0 : release.Artifact.Size+(release.Archive?.Size??0)});
        State(release==null ? "current" : downloaded==null ? "available" : "ready");
        if(release!=null && downloaded==null && preferences.AutoDownload)await DownloadCore(token).ConfigureAwait(false);
    });
    public Task<UpdateSnapshot> DownloadAsync()=>Execute("downloading",DownloadCore);
    private async Task DownloadCore(CancellationToken token)
    {
        var release=selected??throw new InvalidOperationException("请先检查是否存在适合当前发行版的更新。");
        if(downloaded!=null){State("ready");return;}
        long combined=release.Artifact.Size+(release.Archive?.Size??0);State("downloading");Change(v=>v with{DownloadedBytes=0,TotalBytes=combined});
        var sums=await transport.ReadTextAsync(release.Checksums.Url,65536,token).ConfigureAwait(false);
        UpdatePackage.Check(sums.Body.LongLength==release.Checksums.Size && (release.Checksums.Digest.Length==0 || Convert.ToHexString(SHA256.HashData(sums.Body)).Equals(release.Checksums.Digest,StringComparison.OrdinalIgnoreCase)),"GitHub 更新校验清单与附件记录不符。");
        string checksum=UpdateCatalog.Checksum(sums.Text,release.Artifact.Name),directory=UpdateStorage.CreateOperation(cacheRoot,"download");
        string target=Path.Combine(directory,release.Artifact.Name);long nextReport=0;
        try
        {
            string inventoryChecksum="";
            string archiveChecksum="";
            if(release.Inventory!=null)
            {
                inventoryChecksum=UpdateCatalog.Checksum(sums.Text,release.Inventory.Name);
                string inventoryPath=Path.Combine(directory,release.Inventory.Name);
                await transport.DownloadAsync(release.Inventory,inventoryPath,inventoryChecksum,null,token).ConfigureAwait(false);
                using var inventory=File.OpenRead(inventoryPath);UpdatePackage.Parse(UpdatePackage.ReadLimited(inventory,4*1024*1024),release.Version,identity.Variant);
            }
            if(release.Archive!=null)
            {
                archiveChecksum=UpdateCatalog.Checksum(sums.Text,release.Archive.Name);
                await transport.DownloadAsync(release.Archive,Path.Combine(directory,release.Archive.Name),archiveChecksum,(done,_)=>Change(v=>v with{DownloadedBytes=done,TotalBytes=combined}),token).ConfigureAwait(false);
            }
            await transport.DownloadAsync(release.Artifact,target,checksum,(done,total)=>
            {
                long now=Environment.TickCount64;if(done!=total && now<Interlocked.Read(ref nextReport))return;Interlocked.Exchange(ref nextReport,now+200);
                Change(v=>v with{DownloadedBytes=done+(release.Archive?.Size??0),TotalBytes=combined});
            },token).ConfigureAwait(false);
            State("verifying");UpdatePackage.VerifyFile(target,new UpdateFile(release.Artifact.Name,release.Artifact.Size,checksum));
            var completed=new UpdateDownload(target,checksum,release.Artifact.Size,release.Version,identity.Variant,identity.Distribution,directory,inventoryChecksum,archiveChecksum,release.Archive?.Size??0);
            UpdateStorage.Atomic(Path.Combine(directory,"download.json"),JsonSerializer.SerializeToUtf8Bytes(completed,UpdateStorage.Json));
            lock(sync)downloaded=completed;
            State("ready","更新已下载并通过校验，确认后才会重启并安装。");
        }
        catch
        {
            try{UpdateStorage.RemoveOwned(cacheRoot,directory);}catch(IOException){}catch(UnauthorizedAccessException){}throw;
        }
    }
    private async Task RestoreDownload(UpdateRelease release,CancellationToken token)
    {
        if(!Directory.Exists(cacheRoot))return;
        string sums=null;
        foreach(string directory in Directory.EnumerateDirectories(cacheRoot).Take(100))
        {
            try
            {
                if(!UpdateStorage.Owns(cacheRoot,directory) || !File.Exists(Path.Combine(directory,"download.json")))continue;
                using var record=UpdateStorage.ReadJson(Path.Combine(directory,"download.json"),16384);var candidate=record.RootElement.Deserialize<UpdateDownload>(UpdateStorage.Json);
                if(candidate==null || candidate.Version!=release.Version || candidate.Variant!=identity.Variant || candidate.Distribution!=identity.Distribution || candidate.Size!=release.Artifact.Size ||
                    release.Archive==null && candidate.ArchiveSize!=0 ||
                    !string.Equals(candidate.Operation,directory,StringComparison.OrdinalIgnoreCase) || !string.Equals(candidate.File,Path.Combine(directory,release.Artifact.Name),StringComparison.OrdinalIgnoreCase))continue;
                if(sums==null)
                {
                    var text=await transport.ReadTextAsync(release.Checksums.Url,65536,token).ConfigureAwait(false);
                    UpdatePackage.Check(text.Body.LongLength==release.Checksums.Size && (release.Checksums.Digest.Length==0 || Convert.ToHexString(SHA256.HashData(text.Body)).Equals(release.Checksums.Digest,StringComparison.OrdinalIgnoreCase)),"更新缓存校验清单不匹配。");sums=text.Text;
                }
                UpdatePackage.Check(candidate.Checksum==UpdateCatalog.Checksum(sums,release.Artifact.Name) && (release.Artifact.Digest.Length==0 || candidate.Checksum==release.Artifact.Digest),"更新缓存摘要与 GitHub 不匹配。");
                UpdatePackage.VerifyFile(UpdatePackage.SafeFile(directory,release.Artifact.Name),new UpdateFile(release.Artifact.Name,candidate.Size,candidate.Checksum));
                if(release.Inventory!=null)
                {
                    UpdatePackage.Check(candidate.InventoryChecksum==UpdateCatalog.Checksum(sums,release.Inventory.Name),"更新缓存目标清单摘要不匹配。");
                    UpdatePackage.VerifyFile(UpdatePackage.SafeFile(directory,release.Inventory.Name),new UpdateFile(release.Inventory.Name,release.Inventory.Size,candidate.InventoryChecksum));
                }
                if(release.Archive!=null)
                {
                    UpdatePackage.Check(candidate.ArchiveSize==release.Archive.Size && candidate.ArchiveChecksum==UpdateCatalog.Checksum(sums,release.Archive.Name),"更新缓存归档校验值不匹配。");
                    UpdatePackage.VerifyFile(UpdatePackage.SafeFile(directory,release.Archive.Name),new UpdateFile(release.Archive.Name,candidate.ArchiveSize,candidate.ArchiveChecksum));
                }
                long combined=candidate.Size+candidate.ArchiveSize;lock(sync)downloaded=candidate;Change(v=>v with{DownloadedBytes=combined,TotalBytes=combined});return;
            }
            catch(Exception error) when(UpdateStorage.MetadataError(error)){}
        }
    }
    public void Cancel(){lock(sync){try{active?.Cancel();}catch(ObjectDisposedException){}}}
    public async Task<UpdateLease> PrepareInstallAsync(string version,bool reopen)
    {
        if(!identity.Supported || View.State!="ready" || downloaded==null || downloaded.Version!=version)throw new InvalidOperationException("待安装版本已变化，请重新检查并下载更新。");
        if(!await operation.WaitAsync(0))throw new InvalidOperationException("更新操作正在进行。");
        using var token=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);lock(sync)active=token;State("applying","正在预检查更新，完成后退出并安装。");
        try{return await UpdateLauncher.PrepareAsync(paths,downloaded,reopen,token.Token);}
        catch(Exception error){State("failed","更新预检查未通过，原程序继续运行。"+(error is InvalidDataException or InvalidOperationException ? error.Message[..Math.Min(error.Message.Length,400)] : ""));throw new InvalidOperationException(View.Message);}
        finally{lock(sync)active=null;operation.Release();}
    }
    internal void InstallFailed()=>State("failed","安装交接未完成，原程序继续运行，请重试。");
    public Uri ReleasePage {get{lock(sync)return new Uri("https://github.com/"+UpdateCatalog.Repository+"/releases"+(selected==null ? "" : "/tag/"+Uri.EscapeDataString(selected.Tag)));}}
    public void StartAutomatic()
    {
        lock(sync){if(started || disposed || !identity.Supported || Path.GetFileName(paths.ProfileRoot)!="app")return;started=true;}
        _=AutomaticLoop();
    }
    private async Task AutomaticLoop()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20+Random.Shared.Next(10)),lifetime.Token).ConfigureAwait(false);
            while(!lifetime.IsCancellationRequested)
            {
                await Task.Run(()=>UpdateMaintenance.Cleanup(paths,Downloaded?.Operation),lifetime.Token).ConfigureAwait(false);
                Change(v=>v with{LastResult=UpdateMaintenance.LastResult(paths)});
                var current=View;
                bool due=!DateTimeOffset.TryParse(current.CheckedAt,out var checkedAt) || checkedAt>DateTimeOffset.UtcNow || DateTimeOffset.UtcNow-checkedAt>=TimeSpan.FromHours(6);
                if(current.AutoCheck && due){try{await CheckAsync().ConfigureAwait(false);}catch(InvalidOperationException){}}
                await Task.Delay(TimeSpan.FromHours(6),lifetime.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException){}catch(ObjectDisposedException){}
    }
    public void Dispose()
    {
        lock(sync){if(disposed)return;disposed=true;lifetime.Cancel();try{active?.Cancel();}catch(ObjectDisposedException){}}
        transport.Dispose();
    }
}
