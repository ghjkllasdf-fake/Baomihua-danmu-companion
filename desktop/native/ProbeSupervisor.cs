using System;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed class ProbeSupervisor : IDisposable
{
    private readonly object gate=new();
    private readonly Func<IProbeChild> create;
    private readonly TimeSpan startupBudget,frameBudget,retryDelay;
    private readonly CancellationTokenSource stop=new();
    private readonly Task loop;
    private ProbeAttempt current;
    private int stopping;
    private long generation;
    private PlayerSample latest=new(null,"",0,null,null,0,"等待播放器探测","unavailable");
    public PlayerSample Latest=>Volatile.Read(ref latest);
    // Subscribers enqueue their own work; they do not execute UI/player reads here.
    public event Action Updated;
    internal long Generation=>Interlocked.Read(ref generation);
    internal Task Completion=>loop;
    internal ProbeSupervisor(Func<IProbeChild> create,int startupMs=8000,int frameMs=1500,int retryMs=1000)
    {
        this.create=create??throw new ArgumentNullException(nameof(create));
        if (startupMs is <1 or >30000 || frameMs is <1 or >30000 || retryMs<1) throw new ArgumentOutOfRangeException(nameof(startupMs));
        startupBudget=TimeSpan.FromMilliseconds(startupMs); frameBudget=TimeSpan.FromMilliseconds(frameMs); retryDelay=TimeSpan.FromMilliseconds(retryMs);
        loop=Task.Run(RunAsync);
    }
    private void Unknown(string detail)
    {
        bool publish;
        lock (gate)
        {
            publish=Volatile.Read(ref stopping)==0;
            if(publish) Volatile.Write(ref latest,Latest with {Window=null,PositionMs=null,Playing=null,StampMs=Monotonic.Now,Source="unavailable",Detail=detail});
        }
        if(publish) Updated?.Invoke();
    }
    private static TimeSpan Remaining(double deadline)
    {
        double remaining=deadline-Monotonic.Now;
        if (remaining<=0) throw new TimeoutException();
        return TimeSpan.FromMilliseconds(remaining);
    }
    private async Task RunAsync()
    {
        var token=stop.Token;
        while (!token.IsCancellationRequested)
        {
            ProbeAttempt attempt=null; Task<byte[]> read=null; bool cleaned=true;
            try
            {
                double deadline=Monotonic.Now+startupBudget.TotalMilliseconds;
                attempt=new ProbeAttempt(create,deadline,()=>Volatile.Read(ref stopping)!=0);
                lock (gate)
                {
                    if (Volatile.Read(ref stopping)!=0) return;
                    current=attempt; generation++; attempt.Begin();
                }
                var child=await attempt.Bootstrap.WaitAsync(Remaining(deadline),token).ConfigureAwait(false);
                long sequence=0; bool first=true;
                while (!token.IsCancellationRequested)
                {
                    double frameDeadline=first ? deadline : Monotonic.Now+frameBudget.TotalMilliseconds;
                    read=Task.Run(()=>child.ReadAsync(Remaining(frameDeadline),token),token);
                    _=read.ContinueWith(static task=>{ _=task.Exception; },CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                    var bytes=await read.WaitAsync(Remaining(frameDeadline),token).ConfigureAwait(false);
                    var packet=ProbeProtocol.Decode(bytes,sequence,Monotonic.Now);
                    _=Remaining(frameDeadline);
                    lock (gate)
                    {
                        if (Volatile.Read(ref stopping)!=0 || !ReferenceEquals(current,attempt)) break;
                        Volatile.Write(ref latest,packet.Sample);
                    }
                    Updated?.Invoke();
                    sequence=packet.Sequence; first=false;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Unknown("播放器探测已中断，正在重试"); }
            finally
            {
                if (attempt?.Bootstrap!=null)
                {
                    try { cleaned=await attempt.CleanupAsync(read).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { cleaned=false; }
                    lock (gate) if (cleaned && ReferenceEquals(current,attempt)) current=null;
                }
            }
            if (!cleaned) { Unknown("播放器探测未能完成回收，请重新打开助手"); return; }
            try { await Task.Delay(retryDelay,token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping,1)!=0) return;
        var cancel=Task.Run(()=>{ Volatile.Read(ref current)?.RequestStop(); stop.Cancel(); });
        _=Task.WhenAll(loop,cancel).ContinueWith(static (task,state)=>{ if (task.IsFaulted) _=task.Exception; ((CancellationTokenSource)state).Dispose(); },stop,
            CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        try { loop.Wait(1250); } catch (AggregateException) { _=loop.Exception; }
    }
}
