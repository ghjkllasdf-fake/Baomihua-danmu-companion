using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;

namespace Popcorn;

internal static class MediaTimelineTests
{
    private static int checks;
    private static readonly DateTimeOffset Epoch=new(2026,1,1,0,0,0,TimeSpan.Zero);
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static MediaSample Read(MediaTimelineProjection projection,double now,double anchor,double position,double? rate=null,bool? playing=true,double length=300000,double wallOffset=0)=>
        projection.Read("fixture",7,position,length,playing,Epoch.AddMilliseconds(anchor),Epoch.AddMilliseconds(now+wallOffset),now,rate);
    internal static void Run()
    {
        foreach(double rate in new[]{1d,1.75,2})
        {
            var projection=new MediaTimelineProjection();
            Check(!Read(projection,1000,1000,10000).TimelineFresh,"A nullable SMTC rate must not silently become 1x.");
            Check(!Read(projection,6050,6050,10000+5050*rate).TimelineFresh,"One interval cannot distinguish a speed from a seek.");
            var calibrated=Read(projection,11100,11100,10000+10100*rate);
            Check(calibrated.TimelineFresh && Math.Abs(calibrated.PlaybackRate.Value-rate)<.001,"Two consistent real anchor intervals must calibrate a missing playback rate.");
            var sync=new SyncClock();var surface=new OverlaySurface();
            try
            {
                surface.Measure(new Size(1280,720));surface.Arrange(new Rect(0,0,1280,720));
                double start=calibrated.PositionMs;
                surface.Load(new[]{new Comment(start,1,0xffffff,"continuous anchor fixture")});
                double previousX=double.PositiveInfinity;int revision=0;
                for(int i=0;i<34;i++)
                {
                    double now=11100+i*200,anchor=now<16150 ? 11100 : 16150;
                    var sample=Read(projection,now,anchor,10000+(anchor-1000)*rate);
                    Check(sample.TimelineFresh,"A normal five-second anchor cadence must not become stale between updates.");
                    sync.Observe(sample.PositionMs,sample.Playing,sample.StampMs,now,"fixture",200,sample.PlaybackRate);
                    if(i==0)revision=sync.SeekRevision;
                    Check(sync.Mode=="automatic" && sync.SeekRevision==revision,"Ordinary anchors must preserve automatic proof and scene revision.");
                    surface.UpdateClock(sync.At(now),false,Monotonic.Now,sync.SeekRevision,false,0,sync.Rate);surface.Frame();
                    var frame=surface.Snapshot(true);
                    // Inspect the same comment while it is still within its normal lifetime.
                    if((now-11100)*rate<3000)
                    {
                        Check(frame.ActiveCount==1 && frame.X[0]<previousX,"The actual surface must retain a left-moving comment, not clear/replay it.");
                        previousX=frame.X[0];
                    }
                }
            }
            finally{surface.Detach();}
        }
        var exact=new MediaTimelineProjection();
        var moving=Read(exact,6000,1000,10000,2);
        var paused=Read(exact,6200,1000,10000,2,false);
        Check(paused.PositionMs>=moving.PositionMs && Read(exact,7000,1000,10000,2,false).PositionMs==paused.PositionMs,"An old-anchor pause must freeze immediately without rewinding or drift.");
        Check(Read(exact,7100,1000,10000,2,true).PositionMs==paused.PositionMs,"Resuming an unchanged anchor must not include time spent paused.");
        Check(Read(exact,7200,7200,3000,2,false).PositionMs==3000,"A fresh paused seek remains exact.");
        var speed=new MediaTimelineProjection();Read(speed,1000,1000,10000,1);
        Check(Read(speed,1200,1000,10000,2).PositionMs==10200 && Read(speed,1400,1000,10000,2).PositionMs==10600,"A rate change without a new anchor must apply from the current observation, not retroactively.");
        var slowdown=new MediaTimelineProjection();
        Read(slowdown,1000,1000,10000,2);double displayed=Read(slowdown,6000,1000,10000,2).PositionMs;
        Check(Read(slowdown,6050,6050,15050,1).PositionMs>=displayed,"An advancing raw anchor behind the old forecast is not a backward seek.");
        Check(Read(slowdown,6250,6250,2000,1).PositionMs<displayed,"An actually backward raw anchor must still expose a seek.");
        var silent=new MediaTimelineProjection();Read(silent,1000,1000,10000,1);
        Check(!Read(silent,8501,1000,10000,1,wallOffset:-7000).TimelineFresh,"Wall clock changes cannot extend a monotonic anchor lease.");
        var deadline=new MediaTimelineProjection();var deadlineClock=new SyncClock();
        foreach(double now in new[]{1000d,8400,8500,8501})
        {
            var sample=Read(deadline,now,1000,10000,1);
            deadlineClock.Observe(sample.TimelineFresh ? sample.PositionMs : null,sample.TimelineFresh ? sample.Playing : null,now,now,"deadline",200,sample.PlaybackRate,sample.PlaybackBlocked);
            Check(deadlineClock.Running==(now<=8500),"An explicitly expired anchor must stop at its own deadline, not add the missing-read lease.");
        }
        var frozen=new MediaTimelineProjection();Read(frozen,1000,1000,10000,1);
        Read(frozen,6000,6000,10000,1);
        Check(!Read(frozen,10000,10000,10000,1).TimelineFresh,"Changing timestamps without actual motion cannot sustain prediction indefinitely.");
        var future=new MediaTimelineProjection();
        Check(!Read(future,1000,2000,10000,1).TimelineFresh,"Future provider timestamps cannot establish proof.");
        Read(future,2000,2000,10000,1);
        Check(!Read(future,2200,1500,10200,1).TimelineFresh,"Regressing provider timestamps cannot establish proof.");
        var seek=new MediaTimelineProjection();Read(seek,1000,1000,10000);Read(seek,6000,6000,20000);
        Check(!Read(seek,11000,11000,25000).TimelineFresh,"A single forward seek must not be calibrated as a permanent 2x rate.");
        Check(Read(seek,16000,16000,30000).PlaybackRate==1,"Consistent post-seek intervals must eventually calibrate the actual rate.");
        var recorded=new MediaTimelineProjection();var recordedClock=new SyncClock();int accepted=0,scene=0;
        foreach(var row in JsonNode.Parse(File.ReadAllText(Path.Combine("tests","fixtures","recorded-smtc-cadence.json")))["samples"].AsArray())
        {
            double now=1000+row[0].GetValue<double>(),anchor=1000+row[1].GetValue<double>(),age=row[3].GetValue<double>();
            var sample=Read(recorded,now,anchor,10000+row[2].GetValue<double>(),wallOffset:anchor+age-now);
            if(!sample.TimelineFresh){Check(accepted==0,"Real five-second/null-rate capture must never lose proof after calibration.");continue;}
            recordedClock.Observe(sample.PositionMs,sample.Playing,sample.StampMs,now,"recorded",200,sample.PlaybackRate);
            if(accepted++==0)scene=recordedClock.SeekRevision;
            Check(recordedClock.Mode=="automatic" && recordedClock.SeekRevision==scene && recordedClock.Rate==2,"Real anonymized SMTC cadence must remain continuously synchronized at 2x.");
        }
        Check(accepted>30,"The real-cadence regression must exercise substantial post-calibration playback.");
        Console.WriteLine($"PASS: {checks} SMTC anchor/rate/lease/pause/seek and causal OverlaySurface checks.");
    }
}
