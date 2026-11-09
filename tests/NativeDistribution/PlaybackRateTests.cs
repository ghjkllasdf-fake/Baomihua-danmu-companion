using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;

namespace Popcorn;

internal static class PlaybackRateTests
{
    private static int checks;
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    internal static void Run()
    {
        foreach(double rate in new[]{1d,1.5,2,3})
        {
            var sync=new SyncClock();
            for(int i=0;i<13;i++)
            {
                double stamp=1000+i*500,position=20000+i*500*rate;
                sync.Observe(position,true,stamp,stamp,"same-video",200,rate);
                Check(sync.SeekRevision==1,$"Stable {rate}x playback must not create a seek at sample {i}.");
                Check(Math.Abs(sync.At(stamp+100)-(position+100*rate))<.001,"Between-sample prediction must use playback rate.");
            }
            sync.Observe(100000,true,7500,7500,"same-video",200,rate);
            Check(sync.SeekRevision==2,"A real forward seek must still reset once at higher rates.");
            sync.Observe(1000,false,8000,8000,"same-video",200,rate);
            Check(sync.SeekRevision==3 && sync.At(8500)==1000,"A backward seek and pause must freeze the corrected clock.");

            var sample=new PlayerSample(new VideoWindow(new IntPtr(9),"fixture",0,0,800,600),"fixture",1,20000,true,1000,"fixture","smtc",200,120000,rate);
            byte[] bytes=ProbeProtocol.Encode(sample,1);
            Check(ProbeProtocol.Decode(bytes,0,1000).Sample.PlaybackRate==rate,"Probe transport must preserve the observed rate.");
            var legacy=JsonNode.Parse(bytes).AsObject();legacy.Remove("playbackRate");
            Check(ProbeProtocol.Decode(System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString()),0,1000).Sample.PlaybackRate==null,"Legacy packets without rate remain readable.");
            foreach(double invalid in new[]{0d,-1,17})
            {
                bool rejected=false;
                try{ProbeProtocol.Encode(sample with{PlaybackRate=invalid},1);}catch(InvalidDataException){rejected=true;}
                Check(rejected,"Invalid rates must be rejected at the probe boundary.");
            }
        }
        var changing=new SyncClock();
        changing.Observe(10000,true,1000,1000,"same-video",200,1);
        changing.Observe(10750,true,1500,1500,"same-video",200,2);
        Check(changing.SeekRevision==1 && changing.Rate==2,"A rate change is not a seek.");
        changing.Observe(11750,true,2000,2000,"same-video",200,null);
        Check(changing.SeekRevision==1 && changing.Rate==2,"Briefly unavailable rate metadata preserves the known rate.");
        changing.Observe(12500,true,2500,2500,"same-video",200,1.5);
        Check(changing.SeekRevision==1 && changing.Rate==1.5,"Slowing playback must not reset comments.");
        changing.BeginMediaEpoch(3000);
        changing.Observe(0,true,3000,3000,"new-video",200,null);
        Check(changing.Rate==1,"A new media identity must not inherit the old rate.");
        VerifyMissingRate();
        VerifyObservedTiming();
        VerifySurface();
        Console.WriteLine($"PASS: {checks} checks for 1/1.5/2/3x timing, real seeks, pauses, rate transitions and probe packet compatibility.");
    }
    private static void VerifyObservedTiming()
    {
        // Relative stamps/positions from the real 2x reproduction; no media metadata.
        double[][] samples={
            [1000,20000],[2166.379,22180.899],[2363.838,22619.688],[2679.943,23259.673],
            [2860.044,23679.964],[3311.413,24541.497],[3529.146,24959.336],[3692.765,25380.655],
            [3848.793,25600.339],[4093.095,26020.081],[4257.337,26460.9],[4398.401,26659.648],
            [4757.799,27301.352],[5041.362,27940.728],[5187.244,28199.851],[5330.628,28580.108],
            [5786.989,29480.431],[6292.498,30563.079],[6645.063,31200.924],[6800.601,31400.832],
            [7306.616,32519.88],[7607.002,33180.774],[7833.097,33561.023],[8229.569,34201.335]
        };
        var sync=new SyncClock();
        foreach(var sample in samples)
        {
            sync.Observe(sample[1],true,sample[0],sample[0],"relative-timing",200);
            Check(sync.SeekRevision==1,"Observed irregular 2x slider timings must not repeatedly reset the scene.");
        }
        Check(Math.Abs(sync.Rate-2)<.2,"Observed missing-rate playback must converge near 2x.");
    }
    private static void VerifyMissingRate()
    {
        foreach(double rate in new[]{.25,.5,.75,1,1.25,1.5,1.75,2,3})
        {
            var sync=new SyncClock();
            for(int i=0;i<25;i++)
            {
                double stamp=1000+i*250,position=20000+i*250*rate;
                sync.Observe(position,true,stamp,stamp,"no-rate",200,null);
                Check(sync.SeekRevision==1,$"Missing SMTC rate must not clear continuous {rate}x playback at {i}.");
                if(i>=4)Check(Math.Abs(sync.Rate-rate)<.01,"Precise continuous motion must supply the missing rate.");
            }
            sync.Observe(100000,true,7500,7500,"no-rate",200,null);
            Check(sync.SeekRevision==2,"A forward seek must not be inferred as an extreme speed.");
            sync.Observe(1000,false,8000,8000,"no-rate",200,null);
            Check(sync.SeekRevision==3 && sync.At(9000)==1000,"Backward seeks and pauses remain exact with inferred rate.");
        }
        var smallSeek=new SyncClock();
        for(int i=0;i<6;i++)smallSeek.Observe(10000+i*250,true,1000+i*250,1000+i*250,"small-seek",200);
        smallSeek.Observe(12250,true,2500,2500,"small-seek",200);
        smallSeek.Observe(12500,true,2750,2750,"small-seek",200);
        Check(smallSeek.SeekRevision==2 && Math.Abs(smallSeek.Rate-1)<.01,"An isolated small forward jump must reset once rather than becoming a sustained rate.");
        foreach(double interval in new[]{125d,250,500})
        {
            var transition=new SyncClock();
            double positionMs=10000;
            for(int i=0;i<40;i++)
            {
                double rate=i<10 ? 1 : i<20 ? 2 : i<30 ? 1.75 : 1;
                if(i>0)positionMs+=interval*rate;
                double stamp=1000+i*interval;
                transition.Observe(positionMs,true,stamp,stamp,"rate-change",200);
                Check(transition.SeekRevision==1,"Continuous menu rate changes must preserve the comment scene when SMTC has no rate.");
            }
        }
    }
    private static void VerifySurface()
    {
        foreach(double rate in new[]{1d,1.5,2,3})
        {
            var sync=new SyncClock();
            sync.Observe(10000,false,1000,1000,"same-video",200,rate);
            var surface=new OverlaySurface();
            try
            {
                surface.Measure(new Size(1280,720));surface.Arrange(new Rect(0,0,1280,720));
                double predictionStamp=Monotonic.Now-100;
                surface.UpdateClock(10000,true,predictionStamp,0,false,0,rate);
                double predicted=surface.Snapshot(true).PositionMs,after=Monotonic.Now;
                Check(predicted>=10000+100*rate && predicted<=10000+(after-predictionStamp)*rate+.01,"The renderer clock must receive the supplied rate, not coast at 1x.");
                surface.Load(new[]{new Comment(10000,1,0xffffff,"rate fixture")});
                surface.UpdateClock(10000,false,Monotonic.Now,sync.SeekRevision,false,0,rate);surface.Frame();
                var previous=surface.Snapshot(true);
                Check(previous.ActiveCount==1,"A precisely timed fixture comment must be admitted.");
                for(int i=1;i<=4;i++)
                {
                    double stamp=1000+i*500;
                    sync.Observe(10000+i*500*rate,true,stamp,stamp,"same-video",200,rate);
                    surface.UpdateClock(sync.At(stamp),sync.Running,Monotonic.Now,sync.SeekRevision,false,0,sync.Rate);surface.Frame();
                    var current=surface.Snapshot(true);
                    Check(current.ActiveCount==1 && current.X[0]<previous.X[0],"Rate-aware samples must retain the same moving comment instead of clearing/rebuilding it.");
                    previous=current;
                }
            }
            finally{surface.Detach();}
        }
    }
}
