using System;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed class ProbeRestartException : Exception
{
    public ProbeRestartException():base("The probe operation outcome is unconfirmed; restart the owned worker.") { }
}
internal static class ProbeOperation
{
    public static Task Complete(Func<Task> start,int timeoutMs,CancellationToken stop)
    {
        Task operation=null;
        try
        {
            operation=start();
            operation.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs),stop).GetAwaiter().GetResult();
            return operation;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (operation==null || !operation.IsCompleted || ex is TimeoutException) throw new ProbeRestartException();
            throw;
        }
    }
}
