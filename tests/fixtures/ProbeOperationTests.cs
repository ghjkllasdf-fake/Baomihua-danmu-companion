using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class ProbeOperationTests
{
    private static void Restart(Action action)
    {
        try { action(); } catch (ProbeRestartException ex) { if (ex.Message.Contains("credential-marker") || ex.InnerException!=null) throw new Exception("A restart exposed the raw operation failure."); return; }
        throw new Exception("An unconfirmed operation was allowed to continue in the same worker.");
    }
    public static void Run()
    {
        var hanging=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Restart(()=>ProbeOperation.Complete(()=>hanging.Task,50,CancellationToken.None));
        if (hanging.Task.IsCompleted) throw new Exception("A wait timeout was mistaken for cancellation of the underlying operation.");
        hanging.SetResult();
        Restart(()=>ProbeOperation.Complete(()=>throw new IOException("credential-marker"),50,CancellationToken.None));
        Restart(()=>ProbeOperation.Complete(()=>null,50,CancellationToken.None));
        var known=Task.FromException(new IOException("known failure"));
        try { ProbeOperation.Complete(()=>known,50,CancellationToken.None); throw new Exception("A known failed operation was reported as success."); }
        catch (IOException) { }
        var success=Task.CompletedTask;
        if (!ReferenceEquals(success,ProbeOperation.Complete(()=>success,50,CancellationToken.None))) throw new Exception("A confirmed completion did not retain its task.");
        using var stop=new CancellationTokenSource(); stop.Cancel();
        var pending=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Restart(()=>ProbeOperation.Complete(()=>pending.Task,50,stop.Token)); pending.SetResult();
        Console.WriteLine("PASS: unfinished timeout/cancellation/creation failure requires worker restart; confirmed failure remains a safe fallback.");
    }
}
