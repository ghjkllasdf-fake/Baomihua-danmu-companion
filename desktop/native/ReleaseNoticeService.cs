using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed class ReleaseNoticeService : IDisposable
{
    private readonly ReleaseNoticeIdentity identity;
    private readonly ReleaseNoticeTransport transport;
    private readonly TimeProvider clock;
    private readonly ApplicationPaths paths;
    private readonly string preferencesFile,retryFile;
    private readonly int startupJitter;
    private readonly object sync=new();
    private readonly SemaphoreSlim operation=new(1,1),scheduleChanged=new(0,1);
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource active;
    private ReleaseNoticePreferences preferences;
    private ReleaseNoticeSnapshot view;
    private ReleaseNotice selected;
    private Task automaticTask=Task.CompletedTask;
    private bool started,disposed,corruptionPending;
    private long automaticStartedAt;
    private long? lastAttempt,failureStartedAt;
    private DateTimeOffset attemptedAt=DateTimeOffset.MinValue;
    private TimeSpan failureDelay;
    private int failures;
    private string retryNotice="";
    private const string Unavailable="当前版本信息不完整，无法自动检查。请打开 Release 页面自行下载完整发行包。";
    internal event Action Changed;
    internal ReleaseNoticeSnapshot View {get{lock(sync)return view;}}
    internal Task AutomaticCompletion=>automaticTask;
    internal Uri ReleasePage {get{lock(sync)return new Uri(selected?.Page??ReleaseNoticeCatalog.Page());}}
    internal ReleaseNoticeService(ApplicationPaths paths,ReleaseNoticeIdentity identity=null,ReleaseNoticeTransport transport=null,TimeProvider clock=null,int? startupJitter=null)
    {
        this.paths=paths;this.clock=clock??TimeProvider.System;this.identity=identity??ReleaseNoticeIdentity.Load(paths);
        this.transport=transport??new ReleaseNoticeTransport(clock:this.clock);this.startupJitter=startupJitter??Random.Shared.Next(10);
        if(this.startupJitter is <0 or >9)throw new ArgumentOutOfRangeException(nameof(startupJitter));
        preferencesFile=Path.Combine(paths.ProfileRoot,"updates","preferences.json");retryFile=Path.Combine(paths.ProfileRoot,"updates","retry.json");
        preferences=ReleaseNoticeCatalog.LoadPreferences(preferencesFile,ReleaseVersion.TryParse(this.identity.Version,out var version) && version.Stable ? "stable" : "beta",out string notice);
        var retry=ReleaseNoticeRetryStorage.Load(retryFile,this.clock.GetUtcNow(),out retryNotice);
        failures=retry.Failures;corruptionPending=retry.UnconfirmedCorruption;
        attemptedAt=retry.AttemptedAt>this.clock.GetUtcNow() ? this.clock.GetUtcNow() : retry.AttemptedAt;
        if(retry.NotBefore>this.clock.GetUtcNow()){failureStartedAt=this.clock.GetTimestamp();failureDelay=retry.NotBefore-this.clock.GetUtcNow();}
        view=new(this.identity.Supported ? "idle" : "unavailable",this.identity.Version,"",this.identity.Variant,this.identity.Distribution,preferences.Channel,
            preferences.AutoCheck,preferences.CheckedAt,"",this.identity.Supported ? notice+retryNotice : Unavailable,0,this.identity.Supported,preferences.CheckIntervalHours,ReleaseNoticeCatalog.Page());
    }
    private void Change(Func<ReleaseNoticeSnapshot,ReleaseNoticeSnapshot> change)
    {lock(sync){if(disposed)return;view=change(view) with{Revision=view.Revision+1};}Changed?.Invoke();}
    private void State(string state,string message="")=>Change(v=>v with{State=state,Message=message[..Math.Min(message.Length,500-retryNotice.Length)]+retryNotice});
    internal ReleaseNoticeSnapshot SetPreferences(JsonElement payload)
    {
        ReleaseNoticeStorage.Unique(payload);
        ReleaseNoticeStorage.Check(payload.ValueKind==JsonValueKind.Object && payload.EnumerateObject().Count()==3,"更新提醒设置参数无效。");
        string channel=payload.GetProperty("channel").GetString();ReleaseNoticeStorage.Check(channel is "beta" or "stable","更新通道无效。");
        int hours=payload.GetProperty("checkIntervalHours").GetInt32();ReleaseNoticeStorage.Check(ReleaseNoticeStorage.ValidInterval(hours),"检查频率仅支持每天、每周或每月。");
        if(!operation.Wait(0))throw new InvalidOperationException("正在检查版本，请稍候或取消。");
        try
        {
            var next=new ReleaseNoticePreferences(payload.GetProperty("autoCheck").GetBoolean(),channel,preferences.CheckedAt,hours);
            ReleaseNoticeCatalog.SavePreferences(preferencesFile,next);bool changed=channel!=preferences.Channel;preferences=next;
            if(changed)lock(sync)selected=null;
            Change(v=>v with{AutoCheck=next.AutoCheck,Channel=channel,CheckIntervalHours=hours,Message=identity.Supported ? retryNotice : Unavailable,
                State=changed ? identity.Supported ? "idle" : "unavailable" : v.State,AvailableVersion=changed ? "" : v.AvailableVersion,
                Notes=changed ? "" : v.Notes,ReleasePage=changed ? ReleaseNoticeCatalog.Page() : v.ReleasePage});return View;
        }
        finally{operation.Release();Wake();}
    }
    internal Task<ReleaseNoticeSnapshot> CheckAsync()=>Check(false);
    private async Task<ReleaseNoticeSnapshot> Check(bool automatic)
    {
        if(!identity.Supported)throw new InvalidOperationException(Unavailable);
        if(!await operation.WaitAsync(0).ConfigureAwait(false))throw new InvalidOperationException("正在检查版本，请稍候。");
        if(automatic && NextDelay()!=TimeSpan.Zero){operation.Release();return View;}
        using var token=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);lock(sync)active=token;
        State("checking");
        try
        {
            lock(sync){lastAttempt=clock.GetTimestamp();attemptedAt=clock.GetUtcNow();}PersistRetry();
            var found=await transport.DiscoverAsync(identity.Version,preferences.Channel,token.Token).ConfigureAwait(false);
            lock(sync)selected=found;
            string stamp=clock.GetUtcNow().ToString("O");preferences=preferences with{CheckedAt=stamp};
            string saveNotice="";
            try{ReleaseNoticeCatalog.SavePreferences(preferencesFile,preferences);}
            catch(Exception error) when(ReleaseNoticeStorage.MetadataError(error)){saveNotice="上次检查时间未能保存，重启后可能重新检查。";}
            failures=0;corruptionPending=false;failureStartedAt=null;failureDelay=TimeSpan.Zero;PersistRetry();
            Change(v=>v with{AvailableVersion=found?.Version??"",Notes=found?.Notes??"",CheckedAt=stamp,ReleasePage=found?.Page??ReleaseNoticeCatalog.Page()});
            State(found==null ? "current" : "available",saveNotice);
        }
        catch(OperationCanceledException)
        {
            if(!token.IsCancellationRequested)Failure(null);
            State(token.IsCancellationRequested ? selected==null ? "idle" : "available" : "failed",token.IsCancellationRequested ? "版本检查已取消。" : "版本检查超时，请重试。");
        }
        catch(Exception error) when(ReleaseNoticeStorage.MetadataError(error) || error is System.Net.Http.HttpRequestException)
        {Failure(error);State("failed",error is InvalidDataException or InvalidOperationException ? error.Message[..Math.Min(error.Message.Length,400)] : "版本检查失败，请确认网络后重试。");}
        finally{lock(sync)active=null;operation.Release();Wake();}
        return View;
    }
    private TimeSpan FailureRemaining()=>failureStartedAt.HasValue ? failureDelay-clock.GetElapsedTime(failureStartedAt.Value) : TimeSpan.Zero;
    private void Failure(Exception error)
    {
        var remaining=FailureRemaining();failures=Math.Min(failures+1,3);failureDelay=TimeSpan.FromHours(6*(1<<(failures-1)));
        if(error is ReleaseNoticeRateLimitException limited && limited.RetryAfter>failureDelay)failureDelay=limited.RetryAfter;
        if(remaining>failureDelay)failureDelay=remaining;
        var maximum=DateTimeOffset.MaxValue-clock.GetUtcNow();if(failureDelay>maximum)failureDelay=maximum;
        failureStartedAt=clock.GetTimestamp();PersistRetry();
    }
    private void PersistRetry()
    {
        var remaining=FailureRemaining();
        try
        {
            ReleaseNoticeRetryStorage.Save(retryFile,new(remaining>TimeSpan.Zero ? ReleaseNoticeRetryStorage.Deadline(clock.GetUtcNow(),remaining) : DateTimeOffset.MinValue,
                failures,remaining>TimeSpan.Zero,attemptedAt,corruptionPending));
            if(!corruptionPending)retryNotice="";
        }
        catch(Exception error) when(ReleaseNoticeStorage.MetadataError(error)){retryNotice="检查退避未能保存，重启后可能重新检查。";}
    }
    internal void Cancel(){lock(sync){try{active?.Cancel();}catch(ObjectDisposedException){}}}
    internal void StartAutomatic()
    {
        lock(sync)
        {
            if(started || disposed)return;started=true;
            if(!identity.Supported || Path.GetFileName(paths.ProfileRoot)!="app")return;
            automaticStartedAt=clock.GetTimestamp();automaticTask=AutomaticLoop();
        }
    }
    private void Wake(){try{scheduleChanged.Release();}catch(SemaphoreFullException){}}
    private TimeSpan NextDelay()
    {
        lock(sync)
        {
            if(!view.AutoCheck)return Timeout.InfiniteTimeSpan;
            var interval=TimeSpan.FromHours(view.CheckIntervalHours);
            var remaining=TimeSpan.FromSeconds(20+startupJitter)-clock.GetElapsedTime(automaticStartedAt);
            void Extend(TimeSpan wait){if(wait>remaining)remaining=wait;}
            var now=clock.GetUtcNow();
            if(DateTimeOffset.TryParse(view.CheckedAt,out var checkedAt) && checkedAt<=now)Extend(interval-(now-checkedAt));
            if(lastAttempt.HasValue)Extend(interval-clock.GetElapsedTime(lastAttempt.Value));
            else if(attemptedAt!=DateTimeOffset.MinValue)Extend(interval-(now-attemptedAt));
            if(failureStartedAt.HasValue)Extend(FailureRemaining());
            return remaining>TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    private async Task Wait(TimeSpan delay)
    {
        if(delay==Timeout.InfiniteTimeSpan){await scheduleChanged.WaitAsync(lifetime.Token).ConfigureAwait(false);return;}
        using var wait=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var timer=Task.Delay(delay>TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay,clock,wait.Token);
        var changed=scheduleChanged.WaitAsync(wait.Token);await Task.WhenAny(timer,changed).ConfigureAwait(false);wait.Cancel();
        try{await Task.WhenAll(timer,changed).ConfigureAwait(false);}catch(OperationCanceledException) when(!lifetime.IsCancellationRequested){}
    }
    private async Task AutomaticLoop()
    {
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                var delay=NextDelay();if(delay!=TimeSpan.Zero){await Wait(delay).ConfigureAwait(false);continue;}
                try{await Check(true).ConfigureAwait(false);}
                catch(InvalidOperationException){await scheduleChanged.WaitAsync(lifetime.Token).ConfigureAwait(false);}
            }
        }
        catch(OperationCanceledException){}catch(ObjectDisposedException){}
    }
    public void Dispose()
    {lock(sync){if(disposed)return;disposed=true;lifetime.Cancel();try{active?.Cancel();}catch(ObjectDisposedException){}}transport.Dispose();}
}
