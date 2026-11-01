using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed class ReleaseNoticeRateLimitException(TimeSpan retryAfter) : InvalidOperationException("GitHub 暂时限制版本检查，请稍后重试。")
{internal TimeSpan RetryAfter {get;}=retryAfter;}

internal sealed class ReleaseNoticeTransport : IDisposable
{
    private readonly HttpClient client;
    private readonly TimeProvider clock;
    private string cacheKey="",cacheTag="";
    private ReleaseNotice cached;
    internal ReleaseNoticeTransport(HttpMessageHandler handler=null,TimeProvider clock=null)
    {
        this.clock=clock??TimeProvider.System;
        client=new(handler??new HttpClientHandler{AllowAutoRedirect=false,UseDefaultCredentials=false}){Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DanmuCompanion-ReleaseNotice/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version","2022-11-28");
    }
    internal async Task<ReleaseNotice> DiscoverAsync(string current,string channel,CancellationToken token)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string key=current+":"+channel;ReleaseNotice selected=null;
        for(int page=1;page<=10;page++)
        {
            // There is deliberately no generic URL, redirect, asset or download API.
            using var request=new HttpRequestMessage(HttpMethod.Get,$"https://api.github.com/repos/{ReleaseNoticeCatalog.Repository}/releases?per_page=100&page={page}");
            if(page==1 && key==cacheKey && cacheTag.Length>0)request.Headers.TryAddWithoutValidation("If-None-Match",cacheTag);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
            if(response.StatusCode==HttpStatusCode.NotModified)
            {
                ReleaseNoticeStorage.Check(page==1 && key==cacheKey && cacheTag.Length>0,"GitHub 返回了无法使用的版本缓存。");return cached;
            }
            if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                var now=clock.GetUtcNow();var retry=response.Headers.RetryAfter;
                var wait=retry?.Delta??(retry?.Date is DateTimeOffset date ? date-now : TimeSpan.Zero);
                if(response.Headers.TryGetValues("X-RateLimit-Remaining",out var remaining) && remaining.Contains("0") &&
                    response.Headers.TryGetValues("X-RateLimit-Reset",out var resets) && long.TryParse(resets.FirstOrDefault(),out long seconds) && seconds is >=0 and <=253402300799)
                {var reset=DateTimeOffset.FromUnixTimeSeconds(seconds)-now;if(reset>wait)wait=reset;}
                throw new ReleaseNoticeRateLimitException(wait>TimeSpan.Zero ? wait : TimeSpan.Zero);
            }
            if((int)response.StatusCode>=500)throw new HttpRequestException("GitHub 版本服务暂时不可用。");
            ReleaseNoticeStorage.Check(response.StatusCode==HttpStatusCode.OK,"GitHub 版本检查失败；未跟随重定向。");
            const int maximum=2*1024*1024;
            ReleaseNoticeStorage.Check(response.Content.Headers.ContentLength is not long size || size<=maximum,"GitHub 版本信息超过大小上限。");
            using var input=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);using var bytes=new MemoryStream();
            byte[] buffer=new byte[16384];int count;
            while((count=await input.ReadAsync(buffer,timeout.Token).ConfigureAwait(false))>0)
            {ReleaseNoticeStorage.Check(bytes.Length+count<=maximum,"GitHub 版本信息超过大小上限。");bytes.Write(buffer,0,count);}
            using var doc=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=16});
            var found=ReleaseNoticeCatalog.Select(doc.RootElement,current,channel);
            if(found!=null && (selected==null || ReleaseVersion.Newer(found.Version,selected.Version)))selected=found;
            if(doc.RootElement.GetArrayLength()<100)
            {
                string etag=response.Headers.ETag?.ToString()??"";
                cacheKey=key;cacheTag=page==1 && etag.Length<=256 ? etag : "";cached=selected;return selected;
            }
        }
        throw new InvalidDataException("GitHub 版本列表超过检查上限，未认定当前版本为最新。");
    }
    public void Dispose()=>client.Dispose();
}
