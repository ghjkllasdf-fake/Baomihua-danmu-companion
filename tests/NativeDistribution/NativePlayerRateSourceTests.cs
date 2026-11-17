using System;
using System.Diagnostics;
using System.Threading;

namespace Popcorn;

internal static class NativePlayerRateSourceTests
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
        const string assembly122="4075FAF0C522C88D0D22E975583C3C00DE9EBDCABF4428141C085C2903BDA2BA";
        const string native122="6AA0111F8DEC16D88CC61659D3ED2996C0C3702C0557EFC05EA008739767D66A";
        const string assembly124="CA1433AA53490A61646BD99F14F14B15E4C5E59974C3C6144D4DBE4880923AD5";
        const string native124="1AEEBEF244A2232751C7016FE114F80487B3B81808792042B6217E88EF63A3E8";
        Check(NativePlayerRateSource.SelectProfile(assembly122,native122) is {ServicePlayer:"SU",NativePlayer:"Bol",NativeType:"a.aI",NativeHandle:"TN",Position:"BoU",Duration:"Bou",State:"Bot",Disposed:"BoW"},"The verified 1.2.2 profile must retain its own field identities.");
        var profile124=NativePlayerRateSource.SelectProfile(assembly124.ToLowerInvariant(),native124.ToLowerInvariant());
        Check(profile124 is {ServicePlayer:"si",NativePlayer:"BRH",NativeType:"a.ai",NativeHandle:"tI",Position:"BRQ",Duration:"BRq",State:"BRp",Disposed:"BRS"},"The independently inspected 1.2.4 profile must use its new field identities.");
        Check(profile124.Mvid==new Guid("cabf34af-bc64-4a77-b0fe-afe6a2e98ec0"),"The new profile must retain its exact live module identity.");
        Check(NativePlayerRateSource.SelectProfile(assembly122,native124)==null && NativePlayerRateSource.SelectProfile(assembly124,native122)==null,"Mixed managed/native builds must never select a profile.");
        Check(NativePlayerRateSource.SelectProfile("unknown",native124)==null && NativePlayerRateSource.SelectProfile(assembly124,"")==null,"Unknown or missing artifact identities must not guess a layout.");
        var fixture=new PlayerRateBindingTests.Fixture();int calls=0;
        using(var source=new NativePlayerRateSource(_=>{Interlocked.Increment(ref calls);return(fixture.Binding,false);},500))
        {
            Check(SpinWait.SpinUntil(()=>source.ReadProcess(77)==1.75,1500),"Asynchronous discovery must publish the validated rate.");
            for(int i=0;i<20;i++)Check(source.ReadProcess(77)==1.75,"A bound player must not start another discovery.");
            Check(calls==1,"Steady reads must reuse their binding.");
            Check(source.ReadProcessPlayback(77) is {PositionMs:12345,DurationMs:60000,Playing:true,Rate:1.75},"The same verified binding must supply playback proof without UIA.");
            Check(source.ReadProcess(0)==null && fixture.Memory.DisposeCount==1,"A missing player must release its owned process reader.");
            Check(source.ReadProcessPlayback(0)==null,"A lost player must not retain native playback proof.");
        }
        using var release=new ManualResetEventSlim();using var entered=new ManualResetEventSlim();
        var late=new PlayerRateBindingTests.Fixture();
        using(var source=new NativePlayerRateSource(_=>{entered.Set();release.Wait();return(late.Binding,false);},60))
        {
            var watch=Stopwatch.StartNew();
            Check(source.ReadProcess(88)==null && watch.ElapsedMilliseconds<250,"A blocked locator must not block sampling.");
            Check(entered.Wait(1500),"The noncooperative locator fixture must actually enter its read.");
            bool restart=false;
            Check(SpinWait.SpinUntil(()=>{
                try{source.ReadProcess(0);return false;}catch(ProbeRestartException){restart=true;return true;}
            },1500) && restart,"A discovery deadline must require owned-worker restart even when the player disappears.");
            release.Set();
            Check(SpinWait.SpinUntil(()=>Volatile.Read(ref late.Memory.DisposeCount)==1,1500),"A late locator result must be disposed, never published.");
        }
        using var staleRelease=new ManualResetEventSlim();using var staleEntered=new ManualResetEventSlim();
        var stale=new PlayerRateBindingTests.Fixture();var current=new PlayerRateBindingTests.Fixture();
        using(var source=new NativePlayerRateSource(id=>{
            if(id==91){staleEntered.Set();staleRelease.Wait();return(stale.Binding,false);}
            return(current.Binding,false);
        },1000))
        {
            source.ReadProcess(91);Check(staleEntered.Wait(1500),"The old-PID discovery must start.");
            source.ReadProcess(92);staleRelease.Set();
            Check(SpinWait.SpinUntil(()=>source.ReadProcess(92)==1.75,1500),"A new PID must receive its own completed discovery.");
            Check(stale.Memory.DisposeCount==1,"The old-PID result must be rejected and released.");
        }
        int unsupported=0;
        using(var source=new NativePlayerRateSource(_=>{Interlocked.Increment(ref unsupported);return(null,true);},500))
        {
            source.ReadProcess(93);Check(SpinWait.SpinUntil(()=>Volatile.Read(ref unsupported)==1,1500),"Unsupported-build detection must run.");
            for(int i=0;i<30;i++)Check(source.ReadProcess(93)==null,"Unsupported versions must remain a fast optional fallback.");
            Check(unsupported==1,"Unsupported builds must not repeatedly scan the player on every poll.");
        }
        var cold=new PlayerRateBindingTests.Fixture();
        using(var source=new NativePlayerRateSource(_=>{Thread.Sleep(3500);return(cold.Binding,false);}))
        {
            var elapsed=Stopwatch.StartNew();double longestRead=0;
            bool ready=SpinWait.SpinUntil(()=>{
                var call=Stopwatch.StartNew();double? rate=source.ReadProcess(94);
                longestRead=Math.Max(longestRead,call.Elapsed.TotalMilliseconds);
                return rate==1.75;
            },6500);
            Check(ready && elapsed.ElapsedMilliseconds>=3500,"A valid cold diagnostic initialization longer than an interactive call must not restart the sampler.");
            Check(longestRead<250,"Cold initialization must stay outside the sampling thread.");
        }
        using var defaultRelease=new ManualResetEventSlim();using var defaultEntered=new ManualResetEventSlim();
        var defaultLate=new PlayerRateBindingTests.Fixture();
        using(var source=new NativePlayerRateSource(_=>{defaultEntered.Set();defaultRelease.Wait();return(defaultLate.Binding,false);}))
        {
            source.ReadProcess(95);Check(defaultEntered.Wait(1500),"The default-budget noncooperative locator must actually start.");
            var elapsed=Stopwatch.StartNew();double longestRead=0;
            try
            {
                bool restart=SpinWait.SpinUntil(()=>{
                    var call=Stopwatch.StartNew();
                    try{source.ReadProcess(95);return false;}
                    catch(ProbeRestartException){return true;}
                    finally{longestRead=Math.Max(longestRead,call.Elapsed.TotalMilliseconds);}
                },10500);
                Check(restart && elapsed.ElapsedMilliseconds>=7500,"The default discovery budget must expire at its bounded cold-initialization deadline.");
                Check(longestRead<250,"A noncooperative default-budget locator must not block ongoing sampling.");
            }
            finally{defaultRelease.Set();}
            Check(SpinWait.SpinUntil(()=>Volatile.Read(ref defaultLate.Memory.DisposeCount)==1,1500),"The default-budget late result must be disposed after restart, not published.");
        }
        Console.WriteLine($"PASS: {checks} native-rate discovery checks for deadlines, fallback, identity fencing and cleanup.");
    }
}
