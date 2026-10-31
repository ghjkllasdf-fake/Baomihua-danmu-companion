using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class UpdateFrequencyTests
{
    private static void Check(bool value,string message)=>DistributionTests.Check(value,message);
    private sealed class Clock : TimeProvider
    {
        private readonly object gate=new();
        private readonly List<Timer> timers=new();
        private DateTimeOffset now=new(2026,10,31,10,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow(){lock(gate)return now;}
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>GetUtcNow().UtcTicks;
        public TimeSpan? NextDelay {get{lock(gate)return timers.Where(t=>t.Enabled).Select(t=>(TimeSpan?)(t.Due-now)).Order().FirstOrDefault();}}
        public override ITimer CreateTimer(TimerCallback callback,object state,TimeSpan due,TimeSpan period)
        {var timer=new Timer(this,callback,state);lock(gate)timers.Add(timer);timer.Change(due,period);return timer;}
        public void Advance(TimeSpan elapsed)
        {
            Timer[] ready;lock(gate){now+=elapsed;ready=timers.Where(t=>t.Enabled && t.Due<=now).ToArray();foreach(var timer in ready)timer.Enabled=false;}
            foreach(var timer in ready)timer.Fire();
        }
        private sealed class Timer(Clock clock,TimerCallback callback,object state) : ITimer
        {
            public bool Enabled;
            public DateTimeOffset Due;
            public bool Change(TimeSpan due,TimeSpan period)
            {Check(period==Timeout.InfiniteTimeSpan,"Frequency fixture only expects owned one-shot timers.");lock(clock.gate){Enabled=due!=Timeout.InfiniteTimeSpan;Due=Enabled ? clock.now+due : clock.now;return true;}}
            public void Fire()=>callback(state);
            public void Dispose(){lock(clock.gate)Enabled=false;}
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool Block;
        public HttpStatusCode Status=HttpStatusCode.OK;
        public TimeSpan? RetryAfter;
        public DateTimeOffset? RetryAt;
        public bool RateLimitDownloads;
        public int AssetRequests;
        public long? Reset;
        private int requests;
        public int Requests=>Volatile.Read(ref requests);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            bool metadata=request.RequestUri.Host=="api.github.com";
            Check(metadata || RateLimitDownloads,"Automatic frequency checks must never turn into hidden artifact downloads.");
            if(!metadata)Interlocked.Increment(ref AssetRequests);
            if(Block)await Task.Delay(Timeout.Infinite,token);
            string name=UpdateCatalog.ArtifactName("0.1.1-beta.10",false,"portable"),prefix="https://github.com/"+UpdateCatalog.Repository+"/releases/download/v0.1.1-beta.10/";
            var response=new HttpResponseMessage(metadata ? Status : HttpStatusCode.TooManyRequests){Content=new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new[]{new{tag_name="v0.1.1-beta.10",draft=false,prerelease=true,body="frequency fixture",assets=new[]{
                new{name,size=10,browser_download_url=prefix+name},new{name="SHA256SUMS.txt",size=10,browser_download_url=prefix+"SHA256SUMS.txt"}}}}))};
            if(RetryAfter.HasValue)response.Headers.RetryAfter=new RetryConditionHeaderValue(RetryAfter.Value);
            if(RetryAt.HasValue)response.Headers.RetryAfter=new RetryConditionHeaderValue(RetryAt.Value);
            if(Reset.HasValue){response.Headers.Add("X-RateLimit-Remaining","0");response.Headers.Add("X-RateLimit-Reset",Reset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));}
            return response;
        }
    }
    private static void Save(UpdateService service,bool auto,int hours,bool download=false)
    {
        using var payload=JsonDocument.Parse(JsonSerializer.Serialize(new{autoCheck=auto,autoDownload=download,channel="beta",checkIntervalHours=hours}));service.SetPreferences(payload.RootElement);
    }
    private static async Task Until(Func<bool> condition,string message)
    {
        for(int i=0;i<200;i++)
        {
            if(condition()){Check(true,message);return;}
            await Task.Delay(10);
        }
        Check(false,message);
    }
    private static UpdateService Service(string root,string name,Clock clock,Handler handler,string profile="app",int jitter=0)=>
        new(new ApplicationPaths(Path.Combine(root,name),profile,Path.Combine(root,name+"-data")),new UpdateIdentity("0.1.1-beta.9","standard","portable",true),new UpdateTransport(handler,clock),clock,jitter);
    private static async Task Stop(UpdateService service,Clock clock)
    {service.Dispose();await service.AutomaticCompletion.WaitAsync(TimeSpan.FromSeconds(3));Check(clock.NextDelay==null,"Disposal must cancel all pending automatic timers.");}
    private static UpdateRetry StoredRetry(string root,string name,Clock clock)=>
        UpdateRetryStorage.Load(Path.Combine(root,name+"-data","app","updates","retry.json"),clock.GetUtcNow(),out _);
    private static async Task IdleDays(Clock clock,Handler handler,int days,int requests)
    {
        for(int day=0;day<days;day++)
        {
            clock.Advance(TimeSpan.FromDays(1));await Until(()=>clock.NextDelay!=null,"A long cadence must keep rescheduling bounded timer chunks.");
            Check(handler.Requests==requests,"Daily timer chunks must not turn weekly/monthly cadence into daily HTTP requests.");
        }
    }
    private static async Task Scheduling(string root)
    {
        var clock=new Clock();var handler=new Handler();using var service=Service(root,"cadence",clock,handler);service.StartAutomatic();
        Check(clock.NextDelay==TimeSpan.FromSeconds(20),"Automatic checks must retain the startup gate.");
        clock.Advance(TimeSpan.FromSeconds(20));await Until(()=>handler.Requests==1 && clock.NextDelay==TimeSpan.FromDays(1),"The default cadence must check once and wait a day.");
        Save(service,true,168);await IdleDays(clock,handler,6,1);
        clock.Advance(TimeSpan.FromDays(1));await Until(()=>handler.Requests==2 && clock.NextDelay==TimeSpan.FromDays(1),"Weekly cadence must check only at its actual seven-day boundary.");
        Save(service,true,720);await IdleDays(clock,handler,2,2);
        Save(service,true,24);await Until(()=>handler.Requests==3 && clock.NextDelay==TimeSpan.FromDays(1),"Shortening an overdue monthly interval must cancel the old wait and check immediately.");
        Save(service,true,720);await IdleDays(clock,handler,29,3);
        clock.Advance(TimeSpan.FromDays(1));await Until(()=>handler.Requests==4 && clock.NextDelay==TimeSpan.FromDays(1),"Monthly cadence must reach its actual fixed thirty-day boundary without early HTTP.");
        await service.CheckAsync();await Until(()=>handler.Requests==5 && clock.NextDelay==TimeSpan.FromDays(1),"Manual checks must stay immediate and reset confirmed freshness.");
        await IdleDays(clock,handler,1,5);await Stop(service,clock);
        var monthlyHandler=new Handler();using(var monthly=Service(root,"cadence",clock,monthlyHandler))
        {
            monthly.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));await Until(()=>clock.NextDelay!=null,"Monthly preference and confirmed freshness must survive restart.");
            Check(monthly.View.CheckIntervalHours==720 && monthlyHandler.Requests==0,"Restart must not reset a fresh monthly check to daily.");
            await IdleDays(clock,monthlyHandler,28,0);clock.Advance(clock.NextDelay.Value);
            await Until(()=>monthlyHandler.Requests==1 && clock.NextDelay==TimeSpan.FromDays(1),"Restarted monthly cadence must run exactly at the original thirty-day deadline.");await Stop(monthly,clock);
        }
        var restartClock=new Clock();using(var failing=Service(root,"weekly-attempt",restartClock,new Handler{Status=HttpStatusCode.InternalServerError}))
        {Save(failing,true,168);await failing.CheckAsync();await Stop(failing,restartClock);}
        restartClock.Advance(TimeSpan.FromDays(1));var restartedHandler=new Handler();using var restarted=Service(root,"weekly-attempt",restartClock,restartedHandler);restarted.StartAutomatic();restartClock.Advance(TimeSpan.FromSeconds(20));
        await Until(()=>restartClock.NextDelay!=null,"Restart must restore its last attempted check as well as server backoff.");
        Check(restartedHandler.Requests==0,"Restarting after a short network backoff must not bypass a configured weekly interval.");
        Save(restarted,true,24);await Until(()=>restartedHandler.Requests==1 && restartClock.NextDelay==TimeSpan.FromDays(1),"Persisted attempt freshness must still recompute correctly when weekly becomes overdue daily.");await Stop(restarted,restartClock);
    }
    private static async Task DisabledAndCancellation(string root)
    {
        var clock=new Clock();var handler=new Handler();using var service=Service(root,"disabled",clock,handler,jitter:5);Save(service,false,24);service.StartAutomatic();
        Check(clock.NextDelay==TimeSpan.FromSeconds(25),"Disabled checking must still retain the single startup maintenance gate.");clock.Advance(TimeSpan.FromSeconds(10));Save(service,true,24);
        await Until(()=>clock.NextDelay==TimeSpan.FromSeconds(15),"Enabling checks must not bypass the once-per-start jitter gate.");
        Save(service,false,24);Check(clock.NextDelay==TimeSpan.FromSeconds(15),"Disabling checks must not cancel independent one-shot maintenance.");
        clock.Advance(TimeSpan.FromHours(2));await Until(()=>clock.NextDelay==null,"Disabled checks must have no recurring timer after startup maintenance.");Check(handler.Requests==0,"Disabled checks must never contact GitHub.");Save(service,true,24);
        await Until(()=>handler.Requests==1 && clock.NextDelay==TimeSpan.FromHours(24),"Enabling an overdue check must wake it.");
        Save(service,false,24);await Until(()=>clock.NextDelay==null,"Disabled checks must cancel cadence timers too.");await service.CheckAsync();
        Check(handler.Requests==2 && clock.NextDelay==null,"Manual checks must work while auto-check remains disabled.");await Stop(service,clock);
        var blockedClock=new Clock();var blocked=new Handler{Block=true};using var active=Service(root,"cancel",blockedClock,blocked);Save(active,true,24);active.StartAutomatic();blockedClock.Advance(TimeSpan.FromSeconds(20));
        await Until(()=>blocked.Requests==1 && active.View.State=="checking","Automatic check must enter real cancellable I/O.");
        bool duplicate=false;try{await active.CheckAsync();}catch(InvalidOperationException){duplicate=true;}Check(duplicate && blocked.Requests==1,"Manual and automatic checks must share the same single-flight gate.");
        active.Cancel();await Until(()=>active.View.State=="idle" && blockedClock.NextDelay==TimeSpan.FromHours(24),"Cancellation must not immediately restart a hidden check.");await Stop(active,blockedClock);
        var canceledHandler=new Handler();using(var canceled=Service(root,"cancel",blockedClock,canceledHandler))
        {
            canceled.StartAutomatic();blockedClock.Advance(TimeSpan.FromSeconds(20));await Until(()=>blockedClock.NextDelay!=null,"A canceled attempt must persist its cadence freshness.");
            Check(canceledHandler.Requests==0 && blockedClock.NextDelay>TimeSpan.FromHours(23),"Restart must not immediately repeat the canceled automatic check.");await Stop(canceled,blockedClock);
        }
        var isolatedClock=new Clock();var isolated=new Handler();using var unit=Service(root,"isolated",isolatedClock,isolated,"unit");unit.StartAutomatic();isolatedClock.Advance(TimeSpan.FromDays(2));
        Check(isolated.Requests==0 && isolatedClock.NextDelay==null,"Non-app profiles must never schedule automatic checks.");
    }
    private static async Task StartupMaintenance(string root)
    {
        foreach(bool enabled in new[]{false,true})
        {
            var clock=new Clock();var handler=new Handler();string name=enabled ? "fresh-maintenance" : "off-maintenance";
            var paths=new ApplicationPaths(Path.Combine(root,name),"app",Path.Combine(root,name+"-data"));string cache=Path.Combine(paths.ProfileRoot,"updates");
            UpdateStorage.Save(Path.Combine(cache,"preferences.json"),new(enabled,false,"beta",clock.GetUtcNow().ToString("O"),24));
            using var service=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true),new UpdateTransport(handler,clock),clock,0);
            string worker=UpdateStorage.CreateOperation(cache,"worker");Directory.CreateDirectory(Path.Combine(worker,"helper"));File.WriteAllText(Path.Combine(worker,"helper","DanmuCompanion.Updater.exe"),"owned completed worker fixture");
            UpdateMaintenance.Complete(paths,worker);
            UpdateStorage.Atomic(Path.Combine(cache,"last-result.json"),JsonSerializer.SerializeToUtf8Bytes(new{operationId=Path.GetFileName(worker),completedAt=clock.GetUtcNow().ToString("O"),version="0.1.1-beta.10",status="complete"}));
            Check(service.View.LastResult=="","Fixture outcome must arrive after service construction, like a finishing updater worker.");
            service.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));
            await Until(()=>!Directory.Exists(worker) && service.View.LastResult.Contains("0.1.1-beta.10"),"Startup cleanup and actual worker outcome refresh must not wait for a disabled or daily check.");
            Check(handler.Requests==0,"Startup maintenance must not perform a disabled or fresh version check.");await Stop(service,clock);
        }
    }
    private static async Task DownloadQuotaAndRestart(string root)
    {
        var clock=new Clock();var handler=new Handler{RateLimitDownloads=true,RetryAfter=TimeSpan.FromHours(24)};
        using(var service=Service(root,"asset-quota",clock,handler))
        {
            using var prefs=JsonDocument.Parse("{\"autoCheck\":true,\"autoDownload\":true,\"channel\":\"beta\",\"checkIntervalHours\":24}");service.SetPreferences(prefs.RootElement);
            service.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));
            await Until(()=>handler.Requests==2 && handler.AssetRequests==1 && service.View.State=="failed" && clock.NextDelay==TimeSpan.FromHours(24),"Discovery success must not discard an automatic-download Retry-After.");
            Save(service,true,24,true);await Until(()=>clock.NextDelay==TimeSpan.FromHours(24),"Shrinking frequency must not bypass the asset quota.");
            await service.CheckAsync();await Until(()=>handler.Requests==3 && clock.NextDelay==TimeSpan.FromHours(24),"A successful metadata-only manual check must not clear an outstanding asset deadline.");
            Check(handler.AssetRequests==1 && service.View.AutoDownload,"Manual metadata success must not implicitly bypass the asset deadline even with automatic downloads enabled.");Save(service,true,24);await Stop(service,clock);
        }
        var reloadedHandler=new Handler();using var reloaded=Service(root,"asset-quota",clock,reloadedHandler);reloaded.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));
        await Until(()=>clock.NextDelay!=null,"Restart must resume its persisted quota wait after maintenance.");
        Check(reloadedHandler.Requests==0 && clock.NextDelay>TimeSpan.FromHours(23),"Restarting must not erase the GitHub Retry-After deadline.");
        clock.Advance(clock.NextDelay.Value);await Until(()=>reloadedHandler.Requests==1 && clock.NextDelay==TimeSpan.FromHours(24),"Expired persisted quota must allow normal checks again.");await Stop(reloaded,clock);
    }
    private static void RetryStorage(string root)
    {
        var now=new DateTimeOffset(2026,10,31,10,0,0,TimeSpan.Zero);string file=Path.Combine(root,"retry-fixture.json");
        foreach(string content in new[]{"{}","{\"schemaVersion\":1,\"notBefore\":\"invalid\",\"failures\":1,\"serverLimited\":true}",
            "{\"schemaVersion\":1,\"notBefore\":\"10000-01-01T00:00:00Z\",\"failures\":1,\"serverLimited\":true}"})
        {
            File.WriteAllText(file,content);var retry=UpdateRetryStorage.Load(file,now,out string notice);
            Check(notice.Length>0 && retry.NotBefore==now.AddHours(6) && File.ReadAllText(file)==content,"Corrupt or unbounded retry metadata must defer safely without rewriting its bytes.");
        }
        UpdateRetryStorage.Save(file,new(now.AddHours(48),0,true));var limited=UpdateRetryStorage.Load(file,now,out string message);
        Check(message=="" && limited.ServerLimited && limited.NotBefore==now.AddHours(48),"Manual success may reset the failure counter but must preserve the saved server deadline.");
        Check(UpdateRetryStorage.Load(file,now.AddHours(49),out message).NotBefore==DateTimeOffset.MinValue,"Expired stored retry deadlines must not suspend checks forever.");
        Check(UpdateRetryStorage.Deadline(now,TimeSpan.MaxValue)==DateTimeOffset.MaxValue,"Oversized server waits must saturate without overflow or an early retry.");
        UpdateRetryStorage.Save(file,new(DateTimeOffset.MaxValue,3,true));Check(UpdateRetryStorage.Load(file,now,out message).NotBefore==DateTimeOffset.MaxValue && message=="","Representable server deadlines must survive persistence without arbitrary shortening.");
        var paths=new ApplicationPaths(Path.Combine(root,"failed-save"),"unit",Path.Combine(root,"failed-save-data"));using var service=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true));
        Directory.CreateDirectory(Path.Combine(paths.ProfileRoot,"updates","preferences.json"));var before=service.View;
        bool rejected=false;try{Save(service,true,168);}catch(Exception error) when(error is IOException or UnauthorizedAccessException){rejected=true;}
        Check(rejected,"An atomic preference write failure must be reported.");
        Check(service.View==before,"Failed frequency writes must not alter the confirmed state or schedule.");
    }
    private static async Task RetryRegressions(string root)
    {
        var clock=new Clock();var handler=new Handler{Status=HttpStatusCode.TooManyRequests,RetryAfter=TimeSpan.FromDays(10)};
        using(var service=Service(root,"long-quota",clock,handler))
        {
            Save(service,true,24);service.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));
            await Until(()=>handler.Requests==1 && clock.NextDelay==TimeSpan.FromDays(1),"A ten-day quota must begin with a one-day timer chunk.");await Stop(service,clock);
        }
        var freshHandler=new Handler();using(var reloaded=Service(root,"long-quota",clock,freshHandler))
        {
            reloaded.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));await Until(()=>clock.NextDelay==TimeSpan.FromDays(1),"Long server deadlines must survive restart.");
            for(int day=1;day<=9;day++){clock.Advance(TimeSpan.FromDays(1));await Until(()=>clock.NextDelay!=null,"Every quota chunk must reschedule.");Check(freshHandler.Requests==0,"Server quota must not be silently truncated at seven days.");}
            clock.Advance(TimeSpan.FromDays(1)-TimeSpan.FromSeconds(20));await Until(()=>freshHandler.Requests==1 && clock.NextDelay==TimeSpan.FromHours(24),"Long quota must resume at its actual deadline.");await Stop(reloaded,clock);
        }
        var failureClock=new Clock();using(var first=Service(root,"expired-count",failureClock,new Handler{Status=HttpStatusCode.InternalServerError}))
        {Save(first,true,24);await first.CheckAsync();await Stop(first,failureClock);}
        failureClock.Advance(TimeSpan.FromHours(25));var failureHandler=new Handler{Status=HttpStatusCode.InternalServerError};
        using(var second=Service(root,"expired-count",failureClock,failureHandler))
        {second.StartAutomatic();failureClock.Advance(TimeSpan.FromSeconds(20));await Until(()=>failureHandler.Requests==1 && failureClock.NextDelay==TimeSpan.FromHours(24) && StoredRetry(root,"expired-count",failureClock).Failures==2,"Restart after expiry must retain failures until an actual success.");await Stop(second,failureClock);}
        foreach(bool corrupt in new[]{true,false})
        {
            string name=corrupt ? "corrupt-notice" : "write-notice";var noticeClock=new Clock();var noticeHandler=new Handler{Status=HttpStatusCode.InternalServerError};
            var paths=new ApplicationPaths(Path.Combine(root,name),"app",Path.Combine(root,name+"-data"));string retry=Path.Combine(paths.ProfileRoot,"updates","retry.json");Directory.CreateDirectory(Path.GetDirectoryName(retry));
            if(corrupt)File.WriteAllText(retry,"{}");
            using var noticeService=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true),new UpdateTransport(noticeHandler,noticeClock),noticeClock,0);
            if(!corrupt){Directory.CreateDirectory(retry);await noticeService.CheckAsync();}
            string notice=noticeService.View.Message;Check(notice.Length>0,"Retry failure must have a visible diagnostic.");Save(noticeService,true,24);
            Check(noticeService.View.Message.Length>0,"Saving frequency must preserve the independent retry diagnostic.");
            if(corrupt)
            {
                noticeHandler.Block=true;var manual=noticeService.CheckAsync();await Until(()=>noticeHandler.Requests==1,"A corrupt retry record must still permit manual metadata requests.");noticeService.Cancel();await manual;
                Check(noticeService.View.Message.Contains(notice) && noticeService.View.Message.Contains("已取消"),"Cancel must preserve the independent corruption diagnostic and its actual wait.");noticeHandler.Block=false;
            }
            noticeService.StartAutomatic();noticeClock.Advance(TimeSpan.FromSeconds(20));
            await Until(()=>noticeClock.NextDelay!=null,"Retained diagnostic must agree with the retained wait.");Check(noticeHandler.Requests==1 && noticeClock.NextDelay>TimeSpan.FromHours(5),"Preference save must not bypass diagnosed retry deferral.");
            if(corrupt){noticeHandler.Status=HttpStatusCode.OK;await noticeService.CheckAsync();Check(!noticeService.View.Message.Contains(notice),"A successful retry record repair must clear, not perpetuate, the independent diagnostic.");}
            await Stop(noticeService,noticeClock);
        }
    }
    private static async Task LateWorkerMaintenance(string root)
    {
        foreach(int mode in new[]{0,1,2})
        {
            bool enabled=mode==1;string name="late-worker-"+mode;var clock=new Clock();var handler=new Handler();var paths=new ApplicationPaths(Path.Combine(root,name),"app",Path.Combine(root,name+"-data"));string cache=Path.Combine(paths.ProfileRoot,"updates");
            UpdateStorage.Save(Path.Combine(cache,"preferences.json"),new(enabled,false,"beta",clock.GetUtcNow().ToString("O"),24));
            for(int i=0;i<100;i++)Directory.CreateDirectory(Path.Combine(cache,"000-foreign-"+i));
            using var service=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true),new UpdateTransport(handler,clock),clock,0);
            string worker=UpdateStorage.CreateOperation(cache,"worker"),helper=Path.Combine(worker,"helper","DanmuCompanion.Updater.exe");Directory.CreateDirectory(Path.GetDirectoryName(helper));
            foreach(string file in Directory.EnumerateFiles(AppContext.BaseDirectory)){if(Path.GetExtension(file) is ".dll" or ".json")File.Copy(file,Path.Combine(Path.GetDirectoryName(helper),Path.GetFileName(file)));}
            File.Copy(Environment.ProcessPath,helper);var start=new ProcessStartInfo(helper){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};start.ArgumentList.Add("--image-fixture");
            using var child=Process.Start(start)??throw new InvalidOperationException("Late worker fixture did not start.");
            try
            {
                Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))=="ready","Late worker must reach its real exit gate.");
                var job=new UpdateJob(paths.Root,paths.DataRoot,"app",null,false,Path.GetFileName(worker));
                UpdateStorage.Atomic(Path.Combine(worker,"worker.json"),JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=2,kind="direct-overwrite",job,applyDirectory=Path.Combine(root,"absent-transaction"),workerPid=child.Id,workerStartTicks=child.StartTime.ToUniversalTime().Ticks},UpdateStorage.Json));
                using(var record=UpdateStorage.ReadJson(Path.Combine(worker,"worker.json"),16384))
                {
                    Check(UpdateMaintenance.Running(record.RootElement,helper,(_,_)=>throw new System.ComponentModel.Win32Exception(5)),"Inaccessible worker query must block cleanup and concurrent recovery, never imply a dead worker.");
                    Check(!UpdateMaintenance.Running(record.RootElement,helper,(_,_)=>null),"Only a confirmed absent worker may permit cleanup.");
                }
                using(var cancel=new CancellationTokenSource())
                {
                    int failures=0,completed=0;var observed=UpdateMaintenance.ObserveWorkers(paths,()=>completed++,cancel.Token,()=>failures++,FailedScan());
                    Check(failures==1 && !observed.IsCompleted,"An enumeration IO failure must be diagnosed without abandoning already opened worker observers.");cancel.Cancel();await observed.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(completed==0 && !child.HasExited,"Observer cancellation must release its waiter without deleting or stopping the live helper.");
                }
                IEnumerable<string> FailedScan(){yield return worker;throw new DirectoryNotFoundException("controlled scan race");}
                using(var cancel=new CancellationTokenSource())
                {
                    int steps=0;var observed=UpdateMaintenance.ObserveWorkers(paths,()=>throw new Exception("Canceled worker must not refresh."),cancel.Token,null,CanceledScan());
                    await observed.WaitAsync(TimeSpan.FromSeconds(3));Check(steps==2 && !child.HasExited,"Canceled scanning must stop between entries and still await the registered live-worker waiter.");
                    IEnumerable<string> CanceledScan(){steps++;yield return worker;cancel.Cancel();steps++;yield return cache;steps++;throw new Exception("Scan continued after cancellation.");}
                }
                service.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));await Until(()=>service.View.Revision>0,"Startup maintenance must run while the real worker is still alive.");
                Check(Directory.Exists(worker) && service.View.LastResult=="" && handler.Requests==0,"Maintenance must not delete or invent a running worker result.");
                if(mode==2){await Stop(service,clock);Check(!child.HasExited && Directory.Exists(worker),"Disposal must finish worker observation without stopping or deleting a live helper.");continue;}
                UpdateStorage.Atomic(Path.Combine(cache,"last-result.json"),JsonSerializer.SerializeToUtf8Bytes(new{operationId=Path.GetFileName(worker),completedAt=clock.GetUtcNow().ToString("O"),version="0.1.1-beta.10",status="complete"}));UpdateMaintenance.Complete(paths,worker);
                child.StandardInput.WriteLine("exit");child.StandardInput.Close();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Until(()=>!Directory.Exists(worker) && service.View.LastResult.Contains("0.1.1-beta.10"),"A worker finishing after startup maintenance must trigger one cleanup and result refresh without polling.");
                Check(handler.Requests==0 && clock.NextDelay==(enabled ? TimeSpan.FromHours(24)-TimeSpan.FromSeconds(20) : null),"Worker exit maintenance must not create disabled-check timers or contact GitHub.");await Stop(service,clock);
            }
            finally{if(!child.HasExited){child.Kill();child.WaitForExit(5000);}}
        }
    }
    private static async Task Backoff(string root)
    {
        var clock=new Clock();var handler=new Handler{Status=HttpStatusCode.InternalServerError};using var service=Service(root,"failure",clock,handler);Save(service,true,24);service.StartAutomatic();clock.Advance(TimeSpan.FromSeconds(20));
        await Until(()=>handler.Requests==1 && clock.NextDelay==TimeSpan.FromDays(1),"Network failure must not poll earlier than the daily cadence.");
        Check(StoredRetry(root,"failure",clock).Failures==1 && StoredRetry(root,"failure",clock).NotBefore==clock.GetUtcNow().AddHours(6),"First failure must retain the independent six-hour network backoff.");
        Save(service,true,168);await IdleDays(clock,handler,1,1);Save(service,true,24);
        await Until(()=>handler.Requests==2 && clock.NextDelay==TimeSpan.FromDays(1),"Changing a failed weekly check to overdue daily must recompute, not retain an artificial week-long backoff.");
        Check(StoredRetry(root,"failure",clock).Failures==2 && StoredRetry(root,"failure",clock).NotBefore==clock.GetUtcNow().AddHours(12),"Repeated failure must keep twelve-hour network backoff independent of cadence.");
        clock.Advance(TimeSpan.FromHours(12));await service.CheckAsync();
        Check(handler.Requests==3 && StoredRetry(root,"failure",clock).Failures==3 && StoredRetry(root,"failure",clock).NotBefore==clock.GetUtcNow().AddHours(24),"Manual retries remain explicit and exponential failure backoff remains bounded at a day.");
        handler.Status=HttpStatusCode.OK;await service.CheckAsync();await Until(()=>handler.Requests==4 && clock.NextDelay==TimeSpan.FromDays(1),"A successful manual check must clear network failures and restore daily cadence.");
        Check(StoredRetry(root,"failure",clock).Failures==0,"Actual success must durably reset the failure counter.");await Stop(service,clock);
        foreach(bool absolute in new[]{false,true})
        {
            var rateClock=new Clock();var limited=new Handler{Status=HttpStatusCode.TooManyRequests};int days=absolute ? 3 : 2;
            if(absolute)limited.RetryAt=rateClock.GetUtcNow().AddDays(days).AddSeconds(20);else limited.RetryAfter=TimeSpan.FromDays(days);
            using var rate=Service(root,absolute ? "rate-date" : "rate-delta",rateClock,limited);Save(rate,true,24);rate.StartAutomatic();rateClock.Advance(TimeSpan.FromSeconds(20));
            await Until(()=>limited.Requests==1 && rateClock.NextDelay==TimeSpan.FromDays(1),"Both Retry-After seconds and dates must extend cadence through bounded timer chunks.");
            Save(rate,true,720);Save(rate,true,24);await IdleDays(rateClock,limited,days-1,1);
            limited.Status=HttpStatusCode.OK;rateClock.Advance(TimeSpan.FromDays(1));await Until(()=>limited.Requests==2 && rateClock.NextDelay==TimeSpan.FromDays(1),"A monthly-to-daily change must not shorten a still-active GitHub server deadline.");await Stop(rate,rateClock);
        }
        var resetClock=new Clock();var resetHandler=new Handler{Status=HttpStatusCode.Forbidden,Reset=resetClock.GetUtcNow().AddHours(48).AddSeconds(20).ToUnixTimeSeconds()};
        using var reset=Service(root,"rate-reset",resetClock,resetHandler);Save(reset,true,24);reset.StartAutomatic();resetClock.Advance(TimeSpan.FromSeconds(20));
        await Until(()=>resetHandler.Requests==1 && resetClock.NextDelay==TimeSpan.FromDays(1),"Long reset deadlines must use bounded timer chunks.");await IdleDays(resetClock,resetHandler,1,1);
        resetHandler.Status=HttpStatusCode.OK;resetClock.Advance(TimeSpan.FromDays(1));await Until(()=>resetHandler.Requests==2 && resetClock.NextDelay==TimeSpan.FromDays(1),"Primary rate-limit reset must restore normal cadence.");await Stop(reset,resetClock);
    }
    private static void Preferences(string root)
    {
        string file=Path.Combine(root,"preferences.json");
        File.WriteAllText(file,"{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkedAt\":\"\"}");
        var legacy=UpdateStorage.Load(file,"stable",out string notice);
        Check(notice=="" && legacy.AutoCheck && legacy.Channel=="beta" && legacy.CheckIntervalHours==24,"Legacy missing frequency must migrate to daily without losing values.");
        foreach(int oldHours in new[]{1,6,12})
        {
            string stamp="2026-10-31T10:00:00+00:00";
            File.WriteAllText(file,JsonSerializer.Serialize(new{autoCheck=false,autoDownload=true,channel="stable",checkedAt=stamp,checkIntervalHours=oldHours}));
            var migrated=UpdateStorage.Load(file,"beta",out notice);
            Check(notice=="" && migrated.CheckIntervalHours==24 && !migrated.AutoCheck && migrated.AutoDownload && migrated.Channel=="stable" && migrated.CheckedAt==stamp,"Retired persisted hourly intervals must migrate to daily without losing other settings.");
            UpdateStorage.Save(file,migrated);Check(UpdateStorage.Load(file,"beta",out notice).CheckIntervalHours==24,"A migrated frequency must round-trip as a supported daily value.");
        }
        foreach(int hours in new[]{24,168,720})
        {
            File.WriteAllText(file,"{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkedAt\":\"\",\"checkIntervalHours\":"+hours+"}");
            var loaded=UpdateStorage.Load(file,"stable",out notice);
            Check(notice=="" && loaded.CheckIntervalHours==hours,"Every allowed interval must survive persisted loading.");
            UpdateStorage.Save(file,loaded);
            using var saved=JsonDocument.Parse(File.ReadAllBytes(file));
            Check(saved.RootElement.GetProperty("checkIntervalHours").GetInt32()==hours,"Saving must explicitly persist the interval.");
        }
        foreach(string value in new[]{"0","2","48","-1","1.5","\"6\"","null","true"})
        {
            string bad="{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkedAt\":\"\",\"checkIntervalHours\":"+value+"}";
            File.WriteAllText(file,bad);var loaded=UpdateStorage.Load(file,"beta",out notice);
            Check(!loaded.AutoCheck && notice.Length>0 && File.ReadAllText(file)==bad,"Invalid interval must fail closed without rewriting saved bytes.");
        }
        var paths=new ApplicationPaths(Path.Combine(root,"app"),"unit",Path.Combine(root,"data"));
        using var service=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true));
        using(var update=JsonDocument.Parse("{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkIntervalHours\":720}"))service.SetPreferences(update.RootElement);
        using(var old=JsonDocument.Parse("{\"autoCheck\":false,\"autoDownload\":false,\"channel\":\"stable\"}"))service.SetPreferences(old.RootElement);
        using var snapshot=JsonDocument.Parse(JsonSerializer.Serialize(service.View,UpdateStorage.Json));
        Check(snapshot.RootElement.GetProperty("checkIntervalHours").GetInt32()==720,"A legacy three-field bridge save must preserve the currently selected monthly interval.");
        var before=service.View;
        using(var bad=JsonDocument.Parse("{\"autoCheck\":true,\"autoDownload\":false,\"channel\":\"beta\",\"checkIntervalHours\":2}"))
            UpdatePackageTests.Reject(()=>service.SetPreferences(bad.RootElement),"Unsupported bridge interval must be rejected.");
        Check(service.View==before,"Rejected preferences must not change the published schedule.");
        foreach(int retired in new[]{1,6,12})
        {
            using var bad=JsonDocument.Parse(JsonSerializer.Serialize(new{autoCheck=true,autoDownload=false,channel="beta",checkIntervalHours=retired}));
            UpdatePackageTests.Reject(()=>service.SetPreferences(bad.RootElement),"Retired hourly values may migrate from disk but are not valid new frequency writes.");
            Check(service.View==before,"A retired frequency write must preserve the confirmed monthly state.");
        }
    }
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"DanmuUpdateFrequency-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{Preferences(root);RetryStorage(root);await RetryRegressions(root);await Scheduling(root);await DisabledAndCancellation(root);await StartupMaintenance(root);await LateWorkerMaintenance(root);await Backoff(root);await DownloadQuotaAndRestart(root);}
        finally
        {
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath())+"DanmuUpdateFrequency-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe frequency fixture cleanup.");
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: frequency migration, legacy bridge, actual timed rescheduling, disabled checks, cancellation, single-flight and GitHub failure/rate backoff.");
    }
}
