using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

public sealed class LatencyEvidenceWriter
{
    private readonly string directory,stem;
    private int sequence;
    public string LastPath { get; private set; }
    public LatencyEvidenceWriter(string path) { directory=Path.GetDirectoryName(path); stem=Path.GetFileNameWithoutExtension(path); }
    public void Write(string text)
    {
        string destination=Path.Combine(directory,stem+"-"+(++sequence).ToString("D6")+".json");
        File.WriteAllText(destination+".tmp",text);
        File.Move(destination+".tmp",destination);
        LastPath=destination;
    }
}

public sealed class LatencyHeartbeat
{
    private double last=-1;
    private bool measured;
    public int Count {get;private set;}
    public double MaxGapMs {get;private set;}
    public int WarmupCount {get;private set;}
    public double WarmupMaxGapMs {get;private set;}
    private void Validate(double now)
    {if(!double.IsFinite(now) || now<0 || now<last)throw new ArgumentException("Invalid heartbeat clock.");}
    public void Track(double now)
    {
        Validate(now);
        if(measured) {Count++;if(last>=0)MaxGapMs=Math.Max(MaxGapMs,now-last);}
        else {WarmupCount++;if(last>=0)WarmupMaxGapMs=Math.Max(WarmupMaxGapMs,now-last);}
        last=now;
    }
    public void BeginMeasurement(double now)
    {
        if(measured)return;
        Validate(now);measured=true;last=now;
    }
}

public sealed class SettingsLatencyObserver : IDisposable
{
    private const BindingFlags PrivateInstance=BindingFlags.Instance|BindingFlags.NonPublic;
    private readonly object gate=new();
    private readonly Form host;
    private readonly string control,ownerNonce;
    private readonly LatencyEvidenceWriter writer;
    private readonly Timer timer=new() { Interval=50 };
    private readonly EventInfo settingsEvent;
    private readonly Delegate settingsHandler;
    private readonly object overlay,overlayWindow,surface,playbackComments;
    private readonly Dispatcher dispatcher;
    private readonly JsonSerializerOptions json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
    private readonly List<object> before=new(),after=new(),deliveries=new();
    private readonly JsonElement initialSettings;
    private JsonElement? playbackStart,playbackFinish;
    private long loadedRevision=-1;
    private double lastPlaybackDiagnosticMs;
    private readonly double started=Now;
    private CoreWebView2 observedCore;
    private int navigationCount;
    private readonly LatencyHeartbeat heartbeat=new();
    private bool busy,ready,finished,disposed;
    private string failure="";
    private static double Now=>Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency;

    public SettingsLatencyObserver(object desktop,string outputPath,string controlPath,string nonce):this(desktop,outputPath,controlPath,nonce,null) { }
    public SettingsLatencyObserver(object desktop,string outputPath,string controlPath,string nonce,object comments)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(nonce??"",@"^[a-f0-9]{32}$")) throw new ArgumentException("Invalid test nonce.");
        host=(Form)desktop; writer=new(outputPath); control=controlPath; ownerNonce=nonce;
        playbackComments=comments;
        overlay=host.GetType().GetField("overlay",PrivateInstance).GetValue(host);
        overlayWindow=overlay.GetType().GetField("window",PrivateInstance).GetValue(overlay);
        dispatcher=(Dispatcher)overlayWindow.GetType().GetProperty("Dispatcher").GetValue(overlayWindow);
        surface=overlayWindow.GetType().GetField("Surface").GetValue(overlayWindow);
        initialSettings=Serialize(host.GetType().GetProperty("Settings").GetValue(host));
        settingsEvent=host.GetType().GetEvent("SettingsUpdated");
        settingsHandler=Delegate.CreateDelegate(settingsEvent.EventHandlerType,this,GetType().GetMethod("OnSettings",PrivateInstance));
        settingsEvent.AddEventHandler(host,settingsHandler);
        host.FormClosing+=Closing;
        timer.Tick+=Tick; timer.Start();
        lock(gate) SaveLocked();
    }

    private JsonElement Serialize(object value)=>JsonSerializer.SerializeToElement(value,value.GetType(),json);
    private void SaveLocked()
    {
        string text=JsonSerializer.Serialize(new { ready,finished,failure,ownerNonce,navigationCount,heartbeatCount=heartbeat.Count,maxObserverGapMs=heartbeat.MaxGapMs,
            warmupHeartbeatCount=heartbeat.WarmupCount,warmupMaxObserverGapMs=heartbeat.WarmupMaxGapMs,initialSettings,calibrationBefore=before,calibrationAfter=after,deliveries,
            playbackLoad=new {requested=playbackComments!=null,loadedCount=loadedRevision>=0 ? ((Array)playbackComments).Length : 0,start=playbackStart,finish=playbackFinish} },json);
        writer.Write(text);
    }
    private static object Property(object target,string name)=>target?.GetType().GetProperty(name).GetValue(target);
    private object PlaybackContext()
    {
        object sample=host.GetType().GetField("sample",PrivateInstance).GetValue(host);
        object sync=host.GetType().GetField("sync",PrivateInstance).GetValue(host);
        object video=Property(sample,"Window"); bool? playing=(bool?)Property(sample,"Playing");
        return new {mode=(string)Property(sync,"Mode"),state=playing==true ? "playing" : playing==false ? "paused" : "unknown",
            title=(string)Property(sample,"Title"),windowTitle=(string)Property(video,"Title"),
            hwnd=video==null ? "" : ((IntPtr)Property(video,"Handle")).ToInt64().ToString(),
            mediaRevision=(long)host.GetType().GetField("mediaRevision",PrivateInstance).GetValue(host),
            sampleStampMs=(double)Property(sample,"StampMs"),source=(string)Property(sample,"Source")};
    }
    private JsonElement PlaybackProof(object context,object snapshot,double nativeMs)=>Serialize(new {
        nativeMs,context,visible=(bool)Property(snapshot,"Visible"),running=(bool)Property(snapshot,"Running"),
        activeCount=(int)Property(snapshot,"ActiveCount"),positionMs=(double)Property(snapshot,"PositionMs"),
        pendingWork=(bool)Property(surface,"HasPendingFrame"),fontPx=(double)Property(snapshot,"FontPx")});
    private object Snapshot()=>overlayWindow.GetType().GetMethod("Snapshot").Invoke(overlayWindow,null);
    private async Task<JsonElement> ObservePlayback(int timeoutSeconds=2)
    {
        object context=PlaybackContext();
        var operation=dispatcher.InvokeAsync(()=>{
            object snapshot=Snapshot();
            return PlaybackProof(context,snapshot,Now);
        },DispatcherPriority.Normal);
        var result=await operation.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
        return result;
    }
    private async Task<bool> PreparePlayback()
    {
        var context=Serialize(PlaybackContext());
        string title=context.GetProperty("title").GetString();
        long revision=context.GetProperty("mediaRevision").GetInt64();
        if(title.Length>0 && revision!=loadedRevision)
        {
            overlay.GetType().GetMethod("SetComments").Invoke(overlay,new[]{playbackComments});
            loadedRevision=revision;
        }
        var proof=await ObservePlayback(10); var captured=proof.GetProperty("context");
        if(Now-lastPlaybackDiagnosticMs>=1000)
        {
            lastPlaybackDiagnosticMs=Now;
            Console.Error.WriteLine("Playback load readiness: "+proof.GetRawText());
        }
        if(captured.GetProperty("mode").GetString()!="automatic" || captured.GetProperty("state").GetString()!="playing"
            || !proof.GetProperty("visible").GetBoolean() || !proof.GetProperty("running").GetBoolean()
            || proof.GetProperty("activeCount").GetInt32()<1
            || proof.GetProperty("pendingWork").GetBoolean()
            || proof.GetProperty("nativeMs").GetDouble()-captured.GetProperty("sampleStampMs").GetDouble()>500) return false;
        playbackStart=proof; return true;
    }
    private async Task<JsonElement> ObserveSettledPlayback()
    {
        double deadline=Now+10000;
        while(true)
        {
            var proof=await ObservePlayback();
            if(!proof.GetProperty("pendingWork").GetBoolean())return proof;
            if(Now>=deadline)throw new TimeoutException("The final layout did not finish within the observation deadline.");
            await Task.Delay(20);
        }
    }

    private void OnSettings(object committed)
    {
        JsonElement expected=Serialize(committed); double committedMs=Now;
        object playbackContext=playbackComments==null ? null : PlaybackContext();
        // Production has already queued Configure at Normal priority; this observes its completed state.
        dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>{
            if(disposed) return;
            try
            {
                object actual=surface.GetType().GetField("settings",PrivateInstance).GetValue(surface);
                JsonElement observed=Serialize(actual);
                object snapshot=playbackComments==null ? null : Snapshot(); double nativeMs=Now;
                JsonElement? playback=snapshot==null ? null : PlaybackProof(playbackContext,snapshot,nativeMs);
                lock(gate)
                {
                    if(disposed) return;
                    deliveries.Add(new { committedMs,nativeMs,settings=observed,matchesCommitted=observed.GetRawText()==expected.GetRawText(),playback });
                    SaveLocked();
                }
            }
            catch(Exception ex) { Fail(ex); }
        }));
    }

    private void Fail(Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        lock(gate)
        {
            failure="Observer failed: "+ex.GetType().Name;
            try { SaveLocked(); } catch(Exception storage) { Console.Error.WriteLine(storage.ToString()); }
        }
        if(host.IsHandleCreated && !host.IsDisposed) host.BeginInvoke(new Action(host.Close));
    }

    private void Navigating(object sender,CoreWebView2NavigationStartingEventArgs args)
    {
        try { lock(gate) { navigationCount++; SaveLocked(); } } catch(Exception ex) { Fail(ex); }
    }

    private void Closing(object sender,FormClosingEventArgs args)
    {
        timer.Stop();
        if(observedCore!=null) { observedCore.NavigationStarting-=Navigating; observedCore=null; }
    }

    private async Task Calibrate(CoreWebView2 core,List<object> rows)
    {
        for(int i=0;i<15;i++)
        {
            double nativeBeforeMs=Now;
            string value=await core.ExecuteScriptAsync("[performance.now(),performance.timeOrigin]").WaitAsync(TimeSpan.FromSeconds(2));
            double nativeAfterMs=Now;
            using var document=JsonDocument.Parse(value);
            double browserMs=document.RootElement[0].GetDouble(),timeOriginMs=document.RootElement[1].GetDouble();
            if(disposed) return;
            lock(gate) { rows.Add(new { nativeBeforeMs,browserMs,nativeAfterMs,timeOriginMs }); SaveLocked(); }
            await Task.Delay(5);
        }
    }

    private async void Tick(object sender,EventArgs args)
    {
        if(disposed) return;
        if(ready || busy)
        {
            double tick=Now;
            lock(gate) heartbeat.Track(tick);
        }
        if(busy) return;
        if(Now-started>90000) { lock(gate) { failure="Observer timed out."; SaveLocked(); } host.Close(); return; }
        string instruction=File.Exists(control) ? File.ReadAllText(control) : "";
        if(instruction=="close") { host.Close(); return; }
        if(ready && (finished || instruction!="calibrate-after")) return;
        if(!(bool)host.GetType().GetProperty("Ready").GetValue(host)) return;
        busy=true;
        try
        {
            if(playbackComments!=null && !ready && !await PreparePlayback()) return;
            var core=(CoreWebView2)host.GetType().GetField("core",PrivateInstance).GetValue(host);
            if(observedCore==null)
            {
                observedCore=core; observedCore.NavigationStarting+=Navigating;
                await core.ExecuteScriptAsync("Object.defineProperty(window,'__settingsLatencyOwner',{value:"+JsonSerializer.Serialize(ownerNonce)+",writable:false,configurable:false})").WaitAsync(TimeSpan.FromSeconds(2));
            }
            if(!ready) {lock(gate)heartbeat.BeginMeasurement(Now);}
            await Calibrate(core,ready ? after : before);
            if(playbackComments!=null && ready) playbackFinish=await ObserveSettledPlayback();
            lock(gate) { if(ready) finished=true; else ready=true; SaveLocked(); }
        }
        catch(Exception ex)
        {
            Fail(ex);
        }
        finally { busy=false; }
    }

    public void Dispose()
    {
        lock(gate) disposed=true;
        if(observedCore!=null) { observedCore.NavigationStarting-=Navigating; observedCore=null; }
        timer.Stop(); timer.Dispose(); host.FormClosing-=Closing; settingsEvent.RemoveEventHandler(host,settingsHandler);
    }
}
