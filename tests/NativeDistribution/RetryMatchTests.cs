using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class RetryMatchTests
{
    private const string Api="http://fixture.invalid/token";
    private static readonly TimeSpan TestTimeout=TimeSpan.FromSeconds(5);
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }
    private static JsonObject Json(string text)=>(JsonObject)JsonNode.Parse(text);
    private static JsonObject Sources(string title)=>new() { ["animes"]=new JsonArray {
        new JsonObject { ["animeId"]="1",["animeTitle"]=title },
        new JsonObject { ["animeId"]="2",["animeTitle"]=title }
    }};
    private static JsonObject Episodes()=>Json("""{"bangumi":{"episodes":[{"episodeId":"1","episodeNumber":1}]}}""");
    private static JsonObject Comments()=>Json("""{"comments":[{"p":"0,1,16777215","m":"controlled test comment"}]}""");

    internal static async Task Run()
    {
        foreach(int limit in new[]{0,3})
        {
            await ThirdRoundSucceeds(limit);
            await EmptyResultsFailOnceAfterThree(limit);
            await TransientFailureRecovers(limit);
            await SourceReadFailuresAreNotReportedAsMissing(limit,"episodes");
            await SourceReadFailuresAreNotReportedAsMissing(limit,"comments");
            await CancelDuringDelay(limit,false);
            await CancelDuringDelay(limit,true);
            await NewMediaDuringDelay(limit);
            foreach(string stage in new[]{"search","episodes","comments"})
            {
                await PermanentFailure(limit,stage,false);
                await PermanentFailure(limit,stage,true);
            }
            await MissingConfigurationIsTerminal(limit);
        }
        Console.WriteLine("PASS: three bounded search rounds, transient recovery, cancellation and terminal 429/auth for both API editions.");
    }

    private static async Task ThirdRoundSucceeds(int limit)
    {
        int searches=0,loads=0,notifications=0;
        var delays=new ConcurrentQueue<TimeSpan>();
        var statuses=new ConcurrentQueue<string>();
        Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            if(action=="search")return Task.FromResult(Interlocked.Increment(ref searches)<3 ? Json("""{"animes":[]}""") : Sources("Series"));
            return Task.FromResult(action=="episodes" ? Episodes() : Comments());
        }
        Task Delay(TimeSpan delay,CancellationToken stop)
        {
            Check(stop.CanBeCanceled,"Retry waiting must be attached to the current media's cancellation/deadline token.");
            stop.ThrowIfCancellationRequested();delays.Enqueue(delay);return Task.CompletedTask;
        }
        using var matcher=new AutomaticMatcher(Fetch,_=>Interlocked.Increment(ref loads),status=>statuses.Enqueue(status.State),
            _=>Interlocked.Increment(ref notifications),limit,_=>0,retryDelay:Delay);
        matcher.Observe(Api,"Series",1,1);
        await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==3 && loads==1 && notifications==0 && matcher.Status.State=="loaded",
            "The third search round must load once without a premature failure notice in either API edition.");
        Check(delays.SequenceEqual(new[]{TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(2)}),"Retries must have bounded 1 s then 2 s waits.");
        Check(!statuses.Contains("failed"),"Intermediate failed searches must not be announced as final failure.");
        matcher.Observe(Api,"Series",1,1);
        await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==3 && loads==1,"Repeating the same media observation must not restart a successful match.");
    }

    private static async Task EmptyResultsFailOnceAfterThree(int limit)
    {
        int searches=0,loads=0,notifications=0;
        var delays=new ConcurrentQueue<TimeSpan>();var statuses=new ConcurrentQueue<string>();
        using var matcher=new AutomaticMatcher((_,action,_,_)=>{
            Check(action=="search","An empty search must not attempt episodes or comments.");
            Interlocked.Increment(ref searches);return Task.FromResult(Json("""{"animes":[]}"""));
        },_=>Interlocked.Increment(ref loads),status=>statuses.Enqueue(status.State),_=>Interlocked.Increment(ref notifications),
            limit,_=>0,retryDelay:(delay,stop)=>{stop.ThrowIfCancellationRequested();delays.Enqueue(delay);return Task.CompletedTask;});
        matcher.Observe(Api,"Missing",1,2);
        await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==3 && delays.Count==2 && loads==0,"Empty results must stop after exactly three rounds without claiming a load.");
        Check(notifications==1 && statuses.Count(state=>state=="failed")==1 && matcher.Status.State=="failed",
            "Only the final failed round may emit one failure status and one notification.");
        matcher.Observe(Api,"Missing",1,2);
        await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==3 && notifications==1,"Routine player sampling must not repeat exhausted searches or notifications.");
    }

    private static async Task TransientFailureRecovers(int limit)
    {
        int searches=0,loads=0,notifications=0,delays=0;
        Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
        {
            if(action=="search")
            {
                if(Interlocked.Increment(ref searches)==1)throw new InvalidOperationException("controlled network outage");
                return Task.FromResult(Sources("Recover"));
            }
            return Task.FromResult(action=="episodes" ? Episodes() : Comments());
        }
        using var matcher=new AutomaticMatcher(Fetch,_=>Interlocked.Increment(ref loads),_=>{},_=>Interlocked.Increment(ref notifications),
            limit,_=>0,retryDelay:(_,stop)=>{stop.ThrowIfCancellationRequested();Interlocked.Increment(ref delays);return Task.CompletedTask;});
        matcher.Observe(Api,"Recover",1,3);await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==2 && delays==1 && loads==1 && notifications==0 && matcher.Status.State=="loaded",
            "A transient network failure followed by success must recover without alarming the user.");
    }

    private static async Task SourceReadFailuresAreNotReportedAsMissing(int limit,string failingStage)
    {
        int searches=0,sourceFailures=0,loads=0,delays=0;
        var notices=new ConcurrentQueue<string>();
        Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
        {
            if(action=="search"){Interlocked.Increment(ref searches);return Task.FromResult(Sources("Network"));}
            if(action==failingStage)
            {Interlocked.Increment(ref sourceFailures);throw new InvalidOperationException("controlled network outage");}
            return Task.FromResult(Episodes());
        }
        using var matcher=new AutomaticMatcher(Fetch,_=>Interlocked.Increment(ref loads),_=>{},notices.Enqueue,
            limit,_=>0,retryDelay:(_,stop)=>{stop.ThrowIfCancellationRequested();Interlocked.Increment(ref delays);return Task.CompletedTask;});
        matcher.Observe(Api,"Network",1,30);await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==3 && sourceFailures>0 && delays==2 && loads==0 && notices.Count==1 && matcher.Status.State=="failed",
            $"A transient {failingStage} failure must run three bounded rounds and report one final failure.");
        Check((matcher.Status.Message.Contains("网络") || matcher.Status.Message.Contains("读取失败")) && !matcher.Status.Message.Contains("未找到"),
            $"Network failures while reading {failingStage} must not be reported as a missing comment source.");
    }

    private static async Task CancelDuringDelay(int limit,bool honorCancellation)
    {
        int searches=0,loads=0,notifications=0;CancellationToken captured=default;
        var delayStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayGate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Delay(TimeSpan duration,CancellationToken stop)
        {
            captured=stop;delayStarted.TrySetResult(true);
            return honorCancellation ? delayGate.Task.WaitAsync(stop) : delayGate.Task;
        }
        using var matcher=new AutomaticMatcher((_,action,_,_)=>{
            Check(action=="search","Cancellation fixture only returns empty searches.");
            Interlocked.Increment(ref searches);return Task.FromResult(Json("""{"animes":[]}"""));
        },_=>Interlocked.Increment(ref loads),_=>{},_=>Interlocked.Increment(ref notifications),limit,_=>0,retryDelay:Delay);
        matcher.Observe(Api,"Cancel",1,4);Task old=matcher.Completion;
        await delayStarted.Task.WaitAsync(TestTimeout);
        matcher.Observe(Api,"Cancel",1,4);
        Check(searches==1,"Repeated observation must not start another search during retry wait.");
        matcher.Suspend();
        Check(captured.IsCancellationRequested,"Manual intervention must cancel the pending retry token.");
        delayGate.TrySetResult(true);
        await old.WaitAsync(TestTimeout);
        Check(searches==1 && loads==0 && notifications==0 && matcher.Status.State=="manual",
            "A cancelled delay, including one that completes late despite cancellation, must never request or notify again.");
    }

    private static async Task NewMediaDuringDelay(int limit)
    {
        int oldSearches=0,newSearches=0,notifications=0;
        var loads=new ConcurrentQueue<AutomaticMatchResult>();
        var delayStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayGate=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
        {
            if(action=="search" && value=="Old")
            {Interlocked.Increment(ref oldSearches);return Task.FromResult(Json("""{"animes":[]}"""));}
            if(action=="search") {Interlocked.Increment(ref newSearches);return Task.FromResult(Sources("New"));}
            return Task.FromResult(action=="episodes" ? Episodes() : Comments());
        }
        using var matcher=new AutomaticMatcher(Fetch,loads.Enqueue,_=>{},_=>Interlocked.Increment(ref notifications),limit,_=>0,
            retryDelay:(_,_)=>{delayStarted.TrySetResult(true);return delayGate.Task;});
        matcher.Observe(Api,"Old",1,5);Task old=matcher.Completion;
        await delayStarted.Task.WaitAsync(TestTimeout);
        matcher.Observe(Api,"New",1,6);
        await matcher.Completion.WaitAsync(TestTimeout);
        delayGate.TrySetResult(true);await old.WaitAsync(TestTimeout);
        Check(oldSearches==1 && newSearches==1 && loads.Count==1 && loads.Single().MediaRevision==6 && notifications==0 && matcher.Status.State=="loaded",
            "A new media identity must win over an old retry whose wait completes after cancellation.");
    }

    private static async Task PermanentFailure(int limit,string failingStage,bool rateLimit)
    {
        int searches=0,episodes=0,comments=0,loads=0,delays=0;
        var notices=new ConcurrentQueue<string>();
        Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
        {
            if(action=="search")Interlocked.Increment(ref searches);
            if(action=="episodes")Interlocked.Increment(ref episodes);
            if(action=="comments")Interlocked.Increment(ref comments);
            if(action==failingStage)
            {
                if(rateLimit)throw new ApiRateLimitException();
                throw new ApiAuthenticationException();
            }
            return Task.FromResult(action=="search" ? Sources("Denied") : action=="episodes" ? Episodes() : Comments());
        }
        using var matcher=new AutomaticMatcher(Fetch,_=>Interlocked.Increment(ref loads),_=>{},notices.Enqueue,limit,_=>0,
            retryDelay:(_,_)=>{Interlocked.Increment(ref delays);return Task.CompletedTask;});
        matcher.Observe(Api,"Denied",1,7);await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==1 && episodes<2 && comments<2 && delays==0 && loads==0 && matcher.Status.State=="failed" && notices.Count==1,
            $"{(rateLimit ? "429" : "Authentication")} at {failingStage} must terminate without trying another source or retrying, in either edition.");
        string notice=notices.Single();
        Check(!notice.Contains(Api,StringComparison.OrdinalIgnoreCase),"Permanent failure notifications must not disclose the API address.");
        Check(rateLimit ? notice.Contains("限流") : notice.Contains("认证") || notice.Contains("授权"),
            "Permanent failure must report an actionable real reason instead of claiming no comments exist.");
    }

    private static async Task MissingConfigurationIsTerminal(int limit)
    {
        int searches=0,notifications=0,delays=0;
        using var matcher=new AutomaticMatcher((_,_,_,_)=>{Interlocked.Increment(ref searches);return Task.FromResult(Sources("Missing"));},
            _=>throw new Exception("A missing API cannot load."),_=>{},_=>Interlocked.Increment(ref notifications),limit,_=>0,
            retryDelay:(_,_)=>{Interlocked.Increment(ref delays);return Task.CompletedTask;});
        matcher.Observe("","Missing",1,8);await matcher.Completion.WaitAsync(TestTimeout);
        Check(searches==0 && delays==0 && notifications==1 && matcher.Status.State=="failed",
            "A missing API address must stop immediately with one actionable failure, not retry.");
    }
}
