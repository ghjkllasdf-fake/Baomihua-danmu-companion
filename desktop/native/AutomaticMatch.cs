using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public sealed record AutomaticMatchStatus(string State,string Message,string SourceTitle="");
public sealed record AutomaticMatchResult(long MediaRevision,string Title,int EpisodeNumber,string SourceId,string SourceTitle,string EpisodeId,Comment[] Comments);

// Owns one automatic operation. The host still checks actual player identity before applying its result.
public sealed class AutomaticMatcher : IDisposable
{
    private sealed record SourceBinding(string Api,string Title,string Id,string SourceTitle);
    private sealed record SeasonSource(string Id,string Title,int Season,int Rank);
    private sealed class SeasonAmbiguityException : Exception {}
    private readonly Func<string,string,string,CancellationToken,Task<JsonObject>> fetch;
    private readonly Action<AutomaticMatchResult> loaded;
    private readonly Action<AutomaticMatchStatus> changed;
    private readonly Action<string> notify;
    private readonly int candidateLimit;
    private readonly Func<int,int> randomIndex;
    private readonly TimeProvider timeProvider;
    private readonly Func<TimeSpan,CancellationToken,Task> retryDelay;
    internal long OperationId=>generation;
    private CancellationTokenSource pending;
    private long generation;
    private string key="";
    private SourceBinding binding;
    private bool disposed;
    // Real movie sources can take almost a minute for a single response.
    internal TimeSpan RoundTimeout=>TimeSpan.FromSeconds(candidateLimit>0 ? 180 : 60);
    public Task Completion { get; private set; }=Task.CompletedTask;
    public AutomaticMatchStatus Status { get; private set; }=new("waiting","等待爆米花播放视频，识别后自动匹配弹幕。");
    public AutomaticMatcher(Func<string,string,string,CancellationToken,Task<JsonObject>> fetch,
        Action<AutomaticMatchResult> loaded,Action<AutomaticMatchStatus> changed,Action<string> notify,
        int candidateLimit=0,Func<int,int> randomIndex=null,TimeProvider timeProvider=null,
        Func<TimeSpan,CancellationToken,Task> retryDelay=null)
    {
        if(candidateLimit is not (0 or 3))throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        this.fetch=fetch;this.loaded=loaded;this.changed=changed;this.notify=notify;
        this.candidateLimit=candidateLimit;this.randomIndex=randomIndex??Random.Shared.Next;
        this.timeProvider=timeProvider??TimeProvider.System;
        this.retryDelay=retryDelay??((delay,stop)=>Task.Delay(delay,this.timeProvider,stop));
    }

    private T[] Candidates<T>(T[] source,int remaining)
    {
        if(candidateLimit==0 || source.Length<=remaining)return source;
        var shuffled=(T[])source.Clone();
        for(int i=0;i<remaining;i++)
        {
            int j=i+randomIndex(shuffled.Length-i);
            (shuffled[i],shuffled[j])=(shuffled[j],shuffled[i]);
        }
        return shuffled.Take(remaining).ToArray();
    }

    public void Observe(string apiBase,string title,int episode,long revision)
    {
        if(disposed)return;
        string next=apiBase+"\n"+title+"\n"+episode+"\n"+revision;
        if(next==key)return;
        key=next;Cancel();
        try {if(!string.IsNullOrWhiteSpace(apiBase))apiBase=ApiClient.NormalizeBase(apiBase);} catch(ArgumentException) { }
        if(binding!=null && (binding.Api!=apiBase || !string.IsNullOrWhiteSpace(title) && binding.Title!=title))binding=null;
        if(string.IsNullOrWhiteSpace(title))
        {
            Set(new("waiting","等待爆米花播放视频，识别后自动匹配弹幕。"));return;
        }
        pending=new CancellationTokenSource(RoundTimeout,timeProvider);
        Completion=Match(apiBase,title,episode,revision,generation,pending.Token);
    }
    public void Suspend()
    {
        if(disposed)return;
        Cancel();binding=null;
        Set(new("manual","正在手动处理弹幕来源；下次切换视频后恢复自动匹配。"));
    }
    private void Cancel() {generation++;pending?.Cancel();pending?.Dispose();pending=null;}
    private bool Current(long owner)=>!disposed && generation==owner;
    private void Check(long owner,CancellationToken stop)
    {stop.ThrowIfCancellationRequested();if(!Current(owner))throw new OperationCanceledException();}
    private void Set(AutomaticMatchStatus status) {Status=status;changed(status);}
    private void Fail(long owner,string message)
    {
        if(!Current(owner))return;
        Set(new("failed",message));notify(message);
    }
    private async Task Match(string apiBase,string title,int episode,long revision,long owner,CancellationToken stop)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(apiBase))
            {Fail(owner,"未配置弹幕服务，请打开托盘面板填写 API 地址。");return;}
            apiBase=ApiClient.NormalizeBase(apiBase);
            string failure="未找到弹幕，请打开托盘面板手动搜索或换源。";
            for(int round=1;round<=3;round++)
            {
                Check(owner,stop);
                Set(new("matching",$"搜索弹幕源（{round}/3）"));
                try {if(await MatchRound(apiBase,title,episode,revision,owner,stop))return;}
                catch(OperationCanceledException){throw;}
                catch(Exception error) when(error is ApiRateLimitException or ApiAuthenticationException){throw;}
                catch(SeasonAmbiguityException){Check(owner,stop);failure="已找到多季弹幕，但当前视频没有可靠季数；请在爆米花中打开对应季的详情，或在托盘面板手动选择。";}
                catch {Check(owner,stop);failure="弹幕服务读取失败，请检查网络或打开托盘面板手动搜索。";}
                if(round<3)
                {
                    await retryDelay(TimeSpan.FromSeconds(round),stop);
                    Check(owner,stop);
                }
            }
            Fail(owner,failure);
        }
        catch(OperationCanceledException)
        {if(Current(owner))Fail(owner,"自动匹配超时，请打开托盘面板检查服务或手动重试。");}
        catch(Exception error) when(error is ApiRateLimitException or ApiAuthenticationException or ArgumentException)
        {Fail(owner,error.Message);}
        catch {Fail(owner,"自动匹配失败，请打开托盘面板检查服务并手动重试。");}
    }
    private async Task<bool> MatchRound(string apiBase,string title,int episode,long revision,long owner,CancellationToken stop)
    {
            Check(owner,stop);
            bool readFailed=false;
            string unavailableSource="";
            int remaining=candidateLimit==0 ? int.MaxValue : candidateLimit;
            if(binding!=null)
            {
                remaining--;
                var preferred=binding;
                try
                {
                    var body=await fetch(apiBase,"episodes",preferred.Id,stop);Check(owner,stop);
                    var episodes=(body["bangumi"] as JsonObject)?["episodes"] as JsonArray;
                    var reused=episodes==null ? null : await ReadSource(apiBase,title,episode,revision,preferred.Id,preferred.SourceTitle,episodes,owner,stop);
                    if(reused!=null)
                    {
                        Apply(apiBase,reused,owner,$"已沿用同剧来源并载入 {reused.Comments.Length:N0} 条弹幕。");
                        return true;
                    }
                }
                catch(OperationCanceledException){throw;}
                catch(Exception error) when(error is ApiRateLimitException or ApiAuthenticationException){throw;}
                catch {Check(owner,stop);readFailed=true;}
                unavailableSource=preferred.Id;binding=null;
            }
            var identity=SeriesIdentity.Parse(title);
            var found=new List<SeasonSource>();
            foreach(string query in identity.SearchQueries())
            {
                JsonObject search;
                try{search=await fetch(apiBase,"search",query,stop);Check(owner,stop);}
                catch(OperationCanceledException){throw;}
                catch(Exception error) when(error is ApiRateLimitException or ApiAuthenticationException){throw;}
                catch{Check(owner,stop);readFailed=true;continue;}
                foreach(var item in (search["animes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    var source=Source(identity,item["animeId"]?.ToString()??"",item["animeTitle"]?.ToString()??"",episode>0);
                    if(source!=null && source.Id!=unavailableSource)found.Add(source);
                }
            }
            var sources=found.OrderBy(s=>s.Rank).DistinctBy(s=>s.Id).ToArray();
            if(identity.Season==0 && sources.Any(s=>s.Season>0))throw new SeasonAmbiguityException();
            if(sources.Length==0)
            {
                if(readFailed)throw new InvalidOperationException("弹幕服务读取失败。");
                return false;
            }
            AutomaticMatchResult best=null;
            // Dedicated season sources take priority over a plain title which still needs
            // per-episode season proof. Wrong seasons never consume the sampled budget.
            var direct=sources.Where(s=>identity.Season<2 || s.Season==identity.Season).ToArray();
            var candidates=Candidates(direct,remaining);
            if(candidateLimit==0)candidates=sources;
            else if(candidates.Length<remaining)
                candidates=candidates.Concat(Candidates(sources.Where(s=>!direct.Contains(s)).ToArray(),remaining-candidates.Length)).ToArray();
            int compared=0;
            foreach(var source in candidates)
            {
                Check(owner,stop);
                Set(new("matching",$"正在比较当前分集的弹幕数量（{++compared}/{candidates.Length}）…"));
                try
                {
                    var body=await fetch(apiBase,"episodes",source.Id,stop);Check(owner,stop);
                    var episodes=(body["bangumi"] as JsonObject)?["episodes"] as JsonArray;
                    if(episodes==null)continue;
                    var candidate=await ReadSource(apiBase,title,episode,revision,source.Id,source.Title,episodes,owner,stop);
                    if(candidate!=null && (best==null || candidate.Comments.Length>best.Comments.Length))best=candidate;
                }
                catch(OperationCanceledException){throw;}
                catch(Exception error) when(error is ApiRateLimitException or ApiAuthenticationException){throw;}
                catch(SeasonAmbiguityException){throw;}
                catch {Check(owner,stop);readFailed=true;} // Still compare other matching sources.
            }
            Check(owner,stop);
            if(best!=null)
            {
                string sampling=candidateLimit>0 ? "随机候选" : "来源";
                Apply(apiBase,best,owner,$"已自动匹配并载入 {best.Comments.Length:N0} 条弹幕，已比较 {candidates.Length} 个{sampling}；同剧后续集数沿用此来源。");
                return true;
            }
            if(readFailed)throw new InvalidOperationException("弹幕服务读取失败。");
            return false;
    }
    private void Apply(string apiBase,AutomaticMatchResult result,long owner,string message)
    {
        if(!Current(owner))return;
        loaded(result);
        if(!Current(owner))return;
        binding=new(apiBase,result.Title,result.SourceId,result.SourceTitle);
        Set(new("loaded",message,result.SourceTitle));
    }
    private async Task<AutomaticMatchResult> ReadSource(string apiBase,string title,int episode,long revision,string sourceId,string source,
        JsonArray episodes,long owner,CancellationToken stop)
    {
        var target=SeriesIdentity.Parse(title);
        var origin=Source(target,sourceId,source,episode>0);
        if(origin==null)return null;
        var all=episodes.OfType<JsonObject>().Select(e=>new {Node=e,Identity=SeriesIdentity.Parse(e["episodeTitle"]?.ToString()??"")}).ToArray();
        if(target.Season==0 && origin.Season==0 && all.Any(e=>e.Identity.Season>0))throw new SeasonAmbiguityException();
        var choices=all.Where(e=>
            (e.Identity.Season==0 || target.Season==0 || e.Identity.Season==target.Season) &&
            (e.Identity.Season==0 || origin.Season==0 || e.Identity.Season==origin.Season) &&
            (target.Season<2 || origin.Season>0 || e.Identity.Season==target.Season)).ToArray();
        var selected=episode>0 ? choices.FirstOrDefault(e=>
            (e.Identity.Season>0 && e.Identity.Episode>0 ? e.Identity.Episode :
                int.TryParse(e.Node["episodeNumber"]?.ToString(),NumberStyles.Integer,CultureInfo.InvariantCulture,out int number) ? number : e.Identity.Episode)==episode)
            : choices.Length==1 ? choices[0] : null;
        string id=selected?.Node["episodeId"]?.ToString()??"";
        if(id.Length is <1 or >500)return null;
        Set(new("loading","已发现弹幕，进行装载"));
        var body=await fetch(apiBase,"comments",id,stop);Check(owner,stop);
        var comments=await Task.Run(()=>ApiClient.ParseComments(body),stop);Check(owner,stop);
        return comments.Length==0 ? null : new(revision,title,episode,sourceId,source,id,comments);
    }
    private static SeasonSource Source(SeriesIdentity target,string id,string candidate,bool episodic)
    {
        if(id.Length is <1 or >500 || candidate.Length>500)return null;
        var actual=SeriesIdentity.Parse(candidate);
        string wanted=SeriesIdentity.TitleKey(target.Title),key=SeriesIdentity.TitleKey(actual.Title);
        int season=actual.Season;
        if(wanted!=key)
        {
            if((target.Season>0 || episodic) && season==0 && wanted.Length>0 && key.StartsWith(wanted,StringComparison.Ordinal) &&
                int.TryParse(key[wanted.Length..],NumberStyles.None,CultureInfo.InvariantCulture,out int sequel) && sequel is >0 and <=999)season=sequel;
            else return null;
        }
        if(target.Season>0 && season>0 && target.Season!=season)return null;
        var year=Regex.Match(target.Title,@"[（(]((?:19|20)\d{2})[)）]");
        // A series' original year is not necessarily the release year of a later season.
        if(target.Season<2 && year.Success && !actual.Title.Contains(year.Groups[1].Value,StringComparison.Ordinal))return null;
        int rank=target.Season>0 ? target.Season==season ? 0 : 1 :
            string.Equals(target.Title.Trim(),candidate.Trim(),StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        return new(id,candidate,season,rank);
    }
    public void Dispose() {if(disposed)return;disposed=true;Cancel();}
}
