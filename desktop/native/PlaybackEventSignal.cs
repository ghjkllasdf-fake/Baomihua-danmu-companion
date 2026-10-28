using System;
using System.Reflection;
using System.Threading;

namespace Popcorn;

// Events only wake the MTA sampler. Playback state still comes from a new read.
internal sealed class PlaybackEventSignal : IDisposable
{
    private readonly AutoResetEvent changed=new(false);
    private object source;
    private EventInfo info;
    private Registration registration;
    private int stopping;
    private sealed class Registration(PlaybackEventSignal owner)
    {
        public Delegate Handler;
        public void OnChanged(object sender,object args)=>owner.Notify(this);
    }
    private void Notify(Registration current)
    {
        if(Volatile.Read(ref stopping)!=0 || !ReferenceEquals(Volatile.Read(ref registration),current)) return;
        try { changed.Set(); }
        catch(ObjectDisposedException) when(Volatile.Read(ref stopping)!=0) { }
    }
    public bool Attach(object next)
    {
        if(Volatile.Read(ref stopping)!=0) throw new ObjectDisposedException(nameof(PlaybackEventSignal));
        if(ReferenceEquals(source,next)) return registration!=null;
        Detach();
        var playbackEvent=next?.GetType().GetEvent("PlaybackInfoChanged");
        if(playbackEvent==null) return false;
        var current=new Registration(this);
        current.Handler=Delegate.CreateDelegate(playbackEvent.EventHandlerType,current,
            typeof(Registration).GetMethod(nameof(Registration.OnChanged)));
        source=next; info=playbackEvent; Volatile.Write(ref registration,current);
        try { playbackEvent.AddEventHandler(next,current.Handler); }
        catch(Exception error) when(error is not OutOfMemoryException) { Detach(); throw new ProbeRestartException(); }
        return true;
    }
    private void Detach()
    {
        var previous=registration; var previousSource=source; var previousInfo=info;
        Volatile.Write(ref registration,null); source=null; info=null;
        if(previous==null) return;
        try { previousInfo.RemoveEventHandler(previousSource,previous.Handler); }
        catch(Exception error) when(error is not OutOfMemoryException) { throw new ProbeRestartException(); }
    }
    public bool Wait(int milliseconds)=>changed.WaitOne(milliseconds);
    public void Dispose()
    {
        if(Interlocked.Exchange(ref stopping,1)!=0) return;
        try { Detach(); } finally { changed.Dispose(); }
    }
}
