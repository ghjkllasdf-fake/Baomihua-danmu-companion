using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

public static class ProbeSupervisorTests
{
    private static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    private static async Task<bool> WaitForBarrier(ManualResetEventSlim signal,int milliseconds)
    {
        var watch=Stopwatch.StartNew();
        while(!signal.IsSet)
        {
            if(watch.ElapsedMilliseconds>=milliseconds)return false;
            await Task.Delay(1).ConfigureAwait(false);
        }
        return true;
    }
    private static async Task BarrierCoordination()
    {
        using var signal=new ManualResetEventSlim();
        var scheduler=new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default,1);
        var result=Task.Factory.StartNew(async ()=>{
            var setter=Task.Factory.StartNew(()=>signal.Set(),CancellationToken.None,TaskCreationOptions.None,scheduler.ExclusiveScheduler);
            bool entered=await WaitForBarrier(signal,2000);
            await setter;
            return entered;
        },CancellationToken.None,TaskCreationOptions.None,scheduler.ExclusiveScheduler).Unwrap();
        bool ready=await result.WaitAsync(TimeSpan.FromSeconds(3));
        scheduler.Complete();await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(ready,"Test barrier waits must yield so the scheduled signal can execute on constrained schedulers.");
    }
    private static async Task Until(Func<bool> ready,int milliseconds=5000)
    {
        var watch=Stopwatch.StartNew();
        while (!ready()) { if (watch.ElapsedMilliseconds>milliseconds) throw new Exception("Supervisor condition timed out."); await Task.Delay(10); }
    }
    private static byte[] Packet()=>ProbeProtocol.Encode(new PlayerSample(new VideoWindow(new IntPtr(1),"fixture",0,0,1280,720),"fixture",1,1000,false,Monotonic.Now,"fixture","uia-text",200,60000),1);
    private static async Task StartupBudgets()
    {
        bool parentStopped=false; int lateBegins=0;
        var lateAttempt=new ProbeAttempt(()=>{Interlocked.Increment(ref lateBegins);return new SyntheticChild();},Monotonic.Now+1000,()=>Volatile.Read(ref parentStopped));
        Volatile.Write(ref parentStopped,true); lateAttempt.Begin();
        Check(await lateAttempt.CleanupAsync() && lateBegins==0,"A late attempt ignored its parent's shared stop fence.");
        using var factoryEntered=new ManualResetEventSlim(); using var allowFactory=new ManualResetEventSlim();
        int creates=0; var late=new SyntheticChild();
        var factoryBlocked=new ProbeSupervisor(()=>{ Interlocked.Increment(ref creates); factoryEntered.Set(); allowFactory.Wait(); return late; },100,100,50);
        try
        {
            Check(await WaitForBarrier(factoryEntered,2000),"Factory did not reach its barrier.");
            await Until(()=>factoryBlocked.Latest.Detail.Contains("中断"),1000);
            await Task.Delay(150);
            Check(creates==1 && !factoryBlocked.Completion.IsCompleted,"A blocked factory lost ownership or started a replacement.");
            var watch=Stopwatch.StartNew(); factoryBlocked.Dispose();
            Check(watch.ElapsedMilliseconds<1500,"Dispose exceeded its caller budget while the factory was blocked.");
        }
        finally { factoryBlocked.Dispose(); allowFactory.Set(); await factoryBlocked.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
        Check(late.Stopped && late.Starts==0 && late.Releases==0,"A late factory result was started, released or left unowned after shutdown.");
        foreach (bool blockRelease in new[] {false,true})
        {
            using var entered=new ManualResetEventSlim(); using var allow=new ManualResetEventSlim();
            var child=new SyntheticChild(); Action block=()=>{ entered.Set(); allow.Wait(); };
            if (blockRelease) child.OnRelease=block; else child.OnStart=block;
            int calls=0; var supervisor=new ProbeSupervisor(()=>{Interlocked.Increment(ref calls);return child;},100,100,50);
            try
            {
                Check(await WaitForBarrier(entered,2000),"Bootstrap did not reach its barrier.");
                await Until(()=>child.Stopped && supervisor.Latest.Detail.Contains("中断"),1000);
                await Task.Delay(150);
                Check(calls==1 && !supervisor.Completion.IsCompleted && !child.ReapedDuringStart,"A blocked launch was replaced or reaped prematurely.");
                var watch=Stopwatch.StartNew(); supervisor.Dispose();
                Check(watch.ElapsedMilliseconds<1500,"A blocked Release held up Dispose.");
            }
            finally { supervisor.Dispose(); allow.Set(); await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
            if (!blockRelease) Check(child.Releases==0,"An expired Start was allowed through the gate.");
        }
        using var stopEntered=new ManualResetEventSlim(); using var allowStop=new ManualResetEventSlim();
        var slowStop=new SyntheticChild {Response=new(TaskCreationOptions.RunContinuationsAsynchronously),OnStop=()=>{stopEntered.Set();allowStop.Wait();}};
        var closing=new ProbeSupervisor(()=>slowStop); await Until(()=>slowStop.Reads==1);
        var dispose=Task.Run(()=>closing.Dispose());
        try
        {
            Check(await WaitForBarrier(stopEntered,2000),"Stop did not reach its barrier.");
            await dispose.WaitAsync(TimeSpan.FromMilliseconds(1500));
            Check(!closing.Completion.IsCompleted,"Completion released the lifetime while Stop was still blocked.");
            closing.Dispose();
        }
        finally {allowStop.Set();slowStop.Response.TrySetException(new IOException("fixture close"));await closing.Completion.WaitAsync(TimeSpan.FromSeconds(3));}
        var remaining=new SyntheticChild {OnStart=()=>Thread.Sleep(120),Response=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var absolute=new ProbeSupervisor(()=>remaining,400,100,50);
        try
        {
            await Until(()=>remaining.Reads==1);
            Check(remaining.LastBudget>TimeSpan.Zero && remaining.LastBudget<TimeSpan.FromMilliseconds(300),"First-frame read received a renewed budget after bootstrap.");
        }
        finally {absolute.Dispose();remaining.Response.TrySetException(new IOException("fixture close"));await absolute.Completion.WaitAsync(TimeSpan.FromSeconds(3));}
        using var readEntered=new ManualResetEventSlim(); using var allowRead=new ManualResetEventSlim();
        double caseStarted=Monotonic.Now,factoryAt=0,startAt=0,releaseAt=0,readAt=0;
        var synchronousRead=new SyntheticChild {Cleanup=false,Response=new(TaskCreationOptions.RunContinuationsAsynchronously),
            OnStart=()=>startAt=Monotonic.Now,OnRelease=()=>releaseAt=Monotonic.Now,
            OnRead=()=>{readAt=Monotonic.Now;readEntered.Set();allowRead.Wait();}};
        synchronousRead.Response.SetResult(Packet());
        int readCreates=0; var readBlocked=new ProbeSupervisor(()=>{factoryAt=Monotonic.Now;Interlocked.Increment(ref readCreates);return synchronousRead;},100,100,50);
        try
        {
            bool reached=await WaitForBarrier(readEntered,2000);
            if(!reached)
            {
                ThreadPool.GetAvailableThreads(out int workers,out int ports);
                Console.Error.WriteLine($"Read-barrier diagnostic: elapsed={Monotonic.Now-caseStarted:F3}, factory={factoryAt-caseStarted:F3}, start={startAt-caseStarted:F3}, release={releaseAt-caseStarted:F3}, read={readAt-caseStarted:F3}, creates={readCreates}, starts={synchronousRead.Starts}, releases={synchronousRead.Releases}, reads={synchronousRead.Reads}, stopped={synchronousRead.Stopped}, complete={readBlocked.Completion.IsCompleted}, detail={readBlocked.Latest.Detail}, pool={ThreadPool.ThreadCount}, pending={ThreadPool.PendingWorkItemCount}, available={workers}, processors={Environment.ProcessorCount}");
            }
            Check(reached,"Read did not reach its synchronous barrier.");
            await Until(()=>readBlocked.Latest.Detail.Contains("中断"),1000);
            Check(readCreates==1 && !readBlocked.Completion.IsCompleted,"A blocked read was treated as fully reaped.");
            allowRead.Set(); await readBlocked.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(readBlocked.Latest.Source=="unavailable","A read that completed after the absolute deadline published a valid frame.");
        }
        finally {allowRead.Set();readBlocked.Dispose();await readBlocked.Completion.WaitAsync(TimeSpan.FromSeconds(3));}
    }
    public static async Task Run(string runtime,string script,string assembly)
    {
        await BarrierCoordination();
        await NotificationFences();
        await StartupBudgets();
        var children=new ConcurrentQueue<ProbeProcess>(); int overlap=0;
        var actual=new ProbeSupervisor(()=>{
            if (children.Any(child=>!child.Reaped)) Interlocked.Increment(ref overlap);
            var child=new ProbeProcess(runtime,script,assembly,"hang"); children.Enqueue(child); return child;
        },4000,120,150);
        try
        {
            await Until(()=>actual.Latest.Source=="uia-text"); double stamp=actual.Latest.StampMs; string identity=actual.Latest.Title;
            await Until(()=>actual.Latest.Playing==null && actual.Latest.PositionMs==null && actual.Latest.Source=="unavailable");
            var lost=actual.Latest; var manual=new SyncClock(); manual.Manual(1000,false,Monotonic.Now);
            var snapshot=PlaybackSnapshot.Capture(lost,manual,Monotonic.Now,1,1);
            Check(lost.Title==identity && snapshot.VideoBoundsPx==null && snapshot.VideoBoundsSource=="unavailable" && snapshot.WindowVisible==null && snapshot.WindowMinimized==null && snapshot.IsForeground==null && snapshot.DpiScale==null,
                "A failed generation refreshed stale client geometry or lost its media identity.");
            await Until(()=>actual.Generation>=2 && actual.Latest.Source=="uia-text" && actual.Latest.StampMs>stamp,8000);
            Check(overlap==0,"A replacement child started before its predecessor was fully reaped.");
        }
        finally { actual.Dispose(); await actual.Completion.WaitAsync(TimeSpan.FromSeconds(4)); }
        long stoppedGeneration=actual.Generation; await Task.Delay(250);
        Check(actual.Generation==stoppedGeneration && children.All(child=>child.Reaped),"Shutdown restarted or leaked an owned child.");
        int creates=0; var stuckCleanup=new SyntheticChild {Cleanup=false};
        var failed=new ProbeSupervisor(()=>{ creates++; return stuckCleanup; },100,100,50);
        await failed.Completion.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(100);
        Check(creates==1 && failed.Latest.Playing==null && failed.Latest.PositionMs==null,"An unconfirmed cleanup spawned another child or claimed playback."); failed.Dispose();
        var late=new SyntheticChild {Response=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var stopped=new ProbeSupervisor(()=>late,100,100,50); await Until(()=>late.Reads>0);
        var baseline=stopped.Latest; stopped.Dispose(); late.Response.SetResult(Packet());
        await stopped.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(ReferenceEquals(stopped.Latest,baseline) && stopped.Generation==1,"Late data published after shutdown or restarted the loop.");
        using var startEntered=new ManualResetEventSlim(); using var allowStart=new ManualResetEventSlim();
        var starting=new SyntheticChild {OnStart=()=>{ startEntered.Set(); if (!allowStart.Wait(4000)) throw new Exception("Fixture launch barrier expired."); }};
        var closing=new ProbeSupervisor(()=>starting,100,100,50);
        Check(await WaitForBarrier(startEntered,3000),"Synthetic Start did not reach its barrier.");
        closing.Dispose(); Check(starting.Stopped,"Shutdown lost ownership of the child while Start was blocked.");
        allowStart.Set(); await closing.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(starting.Releases==0 && !starting.ReapedDuringStart,"A stopped in-flight launch was released or reaped too early.");
        var budgetChild=new SyntheticChild {Response=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var budgets=new ProbeSupervisor(()=>budgetChild); await Until(()=>budgetChild.Reads==1);
        Check(budgetChild.LastBudget<=TimeSpan.FromSeconds(8) && budgetChild.LastBudget>TimeSpan.FromSeconds(7),"The production first-frame deadline changed or was renewed.");
        var firstResponse=budgetChild.Response; budgetChild.Response=new(TaskCreationOptions.RunContinuationsAsynchronously); firstResponse.SetResult(Packet());
        await Until(()=>budgetChild.Reads==2);
        Check(budgetChild.LastBudget<=TimeSpan.FromMilliseconds(1500) && budgetChild.LastBudget>TimeSpan.FromMilliseconds(1400),"The normal frame deadline changed or was renewed.");
        budgets.Dispose(); budgetChild.Response.TrySetException(new IOException("fixture close")); await budgets.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        long reapedAt=0,nextAt=0; int factoryCalls=0;
        var paced=new ProbeSupervisor(()=>{
            if (++factoryCalls==2) nextAt=Stopwatch.GetTimestamp();
            return new SyntheticChild {Cleanup=factoryCalls==1,OnReap=()=>{if(factoryCalls==1)reapedAt=Stopwatch.GetTimestamp();}};
        },100,100,80);
        await paced.Completion.WaitAsync(TimeSpan.FromSeconds(2)); paced.Dispose();
        Check(factoryCalls==2 && (nextAt-reapedAt)*1000.0/Stopwatch.Frequency>=80,"The retry delay did not start after confirmed cleanup.");
        Console.WriteLine("PASS: one owned child, unknown on loss, per-generation sequence reset, confirmed cleanup delay and shutdown publication fences.");
    }
    private static async Task NotificationFences()
    {
        var child=new SyntheticChild{Response=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var notices=new ConcurrentQueue<PlayerSample>();
        var supervisor=new ProbeSupervisor(()=>child,4000,500,500);
        supervisor.Updated+=()=>notices.Enqueue(supervisor.Latest);
        try
        {
            await Until(()=>child.Reads==1);
            var first=child.Response;child.Response=new(TaskCreationOptions.RunContinuationsAsynchronously);
            first.SetResult(Packet());
            await Until(()=>notices.Any(s=>s.Source=="uia-text"));
            child.Response.SetException(new IOException("fixture loss"));
            await Until(()=>notices.Any(s=>s.Source=="unavailable"));
        }
        finally {supervisor.Dispose();await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(3));}
        var late=new SyntheticChild{Response=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        var stopped=new ProbeSupervisor(()=>late,4000,500,100);int updates=0;
        stopped.Updated+=()=>Interlocked.Increment(ref updates);
        await Until(()=>late.Reads==1);stopped.Dispose();late.Response.TrySetResult(Packet());
        await stopped.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Check(updates==0,"A frame completed after shutdown must not publish a UI notification.");
        Console.WriteLine("PASS: accepted frames and unknown transitions notify subscribers; shutdown rejects late notices.");
    }
}

internal sealed class SyntheticChild : IProbeChild
{
    public bool Cleanup=true,Stopped,ReapedDuringStart;
    public int Reads,Releases,Starts;
    public TimeSpan LastBudget;
    public TaskCompletionSource<byte[]> Response;
    public Action OnStart,OnReap,OnStop,OnRelease,OnRead;
    private bool starting;
    public void Start() { Interlocked.Increment(ref Starts); starting=true; try { OnStart?.Invoke(); } finally { starting=false; } }
    public void Release() { Interlocked.Increment(ref Releases); OnRelease?.Invoke(); }
    public Task<byte[]> ReadAsync(TimeSpan timeout,CancellationToken stop)
    {
        LastBudget=timeout; Interlocked.Increment(ref Reads);
        OnRead?.Invoke();
        return Response?.Task??Task.FromException<byte[]>(new IOException("fixture disconnected"));
    }
    public void Stop() {Stopped=true;OnStop?.Invoke();}
    public Task<bool> StopAndReapAsync() { Stopped=true; ReapedDuringStart|=starting; OnReap?.Invoke(); return Task.FromResult(Cleanup); }
}
