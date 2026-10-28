using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal interface IProbeChild
{
    void Start();
    void Release();
    Task<byte[]> ReadAsync(TimeSpan timeout,CancellationToken stop);
    void Stop();
    Task<bool> StopAndReapAsync();
}

internal sealed class ProbeProcess : IProbeChild
{
    private readonly Process process;
    private readonly ProbeJob job;
    private readonly CancellationTokenSource drainStop=new();
    private Task drain=Task.CompletedTask;
    private ProbeFrameReader reader;
    private int started,stopped,reaped,launchState,stopFinished;
    private readonly object reapGate=new();
    private Task<bool> reaping;
    public bool ErrorDrainFailed=>drain.IsFaulted;
    public int Id=>process.Id;
    public long StartTimeTicks=>process.StartTime.ToUniversalTime().Ticks;
    public bool Reaped=>Volatile.Read(ref reaped)!=0;
    public ProbeProcess(string runtime,string script,params string[] arguments)
    {
        runtime=Path.GetFullPath(runtime); script=Path.GetFullPath(script);
        if (!File.Exists(runtime) || !File.Exists(script)) throw new FileNotFoundException("Probe runtime or entry point is missing.");
        var info=new ProcessStartInfo(runtime) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),WorkingDirectory=Path.GetDirectoryName(script)};
        foreach (string value in new[] {"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-MTA","-File",script}) info.ArgumentList.Add(value);
        foreach (string value in arguments) info.ArgumentList.Add(value);
        info.Environment.Clear();
        foreach (string name in new[] {"SystemRoot","WINDIR","TEMP","TMP","USERPROFILE","LOCALAPPDATA","APPDATA","ProgramFiles","ProgramW6432"})
        {
            string value=Environment.GetEnvironmentVariable(name); if (value!=null) info.Environment[name]=value;
        }
        string windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        info.Environment["PATH"]=Path.Combine(windows,"System32");
        process=new Process {StartInfo=info}; job=new ProbeJob();
    }
    public void Start()
    {
        if (Interlocked.CompareExchange(ref launchState,1,0)!=0) throw new InvalidOperationException("The probe launch was already attempted.");
        try
        {
            if (Volatile.Read(ref stopped)!=0) throw new OperationCanceledException();
            if (!process.Start()) throw new IOException("Probe process did not start.");
            Volatile.Write(ref started,1);
            if (Volatile.Read(ref stopped)!=0) throw new OperationCanceledException();
            job.Assign(process);
            if (Volatile.Read(ref stopped)!=0) throw new OperationCanceledException();
            reader=new ProbeFrameReader(process.StandardOutput.BaseStream);
            drain=DrainAsync(process.StandardError.BaseStream);
        }
        catch { Stop(); KillOwned(); throw; }
        finally { Volatile.Write(ref launchState,2); }
    }
    public void Release()
    {
        if (Volatile.Read(ref stopped)!=0) throw new OperationCanceledException();
        process.StandardInput.Write("start\n"); process.StandardInput.Flush();
    }
    public async Task<byte[]> ReadAsync(TimeSpan timeout,CancellationToken stop)
    {
        if (ErrorDrainFailed) throw new IOException("Probe stderr budget failed.");
        var bytes=await reader.ReadAsync(timeout,stop).ConfigureAwait(false);
        if (ErrorDrainFailed) throw new IOException("Probe stderr budget failed.");
        return bytes;
    }
    private async Task DrainAsync(Stream error)
    {
        var bytes=new byte[4096]; int total=0;
        try
        {
            while (true)
            {
                int count=await error.ReadAsync(bytes.AsMemory(),drainStop.Token).ConfigureAwait(false);
                if (count==0) return;
                total+=count; if (total>65536) throw new InvalidDataException("Probe stderr budget exceeded.");
            }
        }
        catch (OperationCanceledException) when (drainStop.IsCancellationRequested) { }
        catch (IOException) when (Volatile.Read(ref stopped)!=0) { }
    }
    private void KillOwned()
    {
        if (Volatile.Read(ref started)==0) return;
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }
    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped,1)!=0) return;
        try { drainStop.Cancel(); }
        finally
        {
            try { job.Dispose(); KillOwned(); }
            finally { Volatile.Write(ref stopFinished,1); }
        }
    }
    public Task<bool> StopAndReapAsync()
    {
        lock (reapGate) return reaping??=ReapAsync();
    }
    private async Task<bool> ReapAsync()
    {
        if (Volatile.Read(ref reaped)!=0) return true;
        Stop(); var watch=Stopwatch.StartNew();
        while (true)
        {
            if (Volatile.Read(ref launchState)!=1 && Volatile.Read(ref stopFinished)!=0 &&
                (Volatile.Read(ref started)==0 || process.HasExited) && drain.IsCompleted && (reader?.IsQuiescent??true))
            {
                if (drain.IsFaulted) _=drain.Exception;
                process.Dispose(); drainStop.Dispose(); Volatile.Write(ref reaped,1); return true;
            }
            if (watch.ElapsedMilliseconds>=1000) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}
