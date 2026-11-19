using System;
using System.Threading;

namespace Popcorn;

internal static class NativeProbe
{
    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            bool toolbar=args is ["--toolbar"];
            if(!(args.Length==0 || toolbar) || !OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
                Thread.CurrentThread.GetApartmentState()!=ApartmentState.MTA)throw new InvalidOperationException("Unsupported probe invocation.");
            ProbeGate.OpenAsync(Console.OpenStandardInput(),TimeSpan.FromSeconds(5),CancellationToken.None).GetAwaiter().GetResult();
            // Do not load projections or activate player APIs before the owned-parent gate.
            if(toolbar){ToolbarProbeRuntime.Run();return 0;}
            NativeRuntime.LoadMediaProjection(AppContext.BaseDirectory);
            using var rates=new NativePlayerRateSource();
            ProbeWorkerRuntime.Run(null,rates.ReadPlayback);
            return 0;
        }
        catch(Exception error)
        {
            // Report code identity only: exception messages can contain player data.
            Console.Error.WriteLine("Player probe failed: "+error.GetType().Name+" @ "+error.TargetSite?.DeclaringType?.Name+"."+error.TargetSite?.Name);
            return 2;
        }
    }
}
