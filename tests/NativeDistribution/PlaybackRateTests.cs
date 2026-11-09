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
        VerifySurface();
        Console.WriteLine($"PASS: {checks} checks for 1/1.5/2/3x timing, real seeks, pauses, rate transitions and probe packet compatibility.");
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
