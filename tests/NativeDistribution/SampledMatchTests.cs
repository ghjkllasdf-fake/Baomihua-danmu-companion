using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class SampledMatchTests
{
    private static void Check(bool value,string message) { if(!value)throw new Exception(message); }
    internal static async Task Run()
    {
        using(var embedded=new AutomaticMatcher((_,_,_,_)=>throw new Exception("No request expected."),_=>{},_=>{},_=>{},3))
        using(var external=new AutomaticMatcher((_,_,_,_)=>throw new Exception("No request expected."),_=>{},_=>{},_=>{}))
        {
            Check(embedded.RoundTimeout==TimeSpan.FromSeconds(180),"Embedded movie downloads need a bounded three-minute matching round.");
            Check(external.RoundTimeout==TimeSpan.FromSeconds(60),"The external API matching deadline must remain unchanged.");
        }
        foreach(bool sampled in new[]{true,false})
        foreach(int count in new[]{2,7})
        {
            var calls=new List<string>();AutomaticMatchResult loaded=null;
            Task<JsonObject> Fetch(string api,string action,string value,CancellationToken stop)
            {
                if(action=="search")return Task.FromResult(new JsonObject{["animes"]=new JsonArray(Enumerable.Range(1,count)
                    .Select(n=>(JsonNode)new JsonObject{["animeId"]=n.ToString(),["animeTitle"]="Sample"}).ToArray())});
                if(action=="episodes")return Task.FromResult((JsonObject)JsonNode.Parse("{\"bangumi\":{\"episodes\":[{\"episodeId\":\""+value+"\",\"episodeNumber\":1}]}}"));
                calls.Add(value);
                return Task.FromResult(new JsonObject{["comments"]=new JsonArray(Enumerable.Range(0,int.Parse(value))
                    .Select(_=>(JsonNode)new JsonObject{["p"]="0,1,16777215",["m"]="sample"}).ToArray())});
            }
            using var matcher=new AutomaticMatcher(Fetch,result=>loaded=result,_=>{},_=>{},sampled ? 3 : 0,bound=>bound-1);
            matcher.Observe("http://fixture.invalid/token","Sample",1,1);await matcher.Completion;
            Check(calls.Count==(sampled ? Math.Min(3,count) : count) && calls.Distinct().Count()==calls.Count,"Sampling must bound distinct candidates without changing external mode.");
            Check(loaded?.Comments.Length==calls.Select(int.Parse).Max(),"Choose the most comments among the actual sample only.");
            if(sampled && count==7)Check(calls.Contains("7") && !calls.SequenceEqual(new[]{"1","2","3"}),"Injected randomness must select more than the first three results.");
        }
        var attempted=new List<string>();int round=1;
        Task<JsonObject> BoundFetch(string api,string action,string value,CancellationToken stop)
        {
            if(action=="search")return Task.FromResult(new JsonObject{["animes"]=new JsonArray(Enumerable.Range(1,7)
                .Select(n=>(JsonNode)new JsonObject{["animeId"]=n.ToString(),["animeTitle"]="Bound"}).ToArray())});
            if(action=="episodes") { attempted.Add(value);return Task.FromResult((JsonObject)JsonNode.Parse("{\"bangumi\":{\"episodes\":[{\"episodeId\":\""+value+"\",\"episodeNumber\":"+round+"}]}}")); }
            if(round==2)throw new InvalidOperationException("unavailable");
            return Task.FromResult((JsonObject)JsonNode.Parse("{\"comments\":[{\"p\":\"0,1,1\",\"m\":\"test\"}]}"));
        }
        using(var matcher=new AutomaticMatcher(BoundFetch,_=>{},_=>{},_=>{},3,_=>0))
        {
            matcher.Observe("http://fixture.invalid/token","Bound",1,1);await matcher.Completion;
            attempted.Clear();round=2;
            matcher.Observe("http://fixture.invalid/token","Bound",2,2);await matcher.Completion;
            Check(attempted.Count==9 && Enumerable.Range(0,3).All(n=>attempted.Skip(n*3).Take(3).Distinct().Count()==3),
                "Each of the three retry rounds must retain its three-source budget, including the first round's bound-source attempt.");
        }
        int downloads=0;var notices=new List<string>();
        Task<JsonObject> Limited(string api,string action,string value,CancellationToken stop)
        {
            if(action=="comments"){downloads++;throw new ApiRateLimitException();}
            return BoundFetch(api,action,value,stop);
        }
        using(var matcher=new AutomaticMatcher(Limited,_=>throw new Exception("A limited round cannot claim success."),_=>{},notices.Add,3,_=>0))
        {
            matcher.Observe("http://fixture.invalid/token","Bound",2,3);await matcher.Completion;
            Check(downloads==1 && notices.Count==1 && notices[0].Contains("限流"),"429 must stop the round without blind fallback or retry.");
        }
        int externalRound=1;AutomaticMatchResult externalLoaded=null;
        Task<JsonObject> ExternalLimited(string api,string action,string value,CancellationToken stop)
        {
            if(action=="search")return Task.FromResult((JsonObject)JsonNode.Parse("{\"animes\":[{\"animeId\":\"1\",\"animeTitle\":\"External\"},{\"animeId\":\"2\",\"animeTitle\":\"External\"}]}"));
            if(action=="episodes")return Task.FromResult((JsonObject)JsonNode.Parse("{\"bangumi\":{\"episodes\":[{\"episodeId\":\""+value+"\",\"episodeNumber\":"+externalRound+"}]}}"));
            if(value=="1" && externalRound>1)throw new ApiRateLimitException();
            return Task.FromResult((JsonObject)JsonNode.Parse("{\"comments\":[{\"p\":\"0,1,1\",\"m\":\"test\"}]}"));
        }
        using(var matcher=new AutomaticMatcher(ExternalLimited,result=>externalLoaded=result,_=>{},_=>{}))
        {
            matcher.Observe("http://fixture.invalid/token","External",1,1);await matcher.Completion;
            Check(externalLoaded?.SourceId=="1","External fixture must first bind the preferred source.");
            externalRound=2;externalLoaded=null;
            matcher.Observe("http://fixture.invalid/token","External",2,2);await matcher.Completion;
            Check(externalLoaded==null && matcher.Status.State=="failed" && matcher.Status.Message.Contains("限流"),
                "A bound-source 429 must stop both editions, not amplify the rate limit with fallback.");
        }
        using(var matcher=new AutomaticMatcher(ExternalLimited,result=>externalLoaded=result,_=>{},_=>{}))
        {
            externalLoaded=null;matcher.Observe("http://fixture.invalid/token","External",2,3);await matcher.Completion;
            Check(externalLoaded==null && matcher.Status.State=="failed" && matcher.Status.Message.Contains("限流"),
                "A first-candidate 429 must stop the external edition as well as includeAPI.");
        }
        Console.WriteLine("PASS: sampled-three candidates, bounded source reuse, external baseline and terminal 429.");
    }
}
