using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class SeasonLiveEvidence
{
    internal static void Trace(int seconds)
    {
        Exception failure=null;
        var worker=new Thread(()=>{
            try
            {
                NativeRuntime.LoadMediaProjection(AppContext.BaseDirectory);
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(seconds,1,300)));
                using var sampler=new PlayerSampler(stop.Token);
                var clock=new SyncClock();string media="";
                while(!stop.IsCancellationRequested)
                {
                    var sample=sampler.Poll();double now=Monotonic.Now;
                    string key=sample.Title.Length>0 ? sample.Title+"#"+sample.EpisodeNumber : media;
                    if(media!=key){media=key;clock.BeginMediaEpoch(now);}
                    bool identified=sample.Window!=null && sample.Title.Length>0;
                    clock.Observe(identified ? sample.PositionMs : null,identified ? sample.Playing : null,sample.StampMs,now,key,sample.PositionResolutionMs);
                    Console.WriteLine(JsonSerializer.Serialize(new{now,sample.Title,sample.EpisodeNumber,sample.PositionMs,sample.Playing,sample.Source,
                        age=now-sample.StampMs,clock.SeekRevision,clock.Mode,clockRunning=clock.Running,clockPosition=clock.At(now),seasonEvidence=sampler.SeasonEvidence}));
                    Console.Out.Flush();sampler.WaitForNext(100);
                }
            }
            catch(OperationCanceledException){}
            catch(Exception error){failure=error;}
        }){IsBackground=true};
        worker.SetApartmentState(ApartmentState.MTA);worker.Start();
        if(!worker.Join((Math.Clamp(seconds,1,300)+10)*1000))throw new TimeoutException("Read-only trace exceeded its deadline.");
        if(failure!=null)throw failure;
    }
    internal static PlayerSample Sample()
    {
        PlayerSample result=null;Exception failure=null;
        var worker=new Thread(()=>{
            try
            {
                NativeRuntime.LoadMediaProjection(AppContext.BaseDirectory);
                using var sampler=new PlayerSampler(CancellationToken.None);
                result=sampler.Poll();
                Console.WriteLine(JsonSerializer.Serialize(new{sampleTitle=result.Title,result.EpisodeNumber,result.Source,result.Detail,result.DurationMs,seasonEvidence=sampler.SeasonEvidence}));
            }
            catch(Exception error){failure=error;}
        }){IsBackground=true};
        worker.SetApartmentState(ApartmentState.MTA);worker.Start();
        if(!worker.Join(10000))throw new TimeoutException("Read-only player evidence exceeded its deadline.");
        if(failure!=null)throw failure;
        return result;
    }
    internal static void RunSampler(int expectedSeason)
    {
        var sample=Sample();var identity=SeriesIdentity.Parse(sample.Title);
        if(sample.Window==null || identity.Season!=expectedSeason)throw new Exception("The actual player did not yield the expected season.");
        Console.WriteLine(JsonSerializer.Serialize(new{sample.Title,sample.EpisodeNumber,season=identity.Season,sample.Source,sample.PositionMs,sample.DurationMs,scope="actual read-only player sampler; no playback input or API access"}));
    }
    internal static async Task RunApi(string payload,int expectedSeason)
    {
        var sample=Sample();
        if(sample.Window==null || SeriesIdentity.Parse(sample.Title).Season!=expectedSeason)throw new Exception("The live player season precondition is not met.");
        await RunApiForTitle(payload,sample.Title,sample.EpisodeNumber,"actual player identity and owned bundled API; no endpoint/token logged");
    }
    internal static async Task RunApiForTitle(string payload,string title,int episode,string scope="actual service only; explicit title, not a current-player acceptance test")
    {
        string directory=Path.Combine(Path.GetTempPath(),"danmu-season-live-"+Guid.NewGuid().ToString("N"));
        var paths=new ApplicationPaths(Path.GetFullPath(payload),"season-live",directory);
        using var service=new EmbeddedApiService(paths);
        await service.StartAsync(EmbeddedApiSources.Defaults);
        if(service.View.State!="ready")throw new Exception("The owned includeAPI service did not become ready.");
        using var client=new ApiClient();AutomaticMatchResult loaded=null;
        using var matcher=new AutomaticMatcher(client.FetchAsync,result=>loaded=result,_=>{},_=>{},3);
        matcher.Observe(service.ApiBase,title,episode,1);
        await matcher.Completion;
        if(loaded==null)throw new Exception("Live automatic season match failed: "+matcher.Status.Message);
        Console.WriteLine(JsonSerializer.Serialize(new{title=loaded.Title,episode=loaded.EpisodeNumber,source=loaded.SourceTitle,comments=loaded.Comments.Length,scope}));
    }
    internal static async Task RunCatalog(string payload,string title)
    {
        string directory=Path.Combine(Path.GetTempPath(),"danmu-season-catalog-"+Guid.NewGuid().ToString("N"));
        using var service=new EmbeddedApiService(new ApplicationPaths(Path.GetFullPath(payload),"season-catalog",directory));
        await service.StartAsync(EmbeddedApiSources.Defaults);
        if(service.View.State!="ready")throw new Exception("The owned includeAPI service did not become ready.");
        using var client=new ApiClient();using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var search=await client.FetchAsync(service.ApiBase,"search",title,stop.Token);
        var sources=(search["animes"] as JsonArray).OfType<JsonObject>()
            .OrderByDescending(s=>(s["animeTitle"]?.ToString()??"").Contains("tencent",StringComparison.OrdinalIgnoreCase))
            .GroupBy(s=>SeriesIdentity.Parse(s["animeTitle"]?.ToString()??"").Season).Select(g=>g.First()).Take(4);
        foreach(var source in sources)
        {
            var episodes=await client.FetchAsync(service.ApiBase,"episodes",source["animeId"].ToString(),stop.Token);
            var entries=(episodes["bangumi"]["episodes"] as JsonArray).OfType<JsonObject>().Where(e=>e["episodeNumber"]?.ToString()=="2")
                .Select(e=>e["episodeTitle"]?.ToString()??"").ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new{source=source["animeTitle"].ToString(),episodeTitles=entries}));
        }
    }
}
