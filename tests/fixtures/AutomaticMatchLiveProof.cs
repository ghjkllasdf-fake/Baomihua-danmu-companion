using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Popcorn;

public static class AutomaticMatchLiveProof
{
    public static async Task<string> Run(string apiBase,string title,int episode)
    {
        using var api=new ApiClient();
        AutomaticMatchResult result=null;var notifications=new List<string>();var requests=new List<string>();
        var counts=new Dictionary<string,int>();
        using var matcher=new AutomaticMatcher(async (address,action,value,stop)=>{
            requests.Add(action);
            var body=await api.FetchAsync(address,action,value,stop);
            if(action=="comments")counts[value]=ApiClient.ParseComments(body).Length;
            return body;
        },value=>result=value,_=>{},notifications.Add);
        matcher.Observe(apiBase,title,episode,1);
        await matcher.Completion;
        if(result==null || result.Title!=title || result.EpisodeNumber!=episode || result.Comments.Length<1 ||
            matcher.Status.State!="loaded" || notifications.Count!=0 || result.Comments.Length!=System.Linq.Enumerable.Max(counts.Values))
            throw new InvalidOperationException("Actual automatic service matching did not load the requested media; status="+matcher.Status.State);
        return JsonSerializer.Serialize(new {
            title,episode,source=result.SourceTitle,comments=result.Comments.Length,
            requests,counts,notificationCount=notifications.Count,manualCommands=0,
            scope="Actual automatic matcher and configured service; does not control or prove the current player/rendered presentation."
        });
    }
}
