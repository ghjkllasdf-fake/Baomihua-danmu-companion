using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed record UpdateText(byte[] Body,string ETag,bool NotModified)
{
    public string Text=>Encoding.UTF8.GetString(Body);
}

internal sealed class UpdateTransport : IDisposable
{
    private readonly HttpClient client;
    private string cacheKey="",cacheTag="";
    private UpdateRelease cached;
    public UpdateTransport(HttpMessageHandler handler=null)
    {
        client=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false,UseDefaultCredentials=false}){Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DanmuCompanion-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version","2022-11-28");
    }
    private static void VerifyUri(Uri uri,bool redirected)
    {
        UpdatePackage.Check(uri!=null && uri.IsAbsoluteUri && uri.Scheme=="https" && uri.IsDefaultPort && uri.UserInfo.Length==0 && uri.AbsoluteUri.Length<=8192,"更新地址必须来自受信任的 HTTPS GitHub 源。");
        bool repository=uri.Host=="api.github.com" && uri.AbsolutePath=="/repos/"+UpdateCatalog.Repository+"/releases" ||
            uri.Host=="github.com" && uri.AbsolutePath.StartsWith("/"+UpdateCatalog.Repository+"/releases/download/",StringComparison.Ordinal);
        bool assetHost=redirected && uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
        UpdatePackage.Check(repository || assetHost,"更新重定向离开了受信任的 GitHub 下载源。");
    }
    private async Task<HttpResponseMessage> GetAsync(Uri uri,CancellationToken token,string etag=null)
    {
        for(int redirect=0;redirect<=5;redirect++)
        {
            VerifyUri(uri,redirect>0);
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            if(etag!=null && uri.Host=="api.github.com")request.Headers.TryAddWithoutValidation("If-None-Match",etag);
            var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location=response.Headers.Location;response.Dispose();UpdatePackage.Check(location!=null,"GitHub 重定向缺少下载地址。");uri=location.IsAbsoluteUri ? location : new Uri(uri,location);continue;
            }
            if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {response.Dispose();throw new InvalidOperationException("GitHub 暂时限制请求，请稍后重试。");}
            if(response.StatusCode!=HttpStatusCode.OK && response.StatusCode!=HttpStatusCode.NotModified)
            {int status=(int)response.StatusCode;response.Dispose();if(status>=500)throw new HttpRequestException("GitHub 下载源暂时不可用。");throw new InvalidDataException("GitHub 更新请求失败，请稍后重试。");}
            return response;
        }
        throw new InvalidDataException("GitHub 更新重定向次数过多。");
    }
    public async Task<UpdateText> ReadTextAsync(Uri uri,int maximum,CancellationToken token,string etag=null)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response=await GetAsync(uri,timeout.Token,etag).ConfigureAwait(false);
        if(response.StatusCode==HttpStatusCode.NotModified)return new UpdateText(Array.Empty<byte>(),etag??"",true);
        UpdatePackage.Check(response.Content.Headers.ContentLength is not long size || size<=maximum,"GitHub 更新元数据超过大小上限。");
        using var input=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);using var memory=new MemoryStream();byte[] buffer=new byte[16384];int count;
        while((count=await input.ReadAsync(buffer,timeout.Token).ConfigureAwait(false))>0)
        {UpdatePackage.Check(memory.Length+count<=maximum,"GitHub 更新元数据超过大小上限。");await memory.WriteAsync(buffer.AsMemory(0,count),timeout.Token).ConfigureAwait(false);}
        string tag=response.Headers.ETag?.ToString()??"";return new UpdateText(memory.ToArray(),tag.Length<=256 ? tag : "",false);
    }
    public async Task<UpdateRelease> DiscoverAsync(string current,string channel,bool includeApi,string distribution,CancellationToken token)
    {
        string key=current+":"+channel+":"+includeApi+":"+distribution;UpdateRelease selected=null;
        for(int page=1;page<=10;page++)
        {
            var uri=new Uri($"https://api.github.com/repos/{UpdateCatalog.Repository}/releases?per_page=100&page={page}");
            var response=await ReadTextAsync(uri,2*1024*1024,token,page==1 && key==cacheKey && cacheTag.Length>0 ? cacheTag : null).ConfigureAwait(false);
            if(response.NotModified){UpdatePackage.Check(page==1 && key==cacheKey && cacheTag.Length>0,"GitHub 返回了无法使用的更新缓存。");return cached;}
            using var doc=JsonDocument.Parse(response.Body,new JsonDocumentOptions{MaxDepth=16});UpdatePackage.Unique(doc.RootElement);
            UpdatePackage.Check(doc.RootElement.ValueKind==JsonValueKind.Array,"GitHub 版本列表无效。");
            var found=UpdateCatalog.Select(response.Text,current,channel,includeApi,distribution);
            if(found!=null && (selected==null || UpdateVersion.Newer(found.Version,selected.Version)))selected=found;
            if(doc.RootElement.GetArrayLength()<100)
            {
                // A first-page ETag cannot validate later pages of a paginated list.
                cacheKey=key;cacheTag=page==1 ? response.ETag : "";cached=selected;return selected;
            }
        }
        throw new InvalidDataException("GitHub 版本列表超过检查上限，未认定当前版本为最新。");
    }
    public async Task DownloadAsync(UpdateAsset asset,string destination,string checksum,Action<long,long> progress,CancellationToken token)
    {
        UpdatePackage.Check(UpdateCatalog.HashValid(checksum) && (asset.Digest.Length==0 || asset.Digest.Equals(checksum,StringComparison.OrdinalIgnoreCase)),"更新附件摘要与校验清单不一致。");
        string temporary=destination+".partial";UpdatePackage.SafeRoot(Path.GetDirectoryName(destination));
        UpdatePackage.Check(!File.Exists(destination) && !File.Exists(temporary),"更新下载文件已存在，未覆盖。");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromMinutes(20));
        for(int attempt=0;attempt<3;attempt++)
        {
            bool created=false;
            try
            {
                using var response=await GetAsync(asset.Url,timeout.Token).ConfigureAwait(false);
                UpdatePackage.Check(response.StatusCode==HttpStatusCode.OK && (response.Content.Headers.ContentLength is not long length || length==asset.Size),"GitHub 下载长度与附件记录不符。");
                using var input=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
                {
                    created=true;byte[] buffer=new byte[65536];long total=0;progress?.Invoke(0,asset.Size);
                    while(true)
                    {
                        int count;try{count=await input.ReadAsync(buffer,timeout.Token).ConfigureAwait(false);}catch(IOException e){throw new HttpRequestException("更新下载连接中断。",e);}
                        if(count==0)break;total+=count;UpdatePackage.Check(total<=asset.Size && total<=UpdateCatalog.MaxDownload,"更新下载超过大小上限。");
                        hash.AppendData(buffer,0,count);await output.WriteAsync(buffer.AsMemory(0,count),timeout.Token).ConfigureAwait(false);progress?.Invoke(total,asset.Size);
                    }
                    UpdatePackage.Check(total==asset.Size && Convert.ToHexString(hash.GetHashAndReset()).Equals(checksum,StringComparison.OrdinalIgnoreCase),"更新下载校验失败，未安装。");
                    await output.FlushAsync(timeout.Token).ConfigureAwait(false);
                }
                File.Move(temporary,destination);return;
            }
            catch(HttpRequestException) when(attempt<2 && !timeout.IsCancellationRequested)
            {if(created)File.Delete(temporary);await Task.Delay(TimeSpan.FromSeconds(2*(attempt+1)),timeout.Token).ConfigureAwait(false);}
            catch{if(created)File.Delete(temporary);throw;}
        }
    }
    public void Dispose()=>client.Dispose();
}
