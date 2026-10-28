using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class ProbeWorkerBoundaryTests
{
    public static async Task Run(string runtime,string script)
    {
        var child=new ProbeProcess(runtime,script);
        try
        {
            child.Start();
            var ready=await child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None);
            if (Encoding.ASCII.GetString(ready)!="gate-ready") throw new Exception("The actual worker bootstrap bypassed its gate read.");
            var pending=child.ReadAsync(TimeSpan.FromSeconds(8),CancellationToken.None);
            await Task.Delay(100);
            if (pending.IsCompleted) throw new Exception("The actual worker bootstrap continued before its owned gate was released.");
            child.Release();
            var packet=ProbeProtocol.Decode(await pending,0,Monotonic.Now);
            if (packet.Sample.Title!="worker-fixture" || packet.Sample.Detail!="MTA") throw new Exception("The actual bootstrap did not compile on MTA or produced invalid data.");
        }
        finally { if (!await child.StopAndReapAsync()) throw new Exception("Worker boundary fixture did not finish cleanup."); }
        Console.WriteLine("PASS: actual PS7 worker bootstrap and source compilation reach a synthetic MTA emitter only after the actual startup gate.");
    }
}
