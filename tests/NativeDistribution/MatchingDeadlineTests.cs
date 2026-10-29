using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class MatchingDeadlineTests
{
    private sealed class Clock : TimeProvider
    {
        private readonly List<Timer> timers=new();
        private TimeSpan elapsed;
        public override ITimer CreateTimer(TimerCallback callback,object state,TimeSpan dueTime,TimeSpan period)
        {
            var timer=new Timer(this,callback,state);timers.Add(timer);timer.Change(dueTime,period);return timer;
        }
        internal void Advance(TimeSpan by)
        {
            elapsed+=by;
            foreach(var timer in timers.ToArray())timer.Fire(elapsed);
        }
        private sealed class Timer : ITimer
        {
            private readonly Clock owner;private readonly TimerCallback callback;private readonly object state;
            private TimeSpan? due;
            internal Timer(Clock owner,TimerCallback callback,object state){this.owner=owner;this.callback=callback;this.state=state;}
            public bool Change(TimeSpan next,TimeSpan period)
            {
                if(period!=Timeout.InfiniteTimeSpan)throw new Exception("Only one-shot deadlines are expected.");
                due=next==Timeout.InfiniteTimeSpan ? null : owner.elapsed+next;return true;
            }
            internal void Fire(TimeSpan now){if(due is {} at && at<=now){due=null;callback(state);}}
            public void Dispose()=>due=null;
            public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
        }
    }
    internal static async Task Run()
    {
        foreach(bool embedded in new[]{true,false})
        {
            var clock=new Clock();CancellationToken observed=default;AutomaticMatchResult loaded=null;
            var waiting=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
            {
                if(action=="search")return (JsonObject)JsonNode.Parse("{\"animes\":[{\"animeId\":\"1\",\"animeTitle\":\"Movie\"},{\"animeId\":\"2\",\"animeTitle\":\"Movie\"}]}");
                if(action=="episodes")return (JsonObject)JsonNode.Parse("{\"bangumi\":{\"episodes\":[{\"episodeId\":\""+value+"\",\"episodeNumber\":1}]}}");
                if(value=="1")return (JsonObject)JsonNode.Parse("{\"comments\":[{\"p\":\"0,1,1\",\"m\":\"first-source\"}]}");
                observed=stop;waiting.TrySetResult(true);await Task.Delay(Timeout.Infinite,stop);throw new Exception("Cancellation expected.");
            }
            using var matcher=new AutomaticMatcher(Fetch,result=>loaded=result,_=>{},_=>{},embedded ? 3 : 0,null,clock);
            matcher.Observe("http://fixture.invalid/token","Movie",1,1);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if(!observed.CanBeCanceled)throw new Exception("The pending request must carry a deadline token.");
            clock.Advance(TimeSpan.FromSeconds(60));
            if(embedded)
            {
                if(observed.IsCancellationRequested || matcher.Completion.IsCompleted)throw new Exception("A real movie must not be cut off at the old 60-second embedded deadline.");
                clock.Advance(TimeSpan.FromSeconds(119));
                if(observed.IsCancellationRequested)throw new Exception("Embedded matching must remain active before 180 seconds.");
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            await matcher.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            if(!observed.IsCancellationRequested || matcher.Status.State!="failed" || loaded!=null)
                throw new Exception("The deadline must cancel the actual request and must not apply partial results.");
        }
        Console.WriteLine("PASS: real cancellation tokens use embedded 180s/external 60s deadlines; no late partial result.");
    }
}
