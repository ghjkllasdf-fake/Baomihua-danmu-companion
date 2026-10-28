using System;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed class ProbeAttempt
{
    private readonly object gate=new();
    private readonly Func<IProbeChild> create;
    private readonly Func<bool> parentStopped;
    private readonly double deadline;
    private IProbeChild child;
    private bool stopping;
    private Task stopTask;
    public Task<IProbeChild> Bootstrap { get; private set; }
    public ProbeAttempt(Func<IProbeChild> create,double deadline,Func<bool> parentStopped)
    { this.create=create; this.deadline=deadline; this.parentStopped=parentStopped; }
    public void Begin()=>Bootstrap=Task.Run(Start);
    private void QueueStop()
    {
        var owned=child;
        if (owned!=null) stopTask??=Task.Run(owned.Stop);
    }
    public void RequestStop()
    {
        lock (gate) { stopping=true; QueueStop(); }
    }
    private void CheckAdmission()
    {
        if (stopping || parentStopped()) throw new OperationCanceledException();
        if (Monotonic.Now>=deadline) throw new TimeoutException();
    }
    private IProbeChild Start()
    {
        lock (gate) CheckAdmission();
        var owned=create()??throw new InvalidOperationException("The probe factory returned no child.");
        lock (gate)
        {
            child=owned;
            if (stopping || parentStopped()) { stopping=true; QueueStop(); throw new OperationCanceledException(); }
            CheckAdmission();
        }
        owned.Start();
        lock (gate) CheckAdmission();
        // Never hold the ownership lock across a pipe write. An already-admitted
        // release may race Stop; its Job remains owned and publication is fenced.
        owned.Release();
        return owned;
    }
    public async Task<bool> CleanupAsync(Task pendingRead=null)
    {
        RequestStop();
        try { await Bootstrap.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        IProbeChild owned; Task stopped;
        lock (gate) { QueueStop(); owned=child; stopped=stopTask; }
        if (owned==null) return true;
        try
        {
            await stopped.ConfigureAwait(false);
            if (pendingRead!=null)
            {
                try { await pendingRead.ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            return await owned.StopAndReapAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
}
