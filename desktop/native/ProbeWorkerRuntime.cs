using System;
using System.Threading;

namespace Popcorn;

public static class ProbeWorkerRuntime
{
    public static void Run()
    {
        if (Thread.CurrentThread.GetApartmentState()!=ApartmentState.MTA) throw new InvalidOperationException("The player sampler requires MTA.");
        using var sampler=new PlayerSampler(CancellationToken.None);
        using var output=Console.OpenStandardOutput(); long sequence=0;
        while (true)
        {
            double started=Monotonic.Now;
            var bytes=ProbeProtocol.Encode(sampler.Poll(),checked(++sequence));
            output.Write(bytes); output.WriteByte(10); output.Flush();
            sampler.WaitForNext(ProbeCadence.DelayMilliseconds(started,Monotonic.Now));
        }
    }
}
