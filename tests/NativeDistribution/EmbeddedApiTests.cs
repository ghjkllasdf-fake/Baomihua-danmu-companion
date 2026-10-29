using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class EmbeddedApiTests
{
    private static void Check(bool value,string message)=>DistributionTests.Check(value,message);
    internal static async Task Run()
    {
        string directory=Path.Combine(Path.GetTempPath(),"danmu-api-tests",Guid.NewGuid().ToString("N"));
        try
        {
        var paths=new ApplicationPaths(AppContext.BaseDirectory,"isolated",directory);
        string broken=Path.Combine(directory,"broken-payload");Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken,"manifest.json"),"{\"variant\":\"includeAPI\"}");
        Check(EmbeddedApiService.IsPackaged(new ApplicationPaths(broken,"isolated",directory)),"Missing API files in an includeAPI manifest must not silently switch to saved external credentials.");
        string file=Path.Combine(paths.ProfileRoot,"embedded-api","sources.json");
        var defaults=EmbeddedApiSources.Load(file);
        Check(defaults.Sources.SequenceEqual(new[]{"douban","360","renren","hanjutv"}),"Source defaults must preserve the upstream four.");
        foreach(var invalid in new[]{Array.Empty<string>(),new[]{"douban","douban"},new[]{"custom"},new[]{"../360"}})
        {
            bool rejected=false;try{EmbeddedApiSources.Validate(invalid);}catch(ArgumentException){rejected=true;}
            Check(rejected,"Invalid embedded source selection accepted.");
        }
        EmbeddedApiSources.Save(file,new[]{"bilibili","360"});
        Check(EmbeddedApiSources.Load(file).Sources.SequenceEqual(new[]{"bilibili","360"}),"Source order must survive restart.");
        File.WriteAllText(file,"{broken");
        Check(EmbeddedApiSources.Load(file).Notice.Length>0 && File.ReadAllText(file)=="{broken","Malformed preferences must fall back with notice, not silently rewrite.");
        var modes=new[]{"ready","wrong-owner","overflow","stderr","exit","stall"};
        foreach(string mode in modes)
        {
            using var service=new EmbeddedApiService(paths,()=>Fixture(mode),TimeSpan.FromSeconds(mode=="ready" ? 12 : 2));
            await service.StartAsync(defaults.Sources);
            if(mode=="ready")
            {
                Check(service.View.State=="ready" && service.ApiBase.StartsWith("http://127.0.0.1:"),"Owned child must pass readiness and authenticated health: "+service.FailureKind);
                int first=service.ProcessId;
                await service.StartAsync(new[]{"bilibili"});
                Check(service.ProcessId!=first && !Alive(first) && service.View.Sources.SequenceEqual(new[]{"bilibili"}),"Explicit source change must replace and reap only its owned child.");
                int second=service.ProcessId;service.Dispose();
                Check(!Alive(second),"Disposal must reap the owned API process.");
            }
            else Check(service.View.State=="failed" && service.ApiBase=="" && service.ProcessId==0,"Invalid/startup-deadline child must fail closed and be reaped: "+mode);
            Check(!JsonSerializer.Serialize(service.View).Contains("owner") && !JsonSerializer.Serialize(service.View).Contains("127.0.0.1"),"UI status must not reveal address or owner token.");
        }
        using(var service=new EmbeddedApiService(paths,()=>Fixture("stall"),TimeSpan.FromSeconds(10)))
        {
            var start=service.StartAsync(defaults.Sources);await Task.Delay(150);int id=service.ProcessId;service.Dispose();
            await start.WaitAsync(TimeSpan.FromSeconds(3));Check(!Alive(id),"Close during startup must terminate the child without waiting the full startup deadline.");
        }
        Console.WriteLine("PASS: embedded source validation, gated owned lifecycle, authenticated health, replacement, failure bounds and shutdown.");
        }
        finally
        {
            string parent=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"danmu-api-tests"))+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(directory).StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unsafe fixture cleanup path.");
            if(Directory.Exists(directory))Directory.Delete(directory,true);
        }
    }
    private static bool Alive(int id){if(id<=0)return false;try{using var p=Process.GetProcessById(id);return !p.HasExited;}catch(ArgumentException){return false;}}
    private static ProcessStartInfo Fixture(string mode)
    {
        var start=new ProcessStartInfo(Environment.ProcessPath);start.ArgumentList.Add("--api-fixture");start.ArgumentList.Add(mode);
        start.Environment["NODE_OPTIONS"]="--require=untrusted.cjs";start.Environment["HTTP_PROXY"]="http://fixture.invalid:1";return start;
    }
    internal static async Task<int> Emit(string mode)
    {
        string line=await Console.In.ReadLineAsync();if(line==null)return 0;
        using var config=JsonDocument.Parse(line);string token=config.RootElement.GetProperty("token").GetString();
        if(Environment.GetEnvironmentVariable("NODE_OPTIONS")!=null || Environment.GetEnvironmentVariable("HTTP_PROXY")!=null)return 3;
        if(mode=="exit")return 2;
        if(mode=="overflow"){Console.WriteLine(new string('x',5000));await Console.Out.FlushAsync();await Console.In.ReadToEndAsync();return 0;}
        if(mode=="stderr"){Console.Error.Write(new string('e',70000));await Console.Error.FlushAsync();await Console.In.ReadToEndAsync();return 0;}
        if(mode=="stall"){await Console.In.ReadToEndAsync();return 0;}
        using var server=new TcpListener(IPAddress.Loopback,0);server.Start();int port=((IPEndPoint)server.LocalEndpoint).Port;
        Console.WriteLine(JsonSerializer.Serialize(new{protocol=1,port,owner=mode=="wrong-owner" ? "foreign" : token}));await Console.Out.FlushAsync();
        var eof=Task.Run(()=>Console.In.ReadToEnd());
        while(true)
        {
            var accept=server.AcceptTcpClientAsync();if(await Task.WhenAny(accept,eof)==eof)return 0;
            using var connection=await accept;using var stream=connection.GetStream();
            using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);string request=await reader.ReadLineAsync();
            while(!string.IsNullOrEmpty(await reader.ReadLineAsync())){}
            string body=JsonSerializer.Serialize(new{success=true,owner=token,upstreamCommit=EmbeddedApiService.UpstreamCommit});
            string response=$"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
        }
    }
}
