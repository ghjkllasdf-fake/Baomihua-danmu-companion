using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Threading;

public sealed class PlaybackLatencyObserver : IDisposable
{
    private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
    private readonly Form host;
    private readonly object overlay,overlayWindow,comments;
    private readonly object probe;
    private readonly EventInfo probeEvent;
    private readonly Action probeObserver;
    private readonly Dispatcher dispatcher;
    private readonly Timer productionTimer,controlTimer=new() { Interval=25 };
    private readonly string directory,nonce;
    private readonly StreamWriter log;
    private readonly object gate=new();
    private readonly HashSet<string> consumed=new();
    private readonly JsonSerializerOptions json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
    private readonly double started=Now;
    private JsonElement last;
    private long sequence,loadedRevision=-1;
    private volatile bool disposed;
    private bool requestedClose;
    private string currentId="";
    private bool inputCompleted=true;
    public bool ClosedNormally { get; private set; }
    private static double Now=>Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency;
    private static object Property(object target,string name)=>target?.GetType().GetProperty(name).GetValue(target);
    private static object Field(object target,string name)=>target.GetType().GetField(name,Fields).GetValue(target);

    public PlaybackLatencyObserver(object desktop,object syntheticComments,string runDirectory,string ownerNonce)
    {
        host=(Form)desktop; comments=syntheticComments; directory=runDirectory; nonce=ownerNonce;
        if(!System.Text.RegularExpressions.Regex.IsMatch(nonce??"",@"^[a-f0-9]{32}$")) throw new ArgumentException("Invalid owner nonce.");
        Directory.CreateDirectory(directory);
        log=new StreamWriter(new FileStream(Path.Combine(directory,"observations.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false)) { AutoFlush=true };
        overlay=Field(host,"overlay"); overlayWindow=Field(overlay,"window");
        dispatcher=(Dispatcher)Property(overlayWindow,"Dispatcher");
        productionTimer=(Timer)Field(host,"timer");
        productionTimer.Tick+=AfterDesktopUpdate;
        probe=Field(host,"probe"); probeEvent=probe.GetType().GetEvent("SampleUpdated");
        probeObserver=AfterProbeSample; probeEvent.AddEventHandler(probe,probeObserver);
        controlTimer.Tick+=Control; controlTimer.Start();
        lock(gate) WriteLocked(new { type="started",nativeMs=Now,nonce,hostPid=Environment.ProcessId });
    }

    private void WriteLocked(object row)=>log.WriteLine(JsonSerializer.Serialize(row,json));
    private void AfterProbeSample()
    {
        if(disposed || host.IsDisposed || !host.IsHandleCreated) return;
        // Desktop's subscriber posts first on this same control. Capture after
        // its coalesced update, then use the existing WPF dispatcher barrier.
        try { host.BeginInvoke(new Action(()=>AfterDesktopUpdate(null,EventArgs.Empty))); }
        catch(InvalidOperationException) when(disposed || host.IsDisposed || !host.IsHandleCreated) { }
    }
    private void CloseHost()
    {
        var exit=host.GetType().GetMethod("RequestExit",Type.EmptyTypes);
        if(exit!=null)exit.Invoke(host,null);else host.Close();
    }
    private void Fail(Exception error)
    {
        lock(gate) { if(disposed) return; WriteLocked(new { type="failure",nativeMs=Now,error=error.GetType().Name }); }
        Console.Error.WriteLine(error);
        if(!host.IsDisposed) host.BeginInvoke(new Action(CloseHost));
    }

    private void AfterDesktopUpdate(object sender,EventArgs args)
    {
        if(disposed) return;
        try
        {
            double committedMs=Now;
            object sample=Field(host,"sample"),sync=Field(host,"sync"),video=Property(sample,"Window");
            string title=(string)Property(sample,"Title"),windowTitle=(string)Property(video,"Title");
            string hwnd=video==null ? "" : ((IntPtr)Property(video,"Handle")).ToInt64().ToString();
            long mediaRevision=(long)Field(host,"mediaRevision");
            if(title.Length>0 && mediaRevision!=loadedRevision)
            {
                overlay.GetType().GetMethod("SetComments").Invoke(overlay,new[] {comments});
                loadedRevision=mediaRevision;
            }
            string mode=(string)Property(sync,"Mode");
            bool? playing=(bool?)Property(sample,"Playing");
            string state=playing==true ? "playing" : playing==false ? "paused" : "unknown";
            double sampleStampMs=(double)Property(sample,"StampMs");
            double? samplePositionMs=(double?)Property(sample,"PositionMs");
            double sampleResolutionMs=(double)Property(sample,"PositionResolutionMs");
            string source=(string)Property(sample,"Source");
            int seekRevision=(int)Property(sync,"SeekRevision");
            long next=++sequence;
            // The production Tick handler has already queued UpdateClock at Normal priority.
            dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(()=>{
                try
                {
                    if(disposed) return;
                    object snapshot=overlayWindow.GetType().GetMethod("Snapshot").Invoke(overlayWindow,null);
                    lock(gate)
                    {
                        if(disposed) return;
                        var row=new { type="sample",sequence=next,committedMs,nativeMs=Now,title,windowTitle,hwnd,mediaRevision,seekRevision,mode,state,sampleStampMs,samplePositionMs,sampleResolutionMs,source,
                            visible=(bool)Property(snapshot,"Visible"),running=(bool)Property(snapshot,"Running"),
                            activeCount=(int)Property(snapshot,"ActiveCount"),positionMs=(double)Property(snapshot,"PositionMs"),
                            text=(string[])Property(snapshot,"Text"),x=(double[])Property(snapshot,"X"),
                            spriteBytes=(long)Property(snapshot,"SpriteBytes"),workingSetBytes=Environment.WorkingSet,managedBytes=GC.GetTotalMemory(false) };
                        last=JsonSerializer.SerializeToElement(row,json);
                        WriteLocked(row);
                    }
                }
                catch(Exception error) { Fail(error); }
            }));
        }
        catch(Exception error) { Fail(error); }
    }

    private void Control(object sender,EventArgs args)
    {
        if(disposed) return;
        try
        {
            if(Now-started>600000) throw new TimeoutException("Playback response observer timed out.");
            foreach(string path in Directory.GetFiles(directory,"command-*.json").OrderBy(p=>p,StringComparer.Ordinal))
            {
                if(consumed.Contains(path)) continue;
                using var document=JsonDocument.Parse(File.ReadAllText(path));
                var command=document.RootElement;
                if(command.GetProperty("nonce").GetString()!=nonce) throw new InvalidDataException("Wrong observer owner.");
                string kind=command.GetProperty("type").GetString();
                if(kind=="close") { consumed.Add(path); requestedClose=true; CloseHost(); return; }
                string id=command.GetProperty("id").GetString();
                if(!System.Text.RegularExpressions.Regex.IsMatch(id??"",@"^[a-z0-9]{1,24}$")) throw new InvalidDataException("Invalid action id.");
                lock(gate)
                {
                    if(kind=="arm")
                    {
                        if(!inputCompleted) throw new InvalidDataException("An earlier input has not completed.");
                        string expected=command.GetProperty("expectedState").GetString();
                        if(expected is not ("paused" or "playing")) throw new InvalidDataException("Invalid expected state.");
                        if(last.ValueKind==JsonValueKind.Undefined || last.GetProperty("mode").GetString()!="automatic"
                            || last.GetProperty("state").GetString()==expected || last.GetProperty("state").GetString() is not ("playing" or "paused")
                            || !last.GetProperty("visible").GetBoolean() || last.GetProperty("activeCount").GetInt32()<1
                            || Now-last.GetProperty("nativeMs").GetDouble()>400 || Now-last.GetProperty("sampleStampMs").GetDouble()>500) continue;
                        currentId=id; inputCompleted=false;
                        var arm=new { type="arm",id,expectedState=expected,nativeMs=Now,baselineSequence=last.GetProperty("sequence").GetInt64() };
                        WriteLocked(arm);
                        string response=JsonSerializer.Serialize(new { nonce,arm,baseline=last },json);
                        string destination=Path.Combine(directory,"armed-"+id+".json");
                        File.WriteAllText(destination+".tmp",response); File.Move(destination+".tmp",destination);
                    }
                    else if(kind=="input-completed")
                    {
                        if(id!=currentId || inputCompleted) throw new InvalidDataException("Unexpected input confirmation.");
                        string windowTitle=command.GetProperty("windowTitle").GetString();
                        string action=command.GetProperty("action").GetString();
                        if(action is not ("space" or "play-pause-button") || windowTitle!=last.GetProperty("windowTitle").GetString())
                            throw new InvalidDataException("Input targeted another window.");
                        inputCompleted=true;
                        WriteLocked(new { type="input-completed",id,nativeMs=Now,action,windowTitle });
                    }
                    else throw new InvalidDataException("Unknown observer command.");
                    consumed.Add(path);
                }
            }
        }
        catch(Exception error) { Fail(error); }
    }

    public void Dispose()
    {
        productionTimer.Tick-=AfterDesktopUpdate;
        probeEvent.RemoveEventHandler(probe,probeObserver);
        controlTimer.Stop(); controlTimer.Dispose();
        if(!dispatcher.HasShutdownStarted) dispatcher.Invoke(()=>{},DispatcherPriority.Normal,TimeSpan.FromSeconds(1));
        lock(gate)
        {
            if(disposed) return;
            disposed=true; ClosedNormally=requestedClose;
            WriteLocked(new { type="closed",nativeMs=Now,normal=ClosedNormally }); log.Dispose();
        }
    }
}
