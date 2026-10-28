using System;
using System.IO;
using System.Collections.Generic;

namespace Popcorn;

internal sealed class CadenceDone : Exception { }
internal sealed class CadenceSampler : IDisposable
{
    public void WaitForNext(int milliseconds)=>CadenceFixture.Sleep(milliseconds);
    public void Dispose()=>CadenceFixture.Disposes++;
    public PlayerSample Poll()
    {
        CadenceFixture.Starts.Add(CadenceFixture.Now);CadenceFixture.Now+=120;
        return new(null,"",0,null,null,CadenceFixture.Now,"fixture","unavailable");
    }
}
public static class CadenceFixture
{
    public static double Now;
    public static int Disposes;
    internal static readonly List<double> Starts=new();
    private static int sleeps;
    public static Stream Output()=>new MemoryStream();
    public static void Sleep(int milliseconds) {Now+=milliseconds;if(++sleeps==3)throw new CadenceDone();}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        Check(ProbeCadence.DelayMilliseconds(1000,1120)==80 && ProbeCadence.DelayMilliseconds(1000,1199.8)==1 &&
            ProbeCadence.DelayMilliseconds(1000,1200)==0 && ProbeCadence.DelayMilliseconds(1000,1500)==0 &&
            ProbeCadence.DelayMilliseconds(1000,900)==200 && ProbeCadence.DelayMilliseconds(double.NaN,1000)==200,
            "The poll scheduler renewed a complete delay or attempted catch-up overlap.");
        Now=0;sleeps=0;Disposes=0;Starts.Clear();
        try {ProbeWorkerRuntime.Run();throw new Exception("Synthetic worker did not stop.");}catch(CadenceDone){}
        Check(Starts.Count==3 && Starts[0]==0 && Starts[1]==200 && Starts[2]==400,"Actual worker added 200ms after a 120ms poll instead of maintaining the intended cadence.");
        Check(Disposes==1,"Actual worker exit must dispose its sampler/event subscription.");
        Now=0;bool replacement=false;
        var old=new object();var fresh=new object();
        var cache=new PlayerControlCache<object>(_=>new[]{replacement ? fresh : old},element=>{
            Now+=120;return new("PlayPauseButton",ReferenceEquals(element,fresh) ? "Play" : "Pause",PlayerControlKind.Button,null,Now);
        },()=>Now);
        var window=new VideoWindow(new IntPtr(1),"fixture",0,0,1280,720);
        double started=Now;cache.Read(window);double replacedAt=Now;replacement=true;Now+=ProbeCadence.DelayMilliseconds(started,Now);
        started=Now;cache.Read(window);Now+=ProbeCadence.DelayMilliseconds(started,Now);
        var corrected=cache.Read(window);
        Check(corrected[0].Name=="Play" && Now-replacedAt<=500,"Poll work plus the cache lifetime delayed replacement correction beyond the response target in the 120ms fixture.");
        Console.WriteLine("PASS: fixed start-to-start cadence, no overlapping catch-up and 120ms-poll readable-peer replacement recovery.");
    }
}
