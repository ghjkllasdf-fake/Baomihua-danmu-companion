using System;
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
    private readonly Func<string,string,string,CancellationToken,Task<JsonObject>> fetch;
    private readonly Action<AutomaticMatchResult> loaded;
    private readonly Action<AutomaticMatchStatus> changed;
    private readonly Action<string> notify;
    private readonly int candidateLimit;
    private readonly Func<int,int> randomIndex;
    private readonly TimeProvider timeProvider;
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
        int candidateLimit=0,Func<int,int> randomIndex=null,TimeProvider timeProvider=null)
    {
        if(candidateLimit is not (0 or 3))throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        this.fetch=fetch;this.loaded=loaded;this.changed=changed;this.notify=notify;
        this.candidateLimit=candidateLimit;this.randomIndex=randomIndex??Random.Shared.Next;
        this.timeProvider=timeProvider??TimeProvider.System;
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
            {Fail(owner,"未配置弹幕服务，请点击通知打开工作台填写 API 地址。");return;}
            apiBase=ApiClient.NormalizeBase(apiBase);
            Set(new("matching","正在自动搜索并匹配当前视频的弹幕…"));
            Check(owner,stop);
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
                        return;
                    }
                }
                catch(OperationCanceledException){throw;}
                catch(ApiRateLimitException) when(candidateLimit>0){throw;}
                catch {Check(owner,stop);}
                unavailableSource=preferred.Id;binding=null;
            }
            var search=await fetch(apiBase,"search",title.Trim(),stop);Check(owner,stop);
            var sources=(search["animes"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(s=>new {Id=s["animeId"]?.ToString()??"",Title=s["animeTitle"]?.ToString()??""})
                .Where(s=>s.Id.Length>0 && s.Id.Length<=500 && s.Id!=unavailableSource && Rank(title,s.Title)>=0)
                .OrderBy(s=>Rank(title,s.Title)).DistinctBy(s=>s.Id).ToArray();
            if(sources.Length==0){Fail(owner,unavailableSource.Length>0 ? "同剧来源没有本集的可用弹幕，且没有找到其他匹配来源，请点击通知手动处理。" : "没有找到当前作品的弹幕来源，请点击通知手动搜索或换源。");return;}
            AutomaticMatchResult best=null;
            var candidates=Candidates(sources,remaining);
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
                catch(ApiRateLimitException) when(candidateLimit>0){throw;}
                catch {Check(owner,stop);} // A broken source must not prevent trying other matching sources.
            }
            Check(owner,stop);
            if(best!=null)
            {
                string sampling=candidateLimit>0 ? "随机候选" : "来源";
                Apply(apiBase,best,owner,$"已自动匹配并载入 {best.Comments.Length:N0} 条弹幕，已比较 {candidates.Length} 个{sampling}；同剧后续集数沿用此来源。");
                return;
            }
            Fail(owner,"匹配的来源没有当前分集的可用弹幕，或服务读取失败，请点击通知手动处理。");
        }
        catch(OperationCanceledException)
        {if(Current(owner))Fail(owner,"自动匹配超时，请点击通知检查弹幕服务或手动重试。");}
        catch(ApiRateLimitException error){Fail(owner,error.Message);}
        catch
        {Fail(owner,"自动匹配失败，请点击通知检查弹幕服务并手动重试。");}
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
        var choices=episodes.OfType<JsonObject>().ToArray();
        var selected=episode>0 ? choices.FirstOrDefault(e=>int.TryParse(e["episodeNumber"]?.ToString(),
            NumberStyles.Integer,CultureInfo.InvariantCulture,out int number) && number==episode)
            : choices.Length==1 ? choices[0] : null;
        string id=selected?["episodeId"]?.ToString()??"";
        if(id.Length is <1 or >500)return null;
        var body=await fetch(apiBase,"comments",id,stop);Check(owner,stop);
        var comments=await Task.Run(()=>ApiClient.ParseComments(body),stop);Check(owner,stop);
        return comments.Length==0 ? null : new(revision,title,episode,sourceId,source,id,comments);
    }
    private static string TitleKey(string text)
    {
        text=Regex.Replace(text??"", @"[（(](?:19|20)\d{2}[)）].*$","");
        return Regex.Replace(text.Normalize(NormalizationForm.FormKC),@"[\s\p{P}\p{S}]","").ToUpperInvariant();
    }
    private static int Rank(string title,string candidate)
    {
        if(TitleKey(title)!=TitleKey(candidate))return -1;
        var year=Regex.Match(title,@"[（(]((?:19|20)\d{2})[)）]");
        if(year.Success && !candidate.Contains(year.Groups[1].Value,StringComparison.Ordinal))return -1;
        return string.Equals(title.Trim(),candidate.Trim(),StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
    public void Dispose() {if(disposed)return;disposed=true;Cancel();}
}
