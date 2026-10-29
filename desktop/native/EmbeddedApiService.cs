using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed record EmbeddedApiView(bool Enabled,string State,string Message,string[] Sources,EmbeddedApiSource[] AvailableSources);

internal sealed class EmbeddedApiService : IDisposable
{
    internal const string UpstreamCommit="4d325e096bbbf0c1ff261a9bfa638f7c58784f60";
    private sealed class Child
    {
        internal Process Process;internal ProbeJob Job;internal string Token;
    }
    private readonly object gate=new();
    private readonly SemaphoreSlim restarts=new(1,1);
    private readonly CancellationTokenSource lifetime=new();
    private readonly ApplicationPaths paths;
    private readonly Func<ProcessStartInfo> launch;
    private readonly TimeSpan startupTimeout;
    private Child child;
    private bool disposed;
    private string apiBase="";
    private EmbeddedApiView view=new(true,"starting","本机弹幕服务正在启动。",EmbeddedApiSources.Defaults,EmbeddedApiSources.Available);
    internal event Action Changed;
    internal string FailureKind {get;private set;}="";
    internal static bool IsPackaged(ApplicationPaths paths)
    {
        if(Directory.Exists(Path.Combine(paths.Root,"api")))return true;
        string manifest=Path.Combine(paths.Root,"manifest.json");
        try
        {
            if(!File.Exists(manifest) || new FileInfo(manifest).Length>4*1024*1024)return false;
            using var doc=JsonDocument.Parse(File.ReadAllText(manifest));
            return doc.RootElement.TryGetProperty("variant",out var variant) && variant.GetString()=="includeAPI";
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){return false;}
    }
    internal EmbeddedApiView View {get {lock(gate)return view with{Sources=(string[])view.Sources.Clone()};}}
    internal string ApiBase {get {lock(gate)return apiBase;}}
    internal int ProcessId {get {lock(gate)return child?.Process.Id??0;}}
    internal EmbeddedApiService(ApplicationPaths paths,Func<ProcessStartInfo> launch=null,TimeSpan? startupTimeout=null)
    {this.paths=paths;this.launch=launch??ProductionStart;this.startupTimeout=startupTimeout??TimeSpan.FromSeconds(30);}
    private ProcessStartInfo ProductionStart()
    {
        string directory=Path.Combine(paths.Root,"api"),metadata=Path.Combine(directory,"runtime.json");
        if(new FileInfo(metadata).Length>65536)throw new InvalidDataException();
        using var doc=JsonDocument.Parse(File.ReadAllText(metadata));var value=doc.RootElement;
        if(value.GetProperty("schemaVersion").GetInt32()!=1 || value.GetProperty("upstreamCommit").GetString()!=UpstreamCommit)throw new InvalidDataException();
        foreach(string relative in new[]{"node.exe","entry.mjs","upstream/danmu_api/worker.js"})
            if(!File.Exists(Path.Combine(directory,relative)))throw new FileNotFoundException();
        var start=new ProcessStartInfo(Path.Combine(directory,"node.exe"));start.ArgumentList.Add(Path.Combine(directory,"entry.mjs"));return start;
    }
    private ProcessStartInfo PrepareStart()
    {
        var start=launch();start.UseShellExecute=false;start.CreateNoWindow=true;start.WindowStyle=ProcessWindowStyle.Hidden;
        start.RedirectStandardInput=true;start.RedirectStandardOutput=true;start.RedirectStandardError=true;
        start.StandardOutputEncoding=new UTF8Encoding(false,true);start.StandardErrorEncoding=new UTF8Encoding(false,true);
        start.WorkingDirectory=Path.Combine(paths.ProfileRoot,"embedded-api");Directory.CreateDirectory(start.WorkingDirectory);
        start.Environment.Clear();
        foreach(string key in new[]{"SystemRoot","WINDIR","TEMP","TMP","USERPROFILE","APPDATA","LOCALAPPDATA","ProgramData"})
        {string value=Environment.GetEnvironmentVariable(key);if(!string.IsNullOrEmpty(value))start.Environment[key]=value;}
        start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);
        start.Environment["NODE_ENV"]="production";return start;
    }
    internal async Task StartAsync(string[] sources)
    {
        sources=EmbeddedApiSources.Validate(sources);
        try{await restarts.WaitAsync(lifetime.Token).ConfigureAwait(false);}catch(OperationCanceledException){return;}
        Child current=null;
        try
        {
            StopCurrent();
            lock(gate){if(disposed)return;view=new(true,"starting","本机弹幕服务正在启动。",sources,EmbeddedApiSources.Available);apiBase="";}
            Changed?.Invoke();
            var start=PrepareStart();current=new Child{Process=new Process{StartInfo=start},Job=new ProbeJob(),Token=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()};
            lock(gate)
            {
                if(disposed){current.Job.Dispose();current.Process.Dispose();return;}
                child=current;if(!current.Process.Start())throw new InvalidOperationException();
                // The child cannot import upstream until this parent-owned Job is assigned.
                current.Job.Assign(current.Process);
            }
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);timeout.CancelAfter(startupTimeout);
            _=DrainStderr(current);
            await current.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{protocol=1,token=current.Token,sources}).AsMemory(),timeout.Token).ConfigureAwait(false);
            await current.Process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            string line=await ReadReady(current.Process.StandardOutput,timeout.Token).ConfigureAwait(false);
            using var ready=JsonDocument.Parse(line);var data=ready.RootElement;
            if(data.ValueKind!=JsonValueKind.Object || data.EnumerateObject().Count()!=3 || data.GetProperty("protocol").GetInt32()!=1 ||
                data.GetProperty("owner").GetString()!=current.Token || !data.GetProperty("port").TryGetInt32(out int port) || port is <1 or >65535)throw new InvalidDataException();
            string address=$"http://127.0.0.1:{port}/{current.Token}";
            using var handler=new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false};using var client=new HttpClient(handler);
            using var response=await client.GetAsync(address+"/health",HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();await response.Content.LoadIntoBufferAsync(4096,timeout.Token).ConfigureAwait(false);
            using var health=JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));var h=health.RootElement;
            if(!h.GetProperty("success").GetBoolean() || h.GetProperty("owner").GetString()!=current.Token || h.GetProperty("upstreamCommit").GetString()!=UpstreamCommit)throw new InvalidDataException();
            lock(gate)
            {
                if(disposed || child!=current || current.Process.HasExited)throw new OperationCanceledException();
                apiBase=address;view=view with{State="ready",Message="本机弹幕服务已就绪，仅允许本机连接。"};
            }
            Changed?.Invoke();_=WatchExit(current);_=WatchStdout(current);
        }
        catch(Exception error)
        {
            FailureKind=error.GetType().Name;
            if(current!=null)Fail(current);else {lock(gate){apiBase="";view=view with{State="failed",Message=FailureMessage};}Changed?.Invoke();}
        }
        finally{restarts.Release();}
    }
    private const string FailureMessage="本机弹幕服务未能启动或已退出。请在弹幕源设置中重新应用以重试；仍失败请完整解压或重新安装 includeAPI 版本。";
    private static async Task<string> ReadReady(StreamReader reader,CancellationToken stop)
    {
        var buffer=new byte[4096];var one=new byte[1];int length=0;
        while(true)
        {
            int count=await reader.BaseStream.ReadAsync(one.AsMemory(),stop).ConfigureAwait(false);if(count==0)throw new EndOfStreamException();
            if(one[0]==10)return new UTF8Encoding(false,true).GetString(buffer,0,length).TrimEnd('\r');
            if(length>=buffer.Length)throw new InvalidDataException();buffer[length++]=one[0];
        }
    }
    private async Task DrainStderr(Child current)
    {
        try
        {
            var buffer=new byte[2048];int total=0,count;
            while((count=await current.Process.StandardError.BaseStream.ReadAsync(buffer.AsMemory(),lifetime.Token).ConfigureAwait(false))>0)
                if((total+=count)>65536){Fail(current);return;}
        }
        catch(Exception){if(!lifetime.IsCancellationRequested)Fail(current);}
    }
    private async Task WatchStdout(Child current)
    {
        try{var buffer=new char[1];await current.Process.StandardOutput.ReadAsync(buffer.AsMemory(),lifetime.Token).ConfigureAwait(false);Fail(current);}
        catch(Exception){if(!lifetime.IsCancellationRequested)Fail(current);}
    }
    private async Task WatchExit(Child current)
    {
        try{await current.Process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);Fail(current);}
        catch(Exception){if(!lifetime.IsCancellationRequested)Fail(current);}
    }
    private void Fail(Child current)
    {
        lock(gate)
        {
            if(disposed || child!=current)return;
            child=null;apiBase="";view=view with{State="failed",Message=FailureMessage};
        }
        Stop(current);Changed?.Invoke();
    }
    private static void Stop(Child current)
    {
        if(current==null)return;
        try{current.Process.StandardInput.Close();if(!current.Process.WaitForExit(700)){current.Job.Dispose();if(!current.Process.WaitForExit(1500)){current.Process.Kill(true);current.Process.WaitForExit(1500);}}}
        catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        finally{current.Job.Dispose();current.Process.Dispose();}
    }
    private void StopCurrent(){Child old;lock(gate){old=child;child=null;apiBase="";}Stop(old);}
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;}lifetime.Cancel();StopCurrent();}
}
