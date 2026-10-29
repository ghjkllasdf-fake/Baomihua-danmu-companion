using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class NativeChildTests
{
    internal static int Emit(string scenario)
    {
        bool announce=scenario.StartsWith("gate-",StringComparison.Ordinal);
        ProbeProcessTests.OpenGate(announce).GetAwaiter().GetResult();
        if(announce)scenario=scenario[5..];
        if(scenario=="owner")
        {
            var child=new ProbeProcess(Environment.ProcessPath,"--fixture","hang");
            child.Start();child.Release();
            ProbeProtocol.Decode(child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None).GetAwaiter().GetResult(),0,Monotonic.Now);
            Console.WriteLine(child.Id+"|"+child.StartTimeTicks);Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);return 0;
        }
        ProbeProcessTests.Emit(scenario,"","--fixture");
        return 0;
    }
    internal static async Task Run()
    {
        string previous=Environment.GetEnvironmentVariable("DANMU_TEST_SECRET");
        Environment.SetEnvironmentVariable("DANMU_TEST_SECRET","do-not-inherit-fixture-marker");
        try
        {
            await ParentExit();
            foreach(string scenario in new[]{"hang","malformed","stderr"})
            {
                var child=new ProbeProcess(Environment.ProcessPath,"--fixture","gate-"+scenario);
                try
                {
                    child.Start();
                    var gate=await child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None);
                    DistributionTests.Check(Encoding.ASCII.GetString(gate)=="gate-ready","Native fixture must reach its actual gate before sampling.");
                    var pending=child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None);
                    await Task.Delay(150);
                    DistributionTests.Check(!pending.IsCompleted,"Native worker must not sample before parent admission.");
                    child.Release();
                    var frame=ProbeProtocol.Decode(await pending,0,Monotonic.Now);
                    DistributionTests.Check(frame.Sample.Title=="synthetic-clean","Native child must not inherit API secrets or browser debug arguments.");
                    if(scenario=="hang")
                    {
                        try{await child.ReadAsync(TimeSpan.FromMilliseconds(100),CancellationToken.None);throw new Exception("Hung native child did not time out.");}
                        catch(TimeoutException){}
                    }
                    else if(scenario=="malformed")
                    {
                        try{ProbeProtocol.Decode(await child.ReadAsync(TimeSpan.FromSeconds(1),CancellationToken.None),1,Monotonic.Now);throw new Exception("Malformed native frame was accepted.");}
                        catch(InvalidDataException){}
                    }
                    else
                    {
                        for(int i=0;i<100 && !child.ErrorDrainFailed;i++)await Task.Delay(10);
                        DistributionTests.Check(child.ErrorDrainFailed,"Native stderr must retain its byte budget.");
                    }
                }
                finally{DistributionTests.Check(await child.StopAndReapAsync(),"Native child and reads must finish bounded cleanup.");}
            }
            var stopped=new ProbeProcess(Environment.ProcessPath,"--fixture","hang");stopped.Stop();
            try{stopped.Start();throw new Exception("Stopped native child restarted.");}catch(OperationCanceledException){}
            DistributionTests.Check(await stopped.StopAndReapAsync(),"Unstarted native child must release resources.");
            var gated=new ProbeProcess(Environment.ProcessPath,"--fixture","hang");
            try
            {
                gated.Start();gated.Stop();
                try{gated.Release();throw new Exception("Stopped native child passed admission.");}catch(OperationCanceledException){}
            }
            finally{DistributionTests.Check(await gated.StopAndReapAsync(),"Native stop between launch and admission must reap the child.");}
        }
        finally{Environment.SetEnvironmentVariable("DANMU_TEST_SECRET",previous);}
        Console.WriteLine("PASS: native EXE gate, sanitized environment, deadlines, malformed/stderr rejection and Job cleanup.");
    }
    private static async Task ParentExit()
    {
        var info=new ProcessStartInfo(Environment.ProcessPath){UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        info.ArgumentList.Add("--fixture");info.ArgumentList.Add("owner");
        using var owner=new Process{StartInfo=info};Process child=null;bool started=false;
        try
        {
            DistributionTests.Check(owner.Start(),"Native owner must start.");started=true;
            var stderr=owner.StandardError.BaseStream.CopyToAsync(Stream.Null);
            owner.StandardInput.Write("start\n");owner.StandardInput.Flush();
            var identity=(await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))?.Split('|');
            DistributionTests.Check(identity is {Length:2},"Native owner must report its held child identity.");
            child=Process.GetProcessById(int.Parse(identity[0]));_=child.Handle;
            DistributionTests.Check(child.StartTime.ToUniversalTime().Ticks==long.Parse(identity[1]) && !child.HasExited && !owner.HasExited,
                "Parent-death test must hold live matching process identities.");
            owner.Kill();await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            await stderr.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            if(started && !owner.HasExited){owner.Kill();await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}
            if(child!=null){using(child)if(!child.HasExited){child.Kill();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}}
        }
    }
}
