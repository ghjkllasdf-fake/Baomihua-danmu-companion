using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class AutomaticMatchTests
{
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static JsonObject Json(string text)=>(JsonObject)JsonNode.Parse(text);
    public static async Task Run()
    {
        var calls=new List<string>();var loaded=new List<AutomaticMatchResult>();var notices=new List<string>();
        Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
        {
            calls.Add(action+":"+value);
            return Task.FromResult(action switch {
                "search"=>Json("""{"animes":[{"animeId":"wrong","animeTitle":"另一作品"},{"animeId":"empty","animeTitle":"测试作品(2025)源A"},{"animeId":"good","animeTitle":"测试作品(2025)源B"}]}"""),
                "episodes" when value=="empty"=>Json("""{"bangumi":{"episodes":[{"episodeId":"missing","episodeNumber":1}]}}"""),
                "episodes"=>Json("""{"bangumi":{"episodes":[{"episodeId":"e2","episodeNumber":"2"},{"episodeId":"e3","episodeNumber":3}]}}"""),
                _=>Json("""{"comments":[{"p":"1,1,16777215","m":"测试弹幕"}]}""")
            });
        }
        using(var matcher=new AutomaticMatcher(Fetch,loaded.Add,_=>{},notices.Add))
        {
            matcher.Observe("http://fixture.invalid/token","测试作品",2,4);
            await matcher.Completion;
            Check(loaded.Count==1 && loaded[0].MediaRevision==4 && loaded[0].EpisodeId=="e2" && loaded[0].Comments.Length==1,"First identified media must search, select and load without a manual command.");
            Check(!calls.Contains("episodes:wrong") && calls.Contains("episodes:empty") && calls.Contains("episodes:good"),"Reject another title and fall back when a matching source lacks the current episode.");
            Check(notices.Count==0 && matcher.Status.State=="loaded","Successful automatic matching must not notify.");
            int before=calls.Count;
            matcher.Observe("http://fixture.invalid/token","测试作品",2,4);await matcher.Completion;
            Check(calls.Count==before,"Ordinary repeated playback samples must not repeat matching.");
            matcher.Observe("http://fixture.invalid/token","测试作品",3,5);await matcher.Completion;
            Check(loaded.Count==2 && loaded[1].EpisodeId=="e3" && calls.Last()=="comments:e3","The next episode must automatically load from the bound source.");
            matcher.Suspend();matcher.Observe("http://fixture.invalid/token","测试作品",3,5);await matcher.Completion;
            Check(loaded.Count==2,"A manual fallback must own the current media without automatic interference.");
        }
        using(var matcher=new AutomaticMatcher((a,b,c,d)=>Task.FromResult(Json("""{"animes":[]}""")),loaded.Add,_=>{},notices.Add))
        {
            matcher.Observe("http://fixture.invalid/token","找不到的作品",1,1);await matcher.Completion;
            matcher.Observe("http://fixture.invalid/token","找不到的作品",1,1);await matcher.Completion;
            Check(notices.Count==1 && matcher.Status.State=="failed","An unsuccessful search must notify exactly once for the current media.");
            matcher.Observe("","找不到的作品",1,1);await matcher.Completion;
            Check(notices.Count==2 && notices.All(n=>!n.Contains("fixture.invalid")),"Missing configuration must notify without exposing an API address.");
        }
        var held=new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        loaded.Clear();notices.Clear();
        using(var matcher=new AutomaticMatcher((a,action,value,stop)=>{
            if(action=="search" && value=="旧作品"){oldStarted.TrySetResult(true);return held.Task;}
            return Task.FromResult(action=="search" ? Json("""{"animes":[{"animeId":"new","animeTitle":"新作品"}]}""") :
                action=="episodes" ? Json("""{"bangumi":{"episodes":[{"episodeId":"movie","episodeTitle":"正片"}]}}""") :
                Json("""{"comments":[{"p":"0,1,16777215","m":"新视频"}]}"""));
        },loaded.Add,_=>{},notices.Add))
        {
            matcher.Observe("http://fixture.invalid/token","旧作品",1,1);var old=matcher.Completion;
            await oldStarted.Task;
            matcher.Observe("http://fixture.invalid/token","新作品",0,2);await matcher.Completion;
            held.SetResult(Json("""{"animes":[{"animeId":"old","animeTitle":"旧作品"}]}"""));await old;
            Check(loaded.Count==1 && loaded[0].MediaRevision==2 && loaded[0].EpisodeId=="movie","A late old search must not replace a new movie; one movie entry can be chosen without an episode number.");
            Check(notices.Count==0,"Cancellation on media changes must not produce a failure notification.");
        }
        loaded.Clear();notices.Clear();bool failCached=false;
        using(var matcher=new AutomaticMatcher((a,action,value,stop)=>{
            if(action=="search")return Task.FromResult(Json("""{"animes":[{"animeId":"a","animeTitle":"测试作品(2025)源A"},{"animeId":"b","animeTitle":"测试作品(2025)源B"}]}"""));
            if(action=="episodes")return Task.FromResult(Json(value=="a" ?
                """{"bangumi":{"episodes":[{"episodeId":"a2","episodeNumber":2},{"episodeId":"a3","episodeNumber":3}]}}""" :
                """{"bangumi":{"episodes":[{"episodeId":"b3","episodeNumber":3}]}}"""));
            if(failCached && value=="a3")throw new InvalidOperationException("fixture unavailable");
            return Task.FromResult(Json("""{"comments":[{"p":"1,1,16777215","m":"可用弹幕"}]}"""));
        },loaded.Add,_=>{},notices.Add))
        {
            matcher.Observe("http://fixture.invalid/token","测试作品",2,1);await matcher.Completion;
            failCached=true;
            matcher.Observe("http://fixture.invalid/token","测试作品",3,2);await matcher.Completion;
            Check(loaded.Count==2 && loaded[1].EpisodeId=="b3" && notices.Count==0,"A failing previously bound source must fall back automatically to another matching source.");
        }
        loaded.Clear();notices.Clear();int downloads=0;
        using(var matcher=new AutomaticMatcher((a,action,value,stop)=>{
            if(action=="comments")downloads++;
            return Task.FromResult(action=="search" ? Json("""{"animes":[{"animeId":"series","animeTitle":"测试作品"}]}""") :
                Json("""{"bangumi":{"episodes":[{"episodeId":"one","episodeNumber":1},{"episodeId":"two","episodeNumber":2}]}}"""));
        },loaded.Add,_=>{},notices.Add))
        {
            matcher.Observe("http://fixture.invalid/token","测试作品",0,1);await matcher.Completion;
            Check(downloads==0 && loaded.Count==0 && notices.Count==1,"Unknown episode numbers in a multi-episode work must notify instead of silently choosing episode one.");
        }
        loaded.Clear();notices.Clear();var compared=new List<string>();
        using(var matcher=new AutomaticMatcher((a,action,value,stop)=>{
            if(action=="search")return Task.FromResult(Json("""{"animes":[{"animeId":"a","animeTitle":"测试作品(2025)源A"},{"animeId":"b","animeTitle":"测试作品(2025)源B"},{"animeId":"c","animeTitle":"测试作品(2025)源C"}]}"""));
            if(action=="episodes")return Task.FromResult(Json($$$"""{"bangumi":{"episodes":[{"episodeId":"{{{value}}}2","episodeNumber":2},{"episodeId":"{{{value}}}3","episodeNumber":3},{"episodeId":"{{{value}}}4","episodeNumber":4},{"episodeId":"{{{value}}}5","episodeNumber":5}]}}"""));
            compared.Add(value);
            int amount=value switch {"a2"=>1,"b2"=>4,"c2"=>2,"a3"=>5,"b3"=>1,"c3"=>2,"a4"=>2,"b4"=>0,"c4"=>6,"a5"=>9,"b5"=>3,_=>1};
            var comments=new JsonArray();
            for(int i=0;i<amount;i++)comments.Add(Json($$"""{"p":"1,1,16777215","m":"弹幕{{i}}"}"""));
            return Task.FromResult(new JsonObject {["count"]=999999,["comments"]=comments});
        },loaded.Add,_=>{},notices.Add))
        {
            matcher.Observe("http://fixture.invalid/token","测试作品",2,1);await matcher.Completion;
            Check(compared.SequenceEqual(new[]{"a2","b2","c2"}) && loaded.Count==1 &&
                loaded[0].EpisodeId=="b2" && loaded[0].Comments.Length==4,
                "Compare every source's current episode and load only the largest actual parsed comment set, ignoring untrusted count metadata.");
            compared.Clear();
            matcher.Observe("http://fixture.invalid/token","测试作品",3,2);await matcher.Completion;
            Check(compared.SequenceEqual(new[]{"b3"}) && loaded.Count==2 && loaded[1].EpisodeId=="b3" && loaded[1].Comments.Length==1,
                "The same work must keep its bound source even when another source would have more comments on the next episode.");
            compared.Clear();
            matcher.Observe("http://fixture.invalid/token","测试作品",4,3);await matcher.Completion;
            Check(compared.SequenceEqual(new[]{"b4","a4","c4"}) && loaded.Count==3 && loaded[2].EpisodeId=="c4" && loaded[2].Comments.Length==6,
                "When the bound source has zero comments, compare all other current-episode sources and switch to the largest.");
            compared.Clear();
            matcher.Observe("http://fixture.invalid/token","测试作品",5,4);await matcher.Completion;
            Check(compared.SequenceEqual(new[]{"c5"}) && loaded.Count==4 && loaded[3].EpisodeId=="c5",
                "After a fallback switch, subsequent episodes must keep the new bound source.");
            Check(notices.Count==0,"Comparing usable sources must remain quiet.");
        }
        Console.WriteLine("PASS: automatic first match, all-source largest-count selection, same-work binding, empty-episode fallback and rebinding, exact title, duplicate suppression, manual ownership, notifications, movie and late-result fencing.");
    }
}
