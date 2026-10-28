using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;

namespace Popcorn;

public static class PlayerControlCacheTests
{
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private sealed class Control { public double Value=1000; public bool Broken; }
    public static void Run()
    {
        double now=100; int discoveries=0,reads=0; var element=new Control();
        var cache=new PlayerControlCache<Control>(_=>{discoveries++;return new[]{element};},control=>{
            reads++; if(control.Broken) throw new IOException("fixture unavailable");
            return new("SeekBarSlider","",PlayerControlKind.Other,new(control.Value,0,60000),now);
        },()=>now);
        var window=new VideoWindow(new IntPtr(1),"fixture episode 1",0,0,1280,720);
        var first=cache.Read(window);
        for(int i=1;i<10;i++) {now=100+i*200;element.Value+=200;var current=cache.Read(window);Check(current[0].Range.Value==element.Value && current[0].StampMs==now,"Cached elements froze values or refreshed old measurement time.");}
        Check(discoveries==5 && reads==10,"Stable controls were scanned every poll or exceeded the 400ms structural recovery limit.");
        Check(first[0].Range.Value==1000,"A later poll mutated an earlier control snapshot.");
        now=2100;cache.Read(window);Check(discoveries==6,"The periodic recovery deadline did not rediscover controls.");
        now=2150;cache.Read(window with {Title="fixture episode 2"});Check(discoveries==7,"A media-title change reused old controls.");
        cache.Read(window with {Handle=new IntPtr(2)});Check(discoveries==8,"A different HWND reused another window's controls.");
        cache.Invalidate();cache.Read(window);Check(discoveries==9,"Explicit detach did not release prior control references.");
        element.Broken=true;
        try {cache.Read(window);throw new Exception("A failed control read published a partial success.");}catch(IOException){}
        element.Broken=false;cache.Read(window);Check(discoveries==10,"A failed control read did not force rediscovery.");
        cache.Read(null);cache.Read(window);Check(discoveries==11,"A missing player did not invalidate its cached controls.");
        now=2350;cache.Read(window);now=2250;cache.Read(window);Check(discoveries==12,"A clock rollback above the discovery time reused old references.");
        now=1;cache.Read(window);Check(discoveries==13,"A clock regression reused references indefinitely.");
        int emptyFinds=0;
        var empty=new PlayerControlCache<Control>(_=>{emptyFinds++;return Array.Empty<Control>();},_=>throw new Exception("Empty cache read an element."),()=>now);
        empty.Read(window);now+=199;empty.Read(window);Check(emptyFinds==1,"An empty tree was repeatedly scanned inside one sampling interval.");
        now+=1;empty.Read(window);Check(emptyFinds==2,"An empty tree hid newly available controls for the full stable-cache lifetime.");
        bool discoveryFails=true;int attempts=0;
        var recovery=new PlayerControlCache<Control>(_=>{attempts++;if(discoveryFails)throw new IOException("fixture discovery failed");return new[]{element};},_=>new("TimeText","00:01",PlayerControlKind.Text,null,now),()=>now);
        try {recovery.Read(window);throw new Exception("Discovery failure was presented as a valid empty tree.");}catch(IOException){}
        discoveryFails=false;recovery.Read(window);Check(attempts==2,"Discovery failure poisoned later recovery.");
        now=0;int slowReads=0;
        var slow=new PlayerControlCache<Control>(_=>Enumerable.Range(0,32).Select(_=>new Control()).ToArray(),control=>{slowReads++;now+=100;return new("TimeText","00:01",PlayerControlKind.Text,null,now);},()=>now);
        var partial=slow.Read(window);
        Check(slowReads==2 && partial.Length==2 && now==200,"A cache hit started extra property reads after the 150ms cooperative budget.");
        foreach(int length in new[]{1,2})
        {
            using var cancel=new CancellationTokenSource();int canceledReads=0;
            var canceled=new PlayerControlCache<Control>(_=>Enumerable.Repeat(element,length).ToArray(),_=>{canceledReads++;cancel.Cancel();return new("TimeText","00:01",PlayerControlKind.Text,null,now);},()=>now,cancel.Token);
            try {canceled.Read(window);throw new Exception("A canceled poll returned playback evidence.");}catch(OperationCanceledException){}
            Check(canceledReads==1,"Cancellation did not stop subsequent cached property reads.");
        }
        foreach(var timeline in new[]{new double[]{100,200,150,160},new double[]{100,90,110,120}})
        {
            var ticks=new Queue<double>(timeline);int unstableReads=0;
            var unstable=new PlayerControlCache<Control>(_=>new[]{element},_=>{unstableReads++;return new("TimeText","00:01",PlayerControlKind.Text,null,0);},()=>ticks.Dequeue());
            Check(unstable.Read(window).Length==0 && unstableReads==0,"An internal clock rollback was missed before sampling started.");
        }
        Console.WriteLine("PASS: live values without repeated discovery, immutable reads, title/HWND/loss/error invalidation, periodic and empty-tree recovery.");
    }
}
