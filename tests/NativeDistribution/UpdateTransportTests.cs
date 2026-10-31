using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class UpdateTransportTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage,HttpResponseMessage> Response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(Response(request));}
    }
    internal static async Task Run()
    {
        byte[] bytes="verified download"u8.ToArray();string hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var url=new Uri($"https://github.com/{UpdateCatalog.Repository}/releases/download/v0.1.1-beta.10/sample.zip");
        var handler=new Handler{Response=_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}};
        using var transport=new UpdateTransport(handler);
        string root=Path.Combine(Path.GetTempPath(),"DanmuUpdateTransport-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string file=Path.Combine(root,"download.zip");long progress=0;
            await transport.DownloadAsync(new UpdateAsset("sample.zip",url,bytes.Length,hash),file,hash,(done,total)=>{progress=done;DistributionTests.Check(total==bytes.Length,"Progress denominator must be real bytes.");},CancellationToken.None);
            DistributionTests.Check(progress==bytes.Length && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes),"Download must be verified before ready.");
            UpdatePackageTests.Reject(()=>transport.DownloadAsync(new UpdateAsset("sample.zip",url,bytes.Length,""),Path.Combine(root,"bad.zip"),new string('0',64),null,CancellationToken.None).GetAwaiter().GetResult(),"Tampered download must fail its full checksum.");
            DistributionTests.Check(!File.Exists(Path.Combine(root,"bad.zip")),"Bad download must not become a ready file.");
            handler.Response=_=>{var response=new HttpResponseMessage(HttpStatusCode.Redirect);response.Headers.Location=new Uri("http://127.0.0.1/private");return response;};
            UpdatePackageTests.Reject(()=>transport.ReadTextAsync(url,64,CancellationToken.None).GetAwaiter().GetResult(),"Redirect to private HTTP endpoint must be rejected.");
            handler.Response=_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[65])};
            UpdatePackageTests.Reject(()=>transport.ReadTextAsync(url,64,CancellationToken.None).GetAwaiter().GetResult(),"Metadata size limit must be enforced.");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
            try{await transport.ReadTextAsync(url,64,cancelled.Token);}catch(OperationCanceledException){stopped=true;}
            DistributionTests.Check(stopped,"Cancelled metadata request must stop.");
        }
        finally
        {
            if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath())+"DanmuUpdateTransport-",StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture cleanup root.");
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: update download integrity, limits, redirects and cancellation.");
    }
}
