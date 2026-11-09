using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace Popcorn;

// Explicit opt-in: observe real playback through the exact packaged host DLL,
// packaged probe and bundled API, with an isolated profile. No player inputs.
internal static class PackagedPlaybackEvidence
{
    internal static void Run(string root,int seconds,bool requireComments=true)
    {
        root=Path.GetFullPath(root);seconds=Math.Clamp(seconds,10,300);
        string evidence=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","packaged-playback-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var assembly=Assembly.LoadFrom(Path.Combine(root,"DanmuCompanion.dll"));
        Type Type(string name)=>assembly.GetType("Popcorn."+name,true);
        object Call(string type,string method,params object[] args)=>Type(type).GetMethod(method,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Invoke(null,args);
        object Get(object value,string name)=>value.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).GetValue(value);
        object Field(object value,string name)=>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);
        var paths=Activator.CreateInstance(Type("ApplicationPaths"),root,"packaged-playback",Path.Combine(evidence,"data"));
        Call("NativeRuntime","ValidateApplication",paths);
        Call("NativeRuntime","LoadMediaProjection",root);
        Call("NativeRuntime","ConfigureBrowserEnvironment",paths,0);
        Call("SharedBrowserRuntime","ConfigureLoader",root);
        Call("SharedBrowserRuntime","RequireAvailable",(object)null);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);Application.EnableVisualStyles();
        using var host=(Form)Activator.CreateInstance(Type("DesktopWindow"),root,"","packaged-playback",Path.Combine(evidence,"data"));
        Type("DesktopWindow").GetProperty("SuppressErrorDialogs",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(host,true);
        var sync=Field(host,"sync");var overlay=Field(host,"overlay");
        using var trace=new StreamWriter(Path.Combine(evidence,"trace.jsonl")){AutoFlush=true};
        using var samples=new StreamWriter(Path.Combine(evidence,"probe-samples.jsonl")){AutoFlush=true};
        using var processed=new StreamWriter(Path.Combine(evidence,"processed-samples.jsonl")){AutoFlush=true};
        var frames=new Dictionary<double,string>();
        var probe=Field(host,"probe");
        double started=Monotonic.Now;
        double lastProcessedStamp=-1;
        bool capturingSamples=true;
        Action observeProcessed=()=>{
            if(host.IsDisposed)return;
            double stamp=(double)Field(sync,"lastStamp");string frame;
            lock(samples)
            {
                if(!capturingSamples || stamp==lastProcessedStamp || !frames.TryGetValue(stamp,out frame))return;
                lastProcessedStamp=stamp;
            }
            double now=Monotonic.Now;
            using var input=JsonDocument.Parse(frame);
            processed.WriteLine(JsonSerializer.Serialize(new{now,stamp,seekRevision=Get(sync,"SeekRevision"),mode=Get(sync,"Mode"),
                rate=Get(sync,"Rate"),position=sync.GetType().GetMethod("At").Invoke(sync,new object[]{now}),sample=input.RootElement}));
        };
        Action observeSample=()=>{
            var sample=Get(probe,"Latest");
            string frame=JsonSerializer.Serialize(new{elapsed=Monotonic.Now-started,now=Monotonic.Now,
                position=Get(sample,"PositionMs"),playing=Get(sample,"Playing"),stamp=Get(sample,"StampMs"),
                resolution=Get(sample,"PositionResolutionMs"),rate=Get(sample,"PlaybackRate"),title=Get(sample,"Title"),
                episode=Get(sample,"EpisodeNumber"),source=Get(sample,"Source")});
            lock(samples){if(!capturingSamples)return;frames[(double)Get(sample,"StampMs")]=frame;samples.WriteLine(frame);}
            if(host.IsHandleCreated && !host.IsDisposed)
                try{host.BeginInvoke(observeProcessed);}catch(InvalidOperationException) when(host.IsDisposed || !host.IsHandleCreated){}
        };
        var sampleEvent=probe.GetType().GetEvent("SampleUpdated");sampleEvent.AddEventHandler(probe,observeSample);
        using var timer=new Timer{Interval=200};
        int maxComments=0,maxActive=0;Exception failure=null;
        bool reading=false,exiting=false,timedExit=false;
        timer.Tick+=(_,_)=>{
            if(reading || exiting)return;
            reading=true;
            try
            {
                if(Monotonic.Now-started>=seconds*1000)
                {
                    timedExit=exiting=true;timer.Stop();
                    Type("DesktopWindow").GetMethod("RequestExit").Invoke(host,null);return;
                }
                var sample=Field(host,"sample");
                var snapshot=overlay.GetType().GetMethod("Snapshot").Invoke(overlay,null);
                int count=(int)Get(host,"CommentCount"),active=(int)Get(snapshot,"ActiveCount");
                maxComments=Math.Max(maxComments,count);maxActive=Math.Max(maxActive,active);
                trace.WriteLine(JsonSerializer.Serialize(new{elapsed=Monotonic.Now-started,ready=Get(host,"Ready"),title=Get(sample,"Title"),
                    episode=Get(sample,"EpisodeNumber"),position=Get(sample,"PositionMs"),playing=Get(sample,"Playing"),
                    playbackRate=Get(sample,"PlaybackRate"),clockRate=Get(sync,"Rate"),sampleResolution=Get(sample,"PositionResolutionMs"),
                    sampleSource=Get(sample,"Source"),sampleAge=Monotonic.Now-(double)Get(sample,"StampMs"),overlayPosition=Get(snapshot,"PositionMs"),
                    seekRevision=Get(sync,"SeekRevision"),mode=Get(sync,"Mode"),running=Get(sync,"Running"),comments=count,
                    source=Field(host,"commentSourceTitle"),active,x=Get(snapshot,"X"),text=Get(snapshot,"Text"),
                    layoutPending=Get(snapshot,"LayoutPending")}));
            }
            catch(Exception error){failure=error;exiting=true;timer.Stop();Type("DesktopWindow").GetMethod("RequestExit").Invoke(host,null);}
            finally{reading=false;}
        };
        try{timer.Start();Application.Run(host);}
        finally{timer.Stop();sampleEvent.RemoveEventHandler(probe,observeSample);lock(samples)capturingSamples=false;}
        if(failure!=null)throw failure;
        if(!timedExit || !(bool)Get(host,"Ready"))throw new Exception("Packaged observation did not complete its timed native lifecycle.");
        if(requireComments && (maxComments==0 || maxActive==0))throw new Exception("Packaged live host did not load and display real comments.");
        File.WriteAllText(Path.Combine(evidence,"result.json"),JsonSerializer.Serialize(new{passed=true,hostAssembly=assembly.Location,
            maxComments,maxActive,seconds,requireComments,scope=requireComments ? "exact packaged host/probe and real API/player; isolated profile; no player inputs" :
                "exact packaged host timed shutdown only; no playback acceptance claim"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{passed=true,evidence,maxComments,maxActive,requireComments}));
    }
}
