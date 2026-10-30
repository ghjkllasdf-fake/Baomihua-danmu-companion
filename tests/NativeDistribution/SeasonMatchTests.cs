using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class SeasonMatchTests
{
    private static JsonObject Json(string value)=>(JsonObject)JsonNode.Parse(value);
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static async Task Run()
    {
        foreach(int limit in new[]{0,3})
        {
            var searches=new List<string>();var read=new List<string>();AutomaticMatchResult loaded=null;
            Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
            {
                if(action=="search")
                {
                    searches.Add(value);
                    return Task.FromResult(value=="示例剧" ? Json("""
                        {"animes":[{"animeId":"first","animeTitle":"示例剧 第1季 (2019) 源A"},{"animeId":"second","animeTitle":"示例剧 第二季 (2024) 源B"}]}
                        """) : Json("""{"animes":[]}"""));
                }
                if(action=="episodes"){read.Add(value);return Task.FromResult(Json($$$"""{"bangumi":{"episodes":[{"episodeId":"{{{value}}}-2","episodeNumber":2}]}}"""));}
                return Task.FromResult(Json("""{"comments":[{"p":"1,1,16777215","m":"测试弹幕"}]}"""));
            }
            using var matcher=new AutomaticMatcher(Fetch,result=>loaded=result,_=>{},_=>{},limit,_=>0,retryDelay:(_,_)=>Task.CompletedTask);
            matcher.Observe("https://danmu.example.com","示例剧 第一季",2,1);await matcher.Completion;
            Check(loaded?.EpisodeId=="first-2","Chinese season aliases must fall back to the base-title query and load only the matching first season.");
            Check(searches.Contains("示例剧") && !read.Contains("second"),"The base query must remain available, and other seasons must be excluded before candidate sampling.");
        }
        foreach(int limit in new[]{0,3})
        {
            var requests=new List<string>();var loaded=new List<AutomaticMatchResult>();bool empty=false;
            Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
            {
                requests.Add(action+":"+value);
                if(action=="search")return Task.FromResult(Json("""
                    {"animes":[{"animeId":"wrong","animeTitle":"示例剧 第十一季"},{"animeId":"one","animeTitle":"示例剧 第一季 (2019)"},{"animeId":"two","animeTitle":"示例剧2(2024)源A"},{"animeId":"alt","animeTitle":"示例剧 第二季 (2024)源B"}]}
                    """));
                if(action=="episodes")return Task.FromResult(Json($$$"""{"bangumi":{"episodes":[{"episodeId":"{{{value}}}-2","episodeNumber":2},{"episodeId":"{{{value}}}-3","episodeNumber":3},{"episodeId":"{{{value}}}-4","episodeNumber":4}]}}"""));
                int count=value.StartsWith("one-",StringComparison.Ordinal) ? 1 :
                    value=="two-2" ? 4 : value=="alt-2" ? 2 : value=="two-3" && empty ? 0 : value=="alt-3" ? 5 : 2;
                var comments=new JsonArray();for(int i=0;i<count;i++)comments.Add(Json($$"""{"p":"1,1,16777215","m":"测试{{i}}"}"""));
                return Task.FromResult(new JsonObject{["comments"]=comments});
            }
            using var matcher=new AutomaticMatcher(Fetch,loaded.Add,_=>{},_=>{},limit,_=>0,retryDelay:(_,_)=>Task.CompletedTask);
            matcher.Observe("https://danmu.example.com","示例剧 第1季",2,1);await matcher.Completion;
            Check(loaded.LastOrDefault()?.EpisodeId=="one-2" && !requests.Contains("episodes:wrong") && !requests.Contains("episodes:two"),
                "A later season with more comments must not win a first-season match.");
            int before=requests.Count;
            matcher.Observe("https://danmu.example.com","示例剧 第1季",3,2);await matcher.Completion;
            Check(loaded.LastOrDefault()?.EpisodeId=="one-3" && requests.Skip(before).SequenceEqual(new[]{"episodes:one","comments:one-3"}),
                "The next episode within the same season must reuse its bound source.");
            matcher.Observe("https://danmu.example.com","示例剧(2019) 第2季",2,3);await matcher.Completion;
            Check(loaded.LastOrDefault()?.EpisodeId=="two-2","A numeric season alias and a season-specific release year must match the requested later season.");
            empty=true;before=requests.Count;
            matcher.Observe("https://danmu.example.com","示例剧(2019) 第2季",3,4);await matcher.Completion;
            Check(loaded.LastOrDefault()?.EpisodeId=="alt-3" && !requests.Skip(before).Contains("episodes:one"),
                "An empty bound episode must fall back within the current season, without borrowing an earlier season.");
            before=requests.Count;
            matcher.Observe("https://danmu.example.com","示例剧(2019) 第2季",4,5);await matcher.Completion;
            Check(loaded.LastOrDefault()?.EpisodeId=="alt-4" && requests.Skip(before).SequenceEqual(new[]{"episodes:alt","comments:alt-4"}),
                "The replacement source must remain bound within its own season.");
        }
        foreach(string laterTitle in new[]{"示例剧 第二季","示例剧2"})
        {
            int downloads=0;var notices=new List<string>();
            using var matcher=new AutomaticMatcher((api,action,value,stop)=>{
                if(action=="comments")downloads++;
                return Task.FromResult(new JsonObject{["animes"]=new JsonArray(
                    new JsonObject{["animeId"]="one",["animeTitle"]="示例剧 第一季"},
                    new JsonObject{["animeId"]="two",["animeTitle"]=laterTitle})});
            },_=>throw new Exception("An unresolved season must not load."),_=>{},notices.Add,retryDelay:(_,_)=>Task.CompletedTask);
            matcher.Observe("https://danmu.example.com","示例剧",2,1);await matcher.Completion;
            Check(downloads==0 && notices.Count==1 && notices[0].Contains("季"),"Unknown seasons must report ambiguity once instead of selecting by comment count.");
        }
        {
            AutomaticMatchResult loaded=null;
            using var matcher=new AutomaticMatcher((api,action,value,stop)=>Task.FromResult(action=="search" ?
                Json("""{"animes":[{"animeId":"all","animeTitle":"示例剧"}]}""") : action=="episodes" ?
                Json("""{"bangumi":{"episodes":[{"episodeId":"s1e2","episodeNumber":2,"episodeTitle":"S01E02"},{"episodeId":"s2e2","episodeNumber":48,"episodeTitle":"S02E02"}]}}""") :
                Json("""{"comments":[{"p":"1,1,16777215","m":"第二季"}]}""")),result=>loaded=result,_=>{},_=>{},retryDelay:(_,_)=>Task.CompletedTask);
            matcher.Observe("https://danmu.example.com","示例剧 第2季",2,1);await matcher.Completion;
            Check(loaded?.EpisodeId=="s2e2","A combined catalog must use explicit season/episode metadata instead of the aggregate episode index.");
        }
        {
            var held=new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loaded=new List<AutomaticMatchResult>();
            using var matcher=new AutomaticMatcher((api,action,value,stop)=>{
                if(action=="search" && value=="示例剧 第1季"){started.TrySetResult(true);return held.Task;}
                return Task.FromResult(action=="search" ? Json("""{"animes":[{"animeId":"two","animeTitle":"示例剧 第二季"}]}""") :
                    action=="episodes" ? Json("""{"bangumi":{"episodes":[{"episodeId":"s2e2","episodeNumber":2}]}}""") :
                    Json("""{"comments":[{"p":"1,1,16777215","m":"第二季"}]}"""));
            },loaded.Add,_=>{},_=>{},retryDelay:(_,_)=>Task.CompletedTask);
            matcher.Observe("https://danmu.example.com","示例剧 第1季",2,1);var old=matcher.Completion;await started.Task;
            matcher.Observe("https://danmu.example.com","示例剧 第2季",2,2);await matcher.Completion;
            held.SetResult(Json("""{"animes":[{"animeId":"one","animeTitle":"示例剧 第一季"}]}"""));await old;
            Check(loaded.Count==1 && loaded[0].EpisodeId=="s2e2" && loaded[0].MediaRevision==2,
                "A late search for the previous season must not overwrite the same episode number in the new season.");
        }
        Console.WriteLine("PASS: season-aware automatic matching in external and sampled includeAPI modes.");
    }
}
