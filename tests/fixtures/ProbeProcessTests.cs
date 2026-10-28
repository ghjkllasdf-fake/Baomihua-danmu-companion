using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class ProbeProcessTests
{
    private static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    public static Task OpenGate(bool announce)
    {
        Stream input=Console.OpenStandardInput();
        if (announce) input=new GateReadyStream(input);
        return ProbeGate.OpenAsync(input,TimeSpan.FromSeconds(5),CancellationToken.None);
    }
    public static void Emit(string scenario,string assembly,string script)
    {
        if (scenario=="owner")
        {
            var child=new ProbeProcess(Environment.ProcessPath,script,assembly,"hang");
            child.Start(); child.Release();
            var ready=child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None).GetAwaiter().GetResult();
            ProbeProtocol.Decode(ready,0,Monotonic.Now);
            Console.WriteLine(child.Id+"|"+child.StartTimeTicks); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return;
        }
        using var output=Console.OpenStandardOutput();
        string title=Environment.GetEnvironmentVariable("DANMU_TEST_SECRET")==null && Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")==null ? "synthetic-clean" : "inherited-unexpected-env";
        var sample=new PlayerSample(new VideoWindow(new IntPtr(1),title,0,0,1280,720),title,1,1000,false,Monotonic.Now,"fixture","uia-text",200,60000);
        output.Write(ProbeProtocol.Encode(sample,1)); output.WriteByte(10); output.Flush();
        if (scenario=="malformed") { output.Write(new byte[] {123,255,125,10}); output.Flush(); }
        if (scenario=="stderr") { Console.Error.Write(new string('x',70000)); Console.Error.Flush(); }
        Thread.Sleep(Timeout.Infinite);
    }
    public static async Task Run(string runtime,string script,string assembly)
    {
        await ParentExit(runtime,script,assembly);
        foreach (string scenario in new[] {"hang","malformed","stderr"})
        {
            var child=new ProbeProcess(runtime,script,assembly,"gate-"+scenario);
            try
            {
                child.Start();
                var gate=await child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None);
                Check(Encoding.ASCII.GetString(gate)=="gate-ready","The child did not reach the actual gate read before sampling.");
                var pending=child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None);
                await Task.Delay(150);
                Check(!pending.IsCompleted,"The synthetic child ran before the parent released its startup gate.");
                child.Release();
                var packet=ProbeProtocol.Decode(await pending,0,Monotonic.Now);
                Check(packet.Sample.Title=="synthetic-clean","The child inherited credentials or WebView debugging settings.");
                if (scenario=="hang")
                {
                    try { await child.ReadAsync(TimeSpan.FromMilliseconds(100),CancellationToken.None); throw new Exception("A hung child did not time out."); }
                    catch (TimeoutException) { }
                }
                else if (scenario=="malformed")
                {
                    try { ProbeProtocol.Decode(await child.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None),1,Monotonic.Now); throw new Exception("Malformed child data was accepted."); }
                    catch (InvalidDataException) { }
                }
                else
                {
                    for (int i=0;i<100 && !child.ErrorDrainFailed;i++) await Task.Delay(10);
                    Check(child.ErrorDrainFailed,"stderr was buffered without a byte budget.");
                }
            }
            finally { Check(await child.StopAndReapAsync(),"The owned child or one of its pipe reads did not finish during bounded cleanup."); }
        }
        var stopped=new ProbeProcess(runtime,script,assembly,"hang"); stopped.Stop();
        try { stopped.Start(); throw new Exception("A stopped child was started."); } catch (OperationCanceledException) { }
        Check(await stopped.StopAndReapAsync(),"An unstarted child leaked resources.");
        var gated=new ProbeProcess(runtime,script,assembly,"hang");
        try
        {
            gated.Start(); gated.Stop();
            try { gated.Release(); throw new Exception("A stopped child passed its startup gate."); } catch (OperationCanceledException) { }
        }
        finally { Check(await gated.StopAndReapAsync(),"Stopping between launch and gate release leaked a child."); }
        Console.WriteLine("PASS: gated hidden CLI, sanitized environment, hang/malformed/stderr failure and bounded owned-child cleanup.");
    }
    public static async Task DownloadedScript(string runtime,string script,string assembly)
    {
        var child=new ProbeProcess(runtime,script,assembly,"gate-hang");
        try
        {
            child.Start();
            var ready=await child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None);
            Check(Encoding.ASCII.GetString(ready)=="gate-ready","Downloaded probe did not reach its startup gate.");
            child.Release();
            var packet=ProbeProtocol.Decode(await child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None),0,Monotonic.Now);
            Check(packet.Sample.Detail=="fixture" && packet.Sample.Source=="uia-text" && packet.Sample.Title=="synthetic-clean",
                "Downloaded probe did not return the synthetic frame.");
        }
        finally { Check(await child.StopAndReapAsync(),"Downloaded probe did not finish bounded cleanup."); }
    }
    private static async Task ParentExit(string runtime,string script,string assembly)
    {
        var info=new ProcessStartInfo(runtime) {UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach (string value in new[] {"-NoLogo","-NoProfile","-NonInteractive","-MTA","-File",script,assembly,"owner"}) info.ArgumentList.Add(value);
        using var owner=new Process {StartInfo=info}; Process child=null; bool launched=false;
        try
        {
            Check(owner.Start(),"Synthetic owner did not start."); launched=true;
            var stderr=owner.StandardError.BaseStream.CopyToAsync(Stream.Null);
            owner.StandardInput.Write("start\n"); owner.StandardInput.Flush();
            string identity=await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var parts=identity?.Split('|');
            Check(parts is {Length:2} && int.TryParse(parts[0],out _) && long.TryParse(parts[1],out _),"The controlled parent did not report its initialized child's identity.");
            int pid=int.Parse(parts[0]); long created=long.Parse(parts[1]);
            var candidate=Process.GetProcessById(pid);
            try
            {
                _=candidate.Handle;
                Check(candidate.StartTime.ToUniversalTime().Ticks==created,"The reported PID was reused; the candidate is not owned by this test.");
                child=candidate;
            }
            catch { candidate.Dispose(); throw; }
            Check(!child.HasExited && !owner.HasExited,"The owned-process death test was not live at its precondition.");
            owner.Kill(); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await stderr.WaitAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine("PASS: terminating only the held synthetic parent also ends its held Job-owned child.");
        }
        finally
        {
            if (launched && !owner.HasExited) { owner.Kill(); await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            if (child!=null) { using (child) if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } }
        }
    }
}

internal sealed class GateReadyStream(Stream input) : Stream
{
    private int announced;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken stop=default)
    {
        if (Interlocked.Exchange(ref announced,1)==0)
        {
            var output=Console.OpenStandardOutput(); output.Write("gate-ready\n"u8); output.Flush();
        }
        return input.ReadAsync(buffer,stop);
    }
    public override bool CanRead=>true;
    public override bool CanSeek=>false;
    public override bool CanWrite=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    public override void Flush() { }
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
}
