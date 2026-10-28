using System;
using System.Threading.Tasks;

namespace Popcorn;

public delegate void FixturePlaybackChanged(FixturePlaybackSession sender,object args);
public sealed class FixturePlaybackSession
{
    private FixturePlaybackChanged handlers;
    public FixturePlaybackChanged LastAdded;
    public bool FailAdd,FailRemove;
    public int Adds,Removes;
    public event FixturePlaybackChanged PlaybackInfoChanged
    {
        add {Adds++;LastAdded=value;handlers+=value;if(FailAdd)throw new InvalidOperationException("fixture add unconfirmed");}
        remove {Removes++;if(FailRemove)throw new InvalidOperationException("fixture remove unconfirmed");handlers-=value;}
    }
    public void Fire()=>handlers?.Invoke(this,new object());
}
public static class PlaybackEventSignalTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static void Restart(Action action)
    {
        try{action();}catch(ProbeRestartException){return;}
        throw new Exception("Unconfirmed event registration/removal must restart the owned worker.");
    }
    public static void Run()
    {
        using(var signal=new PlaybackEventSignal())
        {
            var first=new FixturePlaybackSession();
            Check(signal.Attach(first) && signal.Attach(first) && first.Adds==1,"Same-session polls must not register duplicate handlers.");
            first.Fire();first.Fire();
            Check(signal.Wait(0) && !signal.Wait(0),"Playback hints must wake once and coalesce; they are not state proof.");
            var late=first.LastAdded;
            var second=new FixturePlaybackSession();
            Check(signal.Attach(second) && first.Removes==1 && second.Adds==1,"Replacing a session must detach the old event.");
            late(first,null);
            Check(!signal.Wait(0),"An already queued callback from an old session must not wake the current sampler.");
            var eventTask=Task.Run(second.Fire);
            Check(signal.Wait(1000),"A C# callback from another thread must wake the sampler.");
            eventTask.GetAwaiter().GetResult();
            Check(!signal.Attach(new object()) && second.Removes==1 && !signal.Wait(0),"An unavailable event must fall back without retaining a previous session.");
            Check(!signal.Attach(null),"No current session must have no event subscription.");
        }
        var disposed=new PlaybackEventSignal();
        var current=new FixturePlaybackSession();
        disposed.Attach(current);var afterClose=current.LastAdded;
        disposed.Dispose();disposed.Dispose();
        afterClose(current,null);
        Check(current.Removes==1,"Dispose must detach exactly once and make late callbacks harmless.");
        using(var failed=new PlaybackEventSignal())
        {
            var partial=new FixturePlaybackSession{FailAdd=true};
            Restart(()=>failed.Attach(partial));
            partial.LastAdded(partial,null);
            Check(!failed.Wait(0) && partial.Removes==1,"A partially registered failing handler must be deactivated and removed.");
        }
        var removing=new PlaybackEventSignal();var stubborn=new FixturePlaybackSession{FailRemove=true};
        removing.Attach(stubborn);var queued=stubborn.LastAdded;
        Restart(()=>removing.Attach(null));
        queued(stubborn,null);
        Check(!removing.Wait(0),"Failed removal must deactivate already queued callbacks before requesting worker restart.");
        removing.Dispose();
        Console.WriteLine("PASS: real delegate binding semantics with synthetic typed sessions; no WinRT activation.");
    }
}
