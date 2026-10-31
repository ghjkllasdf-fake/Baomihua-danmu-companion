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
    private readonly TimeProvider clock;
    private readonly int startupJitter;
    private readonly string preferencesFile,cacheRoot,retryFile;
    private readonly object sync=new();
    private readonly SemaphoreSlim operation=new(1,1);
    private readonly SemaphoreSlim scheduleChanged=new(0,1);
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource active;
    private UpdatePreferences preferences;
    private UpdateSnapshot view;
    private UpdateRelease selected;
    private UpdateDownload downloaded;
    private bool started,disposed;
    private Task automaticTask=Task.CompletedTask;
    private Task workerCompletion=Task.CompletedTask;
    private long automaticStartedAt;
    private long? lastCheckAttempt,failureStartedAt;
    private DateTimeOffset attemptedAt=DateTimeOffset.MinValue;
    private TimeSpan failureDelay;
    private int checkFailures;
    private bool serverBackoff;
    private string retryNotice="";
    internal Task AutomaticCompletion=>automaticTask;
    public event Action Changed;
    public UpdateSnapshot View {get{lock(sync)return view;}}
    public UpdateDownload Downloaded {get{lock(sync)return downloaded;}}
    public UpdateService(ApplicationPaths paths,UpdateIdentity identity=null,UpdateTransport transport=null,TimeProvider clock=null,int? startupJitter=null)
    {
        this.clock=clock??TimeProvider.System;this.startupJitter=startupJitter??Random.Shared.Next(10);
        if(this.startupJitter is <0 or >9)throw new ArgumentOutOfRangeException(nameof(startupJitter));
        this.paths=paths;this.identity=identity??UpdateIdentity.Load(paths);this.transport=transport??new UpdateTransport(clock:this.clock);
        cacheRoot=Path.Combine(paths.ProfileRoot,"updates");preferencesFile=Path.Combine(cacheRoot,"preferences.json");
        retryFile=Path.Combine(cacheRoot,"retry.json");
        var retry=UpdateRetryStorage.Load(retryFile,this.clock.GetUtcNow(),out retryNotice);
        checkFailures=retry.Failures;
        attemptedAt=retry.AttemptedAt>this.clock.GetUtcNow() ? this.clock.GetUtcNow() : retry.AttemptedAt;
        if(retry.NotBefore>this.clock.GetUtcNow()){failureStartedAt=this.clock.GetTimestamp();failureDelay=retry.NotBefore-this.clock.GetUtcNow();serverBackoff=retry.ServerLimited;}
        preferences=UpdateStorage.Load(preferencesFile,UpdateVersion.TryParse(this.identity.Version,out var version) && version.Stable ? "stable" : "beta",out string notice);
        view=new(this.identity.Supported ? "idle" : "unavailable",this.identity.Version,"",this.identity.Variant,this.identity.Distribution,preferences.Channel,
            preferences.AutoCheck,preferences.AutoDownload,preferences.CheckedAt,0,0,"",this.identity.Supported ? notice+retryNotice : "开发、候选或不完整的发行包不执行自动更新，请使用 GitHub 正式发行包。",0,this.identity.Supported,false,UpdateMaintenance.LastResult(paths),preferences.CheckIntervalHours);
    }
    private void Change(Func<UpdateSnapshot,UpdateSnapshot> change)
    {
        lock(sync){if(disposed)return;view=change(view) with{Revision=view.Revision+1};}Changed?.Invoke();
    }
    private void State(string state,string message="")=>Change(v=>v with{State=state,Message=message[..Math.Min(message.Length,500-retryNotice.Length)]+retryNotice,CanInstall=state=="ready" && identity.Supported && File.Exists(Path.Combine(paths.Root,"DanmuCompanion.Updater.exe"))});
    public UpdateSnapshot SetPreferences(JsonElement payload)
    {
        if(View.State=="applying")throw new InvalidOperationException("正在交接更新安装，不能修改更新设置。");
        UpdatePackage.Unique(payload);UpdatePackage.Check(payload.ValueKind==JsonValueKind.Object && payload.EnumerateObject().Count()==(payload.TryGetProperty("checkIntervalHours",out _) ? 4 : 3),"更新设置参数无效。");
        string channel=payload.GetProperty("channel").GetString();UpdatePackage.Check(channel is "beta" or "stable","不支持的更新通道。");
        if(!operation.Wait(0))throw new InvalidOperationException("更新操作进行中，请完成或取消后再修改设置。");
        try
        {
            if(View.State=="applying")throw new InvalidOperationException("正在交接更新安装，不能修改更新设置。");
            var next=new UpdatePreferences(payload.GetProperty("autoCheck").GetBoolean(),payload.GetProperty("autoDownload").GetBoolean(),channel,preferences.CheckedAt,UpdateStorage.ReadInterval(payload,preferences.CheckIntervalHours));
            UpdateStorage.Save(preferencesFile,next);bool changed=next.Channel!=preferences.Channel;preferences=next;
            if(changed){UpdateDownload discarded;lock(sync){selected=null;discarded=downloaded;downloaded=null;}if(discarded!=null)UpdateMaintenance.TryCleanupOperation(paths,discarded.Operation);}
            Change(v=>v with{Channel=channel,AutoCheck=next.AutoCheck,AutoDownload=next.AutoDownload,CheckIntervalHours=next.CheckIntervalHours,Message=retryNotice,State=changed ? identity.Supported ? "idle" : "unavailable" : v.State,
                AvailableVersion=changed ? "" : v.AvailableVersion,Notes=changed ? "" : v.Notes,DownloadedBytes=changed ? 0 : v.DownloadedBytes,TotalBytes=changed ? 0 : v.TotalBytes,CanInstall=changed ? false : v.CanInstall});
            return View;
        }
        finally{operation.Release();WakeAutomatic();}
    }
    private async Task<UpdateSnapshot> Execute(string state,Func<CancellationToken,Task> action,bool automatic=false)
    {
        if(View.State=="applying")throw new InvalidOperationException("正在准备安装更新，请稍候。");
        if(!identity.Supported)throw new InvalidOperationException("此开发、候选或不完整的发行包不支持自动更新。");
        if(!await operation.WaitAsync(0).ConfigureAwait(false))throw new InvalidOperationException("更新操作正在进行，请稍候。");
        if(View.State=="applying"){operation.Release();throw new InvalidOperationException("正在准备安装更新，请稍候。");}
        if(automatic && NextAutomaticDelay()!=TimeSpan.Zero){operation.Release();return View;}
        using var token=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);lock(sync)active=token;
        State(state);
        try{await action(token.Token).ConfigureAwait(false);}
        catch(OperationCanceledException)
        {if(state=="checking" && !token.IsCancellationRequested)RecordFailure(null);else if(state=="checking" && retryNotice.Length==0)PersistRetry();State(token.IsCancellationRequested ? selected==null ? "idle" : "available" : "failed",token.IsCancellationRequested ? "更新操作已取消。" : "更新请求超时，请重试。");}
        catch(Exception error) when(UpdateStorage.MetadataError(error) || error is System.Net.Http.HttpRequestException)
        {if(state=="checking" || error is UpdateRateLimitException)RecordFailure(error);State("failed",error is InvalidDataException or InvalidOperationException ? error.Message[..Math.Min(error.Message.Length,400)] : "检查或下载失败，请确认网络与磁盘空间后重试。");}
        finally{lock(sync)active=null;operation.Release();WakeAutomatic();}
        return View;
    }
    public Task<UpdateSnapshot> CheckAsync()=>Check(false);
    private Task<UpdateSnapshot> Check(bool automatic)=>Execute("checking",async token=>
    {
        lock(sync){lastCheckAttempt=clock.GetTimestamp();attemptedAt=clock.GetUtcNow();}
        var release=await transport.DiscoverAsync(identity.Version,preferences.Channel,identity.Variant=="includeAPI",identity.Distribution,token).ConfigureAwait(false);
        lock(sync){selected=release;if(release==null || downloaded?.Version!=release.Version)downloaded=null;}
        bool assetLimited;lock(sync)assetLimited=serverBackoff && FailureRemaining()>TimeSpan.Zero;
        if(release!=null && Downloaded==null && !assetLimited)await RestoreDownload(release,token).ConfigureAwait(false);
        string stamp=clock.GetUtcNow().ToString("O");preferences=preferences with{CheckedAt=stamp};
        try{UpdateStorage.Save(preferencesFile,preferences);}catch(IOException){}catch(UnauthorizedAccessException){}
        Change(v=>v with{AvailableVersion=release?.Version??"",Notes=release?.Notes??"",CheckedAt=stamp,DownloadedBytes=downloaded==null ? 0 : v.DownloadedBytes,TotalBytes=release==null ? 0 : release.Artifact.Size+(release.Archive?.Size??0)});
        State(release==null ? "current" : downloaded==null ? "available" : "ready",assetLimited && release!=null && downloaded==null ? "发现更新；附件自动下载等待 GitHub 限流期限结束。" : "");
        if(release!=null && downloaded==null && preferences.AutoDownload && !assetLimited)await DownloadCore(token).ConfigureAwait(false);
        ResetFailure();
    },automatic);
    private TimeSpan FailureRemaining()=>failureStartedAt.HasValue ? failureDelay-clock.GetElapsedTime(failureStartedAt.Value) : TimeSpan.Zero;
    private void RecordFailure(Exception error)
    {
        lock(sync)
        {
            var remaining=FailureRemaining();serverBackoff=error is UpdateRateLimitException || serverBackoff && remaining>TimeSpan.Zero;
            checkFailures=Math.Min(checkFailures+1,3);failureDelay=TimeSpan.FromHours(6*(1<<(checkFailures-1)));
            if(error is UpdateRateLimitException limited && limited.RetryAfter>failureDelay)failureDelay=limited.RetryAfter;
            if(remaining>failureDelay)failureDelay=remaining;
            var maximum=DateTimeOffset.MaxValue-clock.GetUtcNow();if(failureDelay>maximum)failureDelay=maximum;
            failureStartedAt=clock.GetTimestamp();
        }
        PersistRetry();
    }
    private void ResetFailure()
    {
        lock(sync){checkFailures=0;if(!serverBackoff || FailureRemaining()<=TimeSpan.Zero){failureStartedAt=null;failureDelay=TimeSpan.Zero;serverBackoff=false;}}
        string oldNotice=retryNotice;PersistRetry();
        if(oldNotice.Length>0 || retryNotice.Length>0)Change(v=>v with{Message=(oldNotice.Length>0 && v.Message.EndsWith(oldNotice,StringComparison.Ordinal) ? v.Message[..^oldNotice.Length] : v.Message)+retryNotice});
    }
    private void PersistRetry()
    {
        UpdateRetry retry;lock(sync){var remaining=FailureRemaining();retry=remaining>TimeSpan.Zero ? new(UpdateRetryStorage.Deadline(clock.GetUtcNow(),remaining),checkFailures,serverBackoff,attemptedAt) : new(DateTimeOffset.MinValue,checkFailures,false,attemptedAt);}
        try{UpdateRetryStorage.Save(retryFile,retry);retryNotice="";}
        catch(Exception error) when(UpdateStorage.MetadataError(error)){retryNotice="检查退避未能保存，重启后可能重新检查。";}
    }
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
        finally{lock(sync)active=null;operation.Release();WakeAutomatic();}
    }
    internal void InstallFailed(){State("failed","安装交接未完成，原程序继续运行，请重试。");WakeAutomatic();}
    public Uri ReleasePage {get{lock(sync)return new Uri("https://github.com/"+UpdateCatalog.Repository+"/releases"+(selected==null ? "" : "/tag/"+Uri.EscapeDataString(selected.Tag)));}}
    public void StartAutomatic()
    {
        lock(sync){if(started || disposed || !identity.Supported || Path.GetFileName(paths.ProfileRoot)!="app")return;started=true;automaticStartedAt=clock.GetTimestamp();automaticTask=AutomaticLoop();}
    }
    private void WakeAutomatic()
    {try{scheduleChanged.Release();}catch(SemaphoreFullException){}}
    private TimeSpan NextAutomaticDelay()
    {
        lock(sync)
        {
            if(!view.AutoCheck)return Timeout.InfiniteTimeSpan;
            var interval=TimeSpan.FromHours(view.CheckIntervalHours);
            var remaining=TimeSpan.FromSeconds(20+startupJitter)-clock.GetElapsedTime(automaticStartedAt);
            void Extend(TimeSpan wait){if(wait>remaining)remaining=wait;}
            var now=clock.GetUtcNow();
            if(DateTimeOffset.TryParse(view.CheckedAt,out var checkedAt) && checkedAt<=now)Extend(interval-(now-checkedAt));
            if(lastCheckAttempt.HasValue)Extend(interval-clock.GetElapsedTime(lastCheckAttempt.Value));
            else if(attemptedAt!=DateTimeOffset.MinValue)Extend(interval-(now-attemptedAt));
            if(failureStartedAt.HasValue)Extend(failureDelay-clock.GetElapsedTime(failureStartedAt.Value));
            return remaining>TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    private async Task WaitAutomatic(TimeSpan delay)
    {
        if(delay==Timeout.InfiniteTimeSpan){await scheduleChanged.WaitAsync(lifetime.Token).ConfigureAwait(false);return;}
        using var wait=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var timer=Task.Delay(delay>TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay,clock,wait.Token);
        var changed=scheduleChanged.WaitAsync(wait.Token);
        await Task.WhenAny(timer,changed).ConfigureAwait(false);
        // Cancel and observe the loser too: an abandoned waiter can swallow the next preference change.
        wait.Cancel();
        try{await Task.WhenAll(timer,changed).ConfigureAwait(false);}catch(OperationCanceledException) when(!lifetime.IsCancellationRequested){}
    }
    private async Task AutomaticLoop()
    {
        try
        {
            // One startup maintenance pass is independent of auto-check; disabled checks have no recurring timer.
            await Task.Delay(TimeSpan.FromSeconds(20+startupJitter),clock,lifetime.Token).ConfigureAwait(false);
            await Task.Run(()=>
            {
                // A healthy host can appear before its updater exits. Observe that exact worker once, without polling.
                workerCompletion=UpdateMaintenance.ObserveWorkers(paths,()=>
                {Maintain();Change(v=>v with{LastResult=UpdateMaintenance.LastResult(paths)});},lifetime.Token,MaintenanceFailed);
                Maintain();
            },lifetime.Token).ConfigureAwait(false);
            Change(v=>v with{LastResult=UpdateMaintenance.LastResult(paths)});
            while(!lifetime.IsCancellationRequested)
            {
                var delay=NextAutomaticDelay();
                if(delay!=TimeSpan.Zero){await WaitAutomatic(delay).ConfigureAwait(false);continue;}
                await Task.Run(Maintain,lifetime.Token).ConfigureAwait(false);
                Change(v=>v with{LastResult=UpdateMaintenance.LastResult(paths)});
                try{await Check(true).ConfigureAwait(false);}
                catch(InvalidOperationException){await scheduleChanged.WaitAsync(lifetime.Token).ConfigureAwait(false);}
            }
        }
        catch(OperationCanceledException){}catch(ObjectDisposedException){}
        finally{await workerCompletion.ConfigureAwait(false);}
    }
    private void MaintenanceFailed()=>Change(v=>v with{Message="更新缓存维护未完成，已保留现有文件；版本检查仍可继续。"+retryNotice});
    private void Maintain()
    {try{UpdateMaintenance.Cleanup(paths,Downloaded?.Operation,lifetime.Token);}catch(Exception error) when(UpdateStorage.MetadataError(error)){MaintenanceFailed();}}
    public void Dispose()
    {
        lock(sync){if(disposed)return;disposed=true;lifetime.Cancel();try{active?.Cancel();}catch(ObjectDisposedException){}}
        transport.Dispose();
    }
}
