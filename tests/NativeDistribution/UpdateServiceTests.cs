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

internal static class UpdateServiceTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public bool Block;
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;
            if(Block)await Task.Delay(Timeout.Infinite,token);
            byte[] payload="verified package fixture"u8.ToArray();string hash=Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            string name=UpdateCatalog.ArtifactName("0.1.1-beta.10",false,"portable");
            string sums=hash+"  "+name+"\n",prefix=$"https://github.com/{UpdateCatalog.Repository}/releases/download/v0.1.1-beta.10/";
            byte[] body=request.RequestUri.Host=="api.github.com" ? JsonSerializer.SerializeToUtf8Bytes(new[]{new{tag_name="v0.1.1-beta.10",draft=false,prerelease=true,body="A verified release summary",assets=new[]{
                new{name,size=payload.Length,browser_download_url=prefix+name,digest="sha256:"+hash},
                new{name="SHA256SUMS.txt",size=Encoding.UTF8.GetByteCount(sums),browser_download_url=prefix+"SHA256SUMS.txt",digest=(string)null}}}}) :
                request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt",StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(sums) : payload;
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(body)};
        }
    }
    internal static async Task Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"DanmuUpdateService-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var paths=new ApplicationPaths(Path.Combine(root,"app"),"unit",Path.Combine(root,"data"));
            var handler=new Handler();using var service=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true),new UpdateTransport(handler));
            DistributionTests.Check(handler.Requests==0,"Construction and isolated profiles must not contact GitHub.");
            var available=await service.CheckAsync();
            DistributionTests.Check(available.State=="available" && available.AvailableVersion=="0.1.1-beta.10" && available.CheckedAt.Length>0,"Check must publish real newer release state and freshness.");
            DistributionTests.Check(handler.Requests==1,"Checking must not silently download a release.");
            var ready=await service.DownloadAsync();
            DistributionTests.Check(ready.State=="ready" && ready.DownloadedBytes==ready.TotalBytes && service.Downloaded!=null,"Download must only become ready after checksum verification.");
            DistributionTests.Check(handler.Requests==3,"Download must fetch the checksum and exact edition artifact.");
            var cachedHandler=new Handler();using(var reloaded=new UpdateService(paths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true),new UpdateTransport(cachedHandler)))
            {
                var cached=await reloaded.CheckAsync();
                DistributionTests.Check(cached.State=="ready" && reloaded.Downloaded!=null && cachedHandler.Requests==2,"A restarted service must reuse complete cached bytes only after fresh GitHub checksum validation.");
            }
            using(var settings=JsonDocument.Parse("{\"autoCheck\":false,\"autoDownload\":false,\"channel\":\"stable\"}"))service.SetPreferences(settings.RootElement);
            DistributionTests.Check(service.View.AvailableVersion=="" && service.View.Channel=="stable","Channel change must revoke a previously selected beta update.");
            using(var bad=JsonDocument.Parse("{\"autoCheck\":true,\"autoDownload\":true,\"channel\":\"preview\"}"))
                UpdatePackageTests.Reject(()=>service.SetPreferences(bad.RootElement),"Unsupported channel must be rejected.");
            handler.Block=true;var pending=service.CheckAsync();
            DistributionTests.Check(service.View.State=="checking","Check must expose its busy state before I/O finishes.");
            bool duplicate=false;try{await service.CheckAsync();}catch(InvalidOperationException){duplicate=true;}
            DistributionTests.Check(duplicate,"Concurrent update checks must be deduplicated.");service.Cancel();await pending;
            DistributionTests.Check(service.View.State=="idle","Cancellation must not be represented as success.");
            var failedPaths=new ApplicationPaths(Path.Combine(root,"write-failure"),"unit",Path.Combine(root,"failed-data"));
            using var failed=new UpdateService(failedPaths,new UpdateIdentity("0.1.1-beta.9","standard","portable",true),new UpdateTransport(new Handler()));
            bool inject=true;failed.Changed+=()=>
            {
                if(!inject || failed.View.State!="verifying")return;inject=false;
                string operation=Directory.GetDirectories(Path.Combine(failedPaths.ProfileRoot,"updates"))[0];Directory.CreateDirectory(Path.Combine(operation,"download.json"));
            };
            await failed.CheckAsync();await failed.DownloadAsync();
            DistributionTests.Check(failed.View.State=="failed" && failed.Downloaded==null,"Download-record failure must not publish a deleted ready package.");
            await failed.DownloadAsync();DistributionTests.Check(failed.View.State=="ready" && failed.Downloaded!=null,"A download-record failure must allow a complete retry.");
        }
        finally
        {
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath())+"DanmuUpdateService-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture cleanup root.");
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: update state, persistence, opt-in download and operation serialization.");
    }
}
