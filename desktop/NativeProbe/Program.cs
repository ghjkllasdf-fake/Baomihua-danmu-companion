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
            if(args.Length!=0 || !OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
                Thread.CurrentThread.GetApartmentState()!=ApartmentState.MTA)throw new InvalidOperationException("Unsupported probe invocation.");
            ProbeGate.OpenAsync(Console.OpenStandardInput(),TimeSpan.FromSeconds(5),CancellationToken.None).GetAwaiter().GetResult();
            // Do not load projections or activate player APIs before the owned-parent gate.
            NativeRuntime.LoadMediaProjection(AppContext.BaseDirectory);
            ProbeWorkerRuntime.Run();
            return 0;
        }
        catch(Exception){Console.Error.WriteLine("Player probe failed.");return 2;}
    }
}
