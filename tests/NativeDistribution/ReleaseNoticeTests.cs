using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class ReleaseNoticeTests
{
    internal static string ContractFixture()
    {
        var snapshots=new List<ReleaseNoticeSnapshot>();
        foreach(string variant in new[]{"standard","includeAPI"})
        foreach(string distribution in new[]{"portable","installer"})
        foreach(int hours in new[]{24,168,720})
        foreach(string state in new[]{"unavailable","idle","checking","current","available","failed"})
        {
            bool supported=state!="unavailable",available=state is "available" or "failed";
            snapshots.Add(new(state,supported ? "0.1.1-beta.9" : "",available ? "0.1.1-beta.10" : "",variant,distribution,"beta",true,
                state=="idle" ? "" : "2026-11-01T01:00:00.0000000+00:00",available ? "New release" : "","",snapshots.Count,supported,hours,
                ReleaseNoticeCatalog.Page(available ? "v0.1.1-beta.10" : null)));
        }
        return JsonSerializer.Serialize(snapshots,ReleaseNoticeStorage.Json);
    }
    private static void Check(bool value,string message)=>DistributionTests.Check(value,message);
    private static void Reject(Action action,string message)
    {
        try{action();}catch(InvalidDataException){Check(true,message);return;}
        Check(false,message);
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Requests;
        internal bool Block;
        internal HttpStatusCode Status=HttpStatusCode.OK;
        internal TimeSpan? RetryAfter;
        internal string Body="[{\"tag_name\":\"v0.1.1-beta.10\",\"draft\":false,\"prerelease\":true,\"body\":\"Release notes\",\"html_url\":\"https://untrusted.example/\"}]";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Check(request.Method==HttpMethod.Get && request.RequestUri.Host=="api.github.com" && request.RequestUri.AbsolutePath=="/repos/"+ReleaseNoticeCatalog.Repository+"/releases",
                "Notice checks may request only project GitHub release metadata, never assets or arbitrary links.");
            Check(request.Headers.Authorization==null,"Notice checks must not embed a GitHub token.");
            Interlocked.Increment(ref Requests);if(Block)await Task.Delay(Timeout.Infinite,token);
            var response=new HttpResponseMessage(Status){Content=new StringContent(Body)};
            if(RetryAfter.HasValue)response.Headers.RetryAfter=new RetryConditionHeaderValue(RetryAfter.Value);return response;
        }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object sync=new();private readonly List<Timer> timers=new();
        private DateTimeOffset now=new(2026,11,1,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow(){lock(sync)return now;}
        public override long GetTimestamp()=>GetUtcNow().UtcTicks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        internal TimeSpan? Next {get{lock(sync)return timers.Where(t=>t.Enabled).Select(t=>(TimeSpan?)(t.Due-now)).Order().FirstOrDefault();}}
        public override ITimer CreateTimer(TimerCallback callback,object state,TimeSpan due,TimeSpan period)
        {var timer=new Timer(this,callback,state);lock(sync)timers.Add(timer);timer.Change(due,period);return timer;}
        internal void Advance(TimeSpan amount)
        {Timer[] ready;lock(sync){now+=amount;ready=timers.Where(t=>t.Enabled && t.Due<=now).ToArray();foreach(var timer in ready)timer.Enabled=false;}foreach(var timer in ready)timer.Fire();}
        private sealed class Timer(Clock clock,TimerCallback callback,object state) : ITimer
        {
            internal bool Enabled;internal DateTimeOffset Due;
            public bool Change(TimeSpan due,TimeSpan period)
            {Check(period==Timeout.InfiniteTimeSpan,"Notice cadence uses only owned one-shot timers.");lock(clock.sync){Enabled=due!=Timeout.InfiniteTimeSpan;Due=Enabled ? clock.now+due : clock.now;return true;}}
            internal void Fire()=>callback(state);
            public void Dispose(){lock(clock.sync)Enabled=false;}
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    private static ReleaseNoticeService Service(string root,string name,Handler handler,Clock clock,string profile="app")=>
        new(new ApplicationPaths(Path.Combine(root,name),profile,Path.Combine(root,name+"-data")),new("0.1.1-beta.9","standard","portable",true),new(handler,clock),clock,0);
    private static void Save(ReleaseNoticeService service,bool auto,int hours)
    {using var payload=JsonDocument.Parse(JsonSerializer.Serialize(new{autoCheck=auto,channel="beta",checkIntervalHours=hours}));service.SetPreferences(payload.RootElement);}
    private static async Task Until(Func<bool> condition,string message)
    {for(int i=0;i<200;i++){if(condition()){Check(true,message);return;}await Task.Delay(5);}Check(false,message);}
    private static async Task Stop(ReleaseNoticeService service,Clock clock)
    {service.Dispose();await service.AutomaticCompletion.WaitAsync(TimeSpan.FromSeconds(3));Check(clock.Next==null,"Shutdown must cancel every scheduled notice timer.");}
    internal static async Task Run()
    {
        Check(ReleaseVersion.Newer("v0.1.1-beta.10","0.1.1-beta.9"),"Numeric beta versions must not sort lexically.");
        Check(ReleaseVersion.Newer("0.1.1","0.1.1-beta.99"),"Stable releases must follow prereleases.");
        Check(!ReleaseVersion.Newer("0.1.1+build.2","0.1.1+build.1"),"Build metadata must not be an update.");
        foreach(string bad in new[]{"dev","1.2","1.02.3","1.2.3-beta.01","1.2.3-","1.2.3+x?","1.2.3\r\n"})
            Check(!ReleaseVersion.TryParse(bad,out _),"Malformed version must be rejected.");
        string root=Path.Combine(Path.GetTempPath(),"DanmuReleaseNotice-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var clock=new Clock();var handler=new Handler();using var service=Service(root,"manual",handler,clock,"unit");
            Check(handler.Requests==0,"Construction must not contact GitHub.");
            var notice=await service.CheckAsync();
            Check(notice.State=="available" && notice.AvailableVersion=="0.1.1-beta.10" && handler.Requests==1,"A newer Release with no assets or updater manifests must produce a notice only.");
            Check(notice.ReleasePage==ReleaseNoticeCatalog.Page("v0.1.1-beta.10") && service.ReleasePage.AbsoluteUri==notice.ReleasePage,"The page must be built from the fixed project and selected tag, not html_url.");
            using(var json=JsonDocument.Parse(JsonSerializer.Serialize(notice,ReleaseNoticeStorage.Json)))
                Check(json.RootElement.EnumerateObject().Count()==14 && !json.RootElement.TryGetProperty("autoDownload",out _) && !json.RootElement.TryGetProperty("canInstall",out _),"The notice contract must not expose download or installation capabilities.");
            Save(service,false,720);Check(!service.View.AutoCheck && service.View.CheckIntervalHours==720,"Notice preferences must support fixed 30-day checks.");
            using(var bad=JsonDocument.Parse("{\"autoCheck\":true,\"autoDownload\":true,\"channel\":\"beta\",\"checkIntervalHours\":24}"))
                Reject(()=>service.SetPreferences(bad.RootElement),"An old payload cannot re-enable asset downloads.");
            using(var bad=JsonDocument.Parse("{\"autoCheck\":true,\"channel\":\"beta\",\"checkIntervalHours\":6}"))
                Reject(()=>service.SetPreferences(bad.RootElement),"New preferences must reject retired short intervals.");
            handler.Block=true;var pending=service.CheckAsync();
            await Until(()=>handler.Requests==2,"Manual checking must enter cancellable HTTP.");
            bool rejected=false;try{await service.CheckAsync();}catch(InvalidOperationException){rejected=true;}
            Check(rejected && handler.Requests==2,"Concurrent checks must share one single-flight gate.");service.Cancel();await pending;
            Check(service.View.State=="available" && service.View.Message.Contains("已取消"),"Cancellation must retain an already confirmed update notice.");
            string file=Path.Combine(root,"legacy-data","unit","updates","preferences.json");Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file,"{\"autoCheck\":true,\"autoDownload\":true,\"channel\":\"beta\",\"checkedAt\":\"\",\"checkIntervalHours\":6}");
            string foreign=Path.Combine(Path.GetDirectoryName(file),"old-downloaded-package.exe");File.WriteAllText(foreign,"foreign or historical bytes");
            var legacyHandler=new Handler();using(var legacy=Service(root,"legacy",legacyHandler,new Clock(),"unit"))
            {
                Check(legacy.View.CheckIntervalHours==24 && legacy.View.AutoCheck,"Legacy daily migration keeps auto-check but ignores auto-download.");await legacy.CheckAsync();
                Check(legacyHandler.Requests==1 && File.ReadAllText(foreign)=="foreign or historical bytes","A saved auto-download setting cannot trigger a download or clean up historical/foreign files.");
                using var saved=ReleaseNoticeStorage.ReadJson(file,4096);Check(!saved.RootElement.TryGetProperty("autoDownload",out _),"Migrated settings must drop auto-download when next saved.");
            }
            await Scheduling(root);await Backoff(root);
        }
        finally
        {
            string expected=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar+"DanmuReleaseNotice-";
            if(!Path.GetFullPath(root).StartsWith(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe notice test cleanup root.");
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: release notices, metadata-only HTTP, retired settings, cadence, cancellation and restart/backoff.");
    }
    private static async Task Scheduling(string root)
    {
        var clock=new Clock();var handler=new Handler();using var service=Service(root,"cadence",handler,clock);service.StartAutomatic();
        Check(clock.Next==TimeSpan.FromSeconds(20),"Notice checks must retain the startup delay.");clock.Advance(TimeSpan.FromSeconds(20));
        await Until(()=>handler.Requests==1 && clock.Next==TimeSpan.FromDays(1),"Default checks must run once then wait a day.");
        Save(service,true,168);
        for(int day=1;day<=7;day++)
        {clock.Advance(TimeSpan.FromDays(1));await Until(()=>clock.Next!=null,"Weekly timers must rearm.");Check(handler.Requests==(day<7 ? 1 : 2),"Weekly chunks must not cause daily HTTP requests.");}
        Save(service,true,720);
        for(int day=1;day<=30;day++)
        {clock.Advance(TimeSpan.FromDays(1));await Until(()=>clock.Next!=null,"Monthly timers must rearm.");Check(handler.Requests==(day<30 ? 2 : 3),"Monthly HTTP must run only at the 30-day boundary.");}
        Save(service,false,24);await Until(()=>clock.Next==null,"Disabled checking must have no maintenance or recurring timer.");
        await service.CheckAsync();Check(handler.Requests==4,"Manual checks must work with automatic checks disabled.");await Stop(service,clock);
    }
    private static async Task Backoff(string root)
    {
        var clock=new Clock();var firstHandler=new Handler{Status=HttpStatusCode.TooManyRequests,RetryAfter=TimeSpan.FromDays(10)};
        using(var first=Service(root,"quota",firstHandler,clock)){Save(first,true,168);await first.CheckAsync();Check(first.View.State=="failed","Rate limits must not be reported as up-to-date.");await Stop(first,clock);}
        clock.Advance(TimeSpan.FromDays(1));var handler=new Handler();using var restarted=Service(root,"quota",handler,clock);restarted.StartAutomatic();
        clock.Advance(TimeSpan.FromSeconds(20));await Until(()=>clock.Next!=null,"Persisted quota must rearm after restart.");
        Check(handler.Requests==0,"Restart must respect both weekly attempt freshness and a longer server deadline.");
        for(int day=1;day<=8;day++){clock.Advance(TimeSpan.FromDays(1));await Until(()=>clock.Next!=null,"Long quota must keep bounded timer chunks.");Check(handler.Requests==0,"Quota must not be shortened to the configured interval.");}
        clock.Advance(TimeSpan.FromDays(1));await Until(()=>handler.Requests==1 && clock.Next!=null,"Checking must resume after the actual server deadline.");await Stop(restarted,clock);
        var isolatedClock=new Clock();var isolatedHandler=new Handler();using var isolated=Service(root,"isolated",isolatedHandler,isolatedClock,"unit");isolated.StartAutomatic();isolatedClock.Advance(TimeSpan.FromDays(40));
        Check(isolatedHandler.Requests==0 && isolatedClock.Next==null,"Isolated development/test profiles must not check automatically.");
    }
}
