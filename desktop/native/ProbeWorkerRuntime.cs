using System;
using System.Threading;

namespace Popcorn;

public static class ProbeWorkerRuntime
{
    public static void Run()=>Run(null);
    public static void Run(Func<VideoWindow,double?> readRate,Func<VideoWindow,PlayerPlaybackReading> readPlayback=null)
    {
        if (Thread.CurrentThread.GetApartmentState()!=ApartmentState.MTA) throw new InvalidOperationException("The player sampler requires MTA.");
        using var sampler=new PlayerSampler(CancellationToken.None,readRate:readRate,readPlayback:readPlayback);
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
