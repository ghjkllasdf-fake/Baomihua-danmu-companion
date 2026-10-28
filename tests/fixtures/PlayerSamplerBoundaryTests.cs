using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Automation;

namespace Popcorn;

public static class PlayerSamplerBoundaryTests
{
    private static Exception failure;
    private static VideoWindow window;
    public static VideoWindow FindPlayer() { if (failure!=null) throw failure; return window; }
    private static void Escapes(Exception expected,Action action)
    {
        try { action(); }
        catch (Exception actual) when (ReferenceEquals(actual,expected)) { return; }
        throw new Exception("A worker-restart failure was swallowed by a production sampler filter.");
    }
    public static void Run()
    {
        var sampler=new PlayerSampler(CancellationToken.None);
        failure=new ProbeRestartException(); Escapes(failure,()=>sampler.Poll());
        failure=new TimeoutException(); Escapes(failure,()=>sampler.Poll());
        var reader=new MediaSessionReader(CancellationToken.None);
        var manager=new FailingMediaManager();
        typeof(MediaSessionReader).GetField("manager",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(reader,manager);
        manager.Failure=new ProbeRestartException(); Escapes(manager.Failure,()=>reader.Read("fixture"));
        manager.Failure=new TimeoutException(); Escapes(manager.Failure,()=>reader.Read("fixture"));
        typeof(PlayerSampler).GetField("latest",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(sampler,
            new PlayerSample(new VideoWindow(new IntPtr(1),"fixture",-100,10,1280,720){WindowVisible=true},"fixture",2,1000,true,Monotonic.Now,"fixture","uia-text",200,60000));
        failure=new IOException("fixture unavailable");
        var lost=sampler.Poll();
        if (lost.Window!=null || lost.Title!="" || lost.EpisodeNumber!=0 || lost.DurationMs!=null || lost.Playing!=null || lost.PositionMs!=null || lost.Source!="unavailable")
            throw new Exception("An unreadable player was published with stale geometry or playback identity.");
        _=ProbeProtocol.Encode(lost,1);
        failure=null;
        if (sampler.Poll().Window!=null) throw new Exception("A missing player retained a prior window.");
        CheckControlIntegration();
        CheckEventIntegration();
        Console.WriteLine("PASS: restart failures escape actual sampler/SMTC filters; ordinary loss clears stale window and playback proof.");
    }
    private static void CheckControlIntegration()
    {
        if(!UiaPlayerControls.Candidate("SeekBarSlider","",PlayerControlKind.Other) || !UiaPlayerControls.Candidate("TimeText","",PlayerControlKind.Text) ||
            !UiaPlayerControls.Candidate("PlayPauseButton","",PlayerControlKind.Button) || !UiaPlayerControls.Candidate("","00:01:00",PlayerControlKind.Text) ||
            !UiaPlayerControls.Candidate("","暂停",PlayerControlKind.Button) || !UiaPlayerControls.Candidate("","01:23/60:00",PlayerControlKind.Other) ||
            !UiaPlayerControls.Candidate("","播放 fixture 第2集 01:23",PlayerControlKind.Other) || !UiaPlayerControls.Candidate("","",PlayerControlKind.Button) ||
            !UiaPlayerControls.Candidate("","buffering",PlayerControlKind.Text) || UiaPlayerControls.Candidate("",new string('x',1001),PlayerControlKind.Other))
            throw new Exception("Control selection dropped supported playback evidence or admitted an unrelated control.");
        int discoveries=0,index=0;
        PlayerControlValue[] values={new("SeekBarSlider","",PlayerControlKind.Other,new(1000,0,60000),Monotonic.Now),
            new("","00:01:00",PlayerControlKind.Text,null,Monotonic.Now),new("PlayPauseButton","暂停",PlayerControlKind.Button,null,Monotonic.Now)};
        var cache=new PlayerControlCache<AutomationElement>(_=>{discoveries++;return new AutomationElement[values.Length];},_=>values[index++%values.Length],()=>Monotonic.Now);
        var actual=new PlayerSampler(CancellationToken.None,cache);
        var media=(MediaSessionReader)typeof(PlayerSampler).GetField("media",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(actual);
        typeof(MediaSessionReader).GetField("manager",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(media,new EmptyMediaManager());
        window=new(new IntPtr(1),"fixture 第2集 test",0,0,1280,720);
        try
        {
            var playing=actual.Poll();
            if(playing.PositionMs!=1000 || playing.Playing!=true || playing.DurationMs!=60000 || playing.Title!="fixture" || playing.EpisodeNumber!=2 || playing.Source!="uia-seekbar-ms" || playing.StampMs!=values[0].StampMs)
                throw new Exception("Actual sampler did not consume cached live control values with the original precision and identity.");
            values[0]=values[0] with {Range=new(5000,0,60000),StampMs=Monotonic.Now};values[2]=values[2] with {Name="播放"};
            var paused=actual.Poll();
            if(paused.Playing!=false || paused.PositionMs!=5000 || discoveries!=1 || playing.PositionMs!=1000 || playing.Playing!=true)
                throw new Exception("A cache hit froze playback/seek values or mutated an earlier sample.");
            values[2]=values[2] with {Name="播放速度(S)"};
            if(actual.Poll().Playing!=null) throw new Exception("A potential button reference became unverified playback-state evidence.");
        }
        finally {window=null;}
        Console.WriteLine("PASS: actual candidate selection and sampler consume live cached references without invoking UIA/SMTC.");
    }
    private static void CheckEventIntegration()
    {
        using var signal=new PlaybackEventSignal();
        var session=new SamplerEventSession();
        var manager=new SamplerEventManager{Sessions=new object[]{session}};
        var reader=new MediaSessionReader(CancellationToken.None,signal);
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var values=new (string,object)[]{("manager",manager),("session",session),("title","fixture current"),
            ("requestedTitle","fixture current"),("metadataAt",Monotonic.Now)};
        foreach(var (name,value) in values) typeof(MediaSessionReader).GetField(name,flags).SetValue(reader,value);
        var sample=reader.Read("fixture current");
        if(sample?.Playing!=true || session.Adds!=1)
            throw new Exception("The actual media reader did not attach its current playback event.");
        session.Fire();
        if(!signal.Wait(0)) throw new Exception("The actual media reader event did not wake the sampler signal.");
        manager.Sessions=Array.Empty<object>();
        if(reader.Read("fixture current")!=null || session.Removes!=1)
            throw new Exception("Losing the target session must detach the media reader event.");
        session.Fire();
        if(signal.Wait(0)) throw new Exception("A lost media session retained an active event hint.");
        Console.WriteLine("PASS: actual media reader attaches and clears current-session event hints with synthetic cached metadata.");
    }
}
public sealed class FailingMediaManager
{
    public Exception Failure;
    public IEnumerable GetSessions()=>throw Failure;
}
public sealed class EmptyMediaManager
{
    public IEnumerable GetSessions()=>Array.Empty<object>();
}
public sealed class SamplerEventManager
{
    public object[] Sessions;
    public IEnumerable GetSessions()=>Sessions;
}
public sealed class SamplerEventSession
{
    private EventHandler<object> handlers;
    public int Adds,Removes;
    public string SourceAppUserModelId=>"BaoMiHua.exe";
    public event EventHandler<object> PlaybackInfoChanged {add{Adds++;handlers+=value;}remove{Removes++;handlers-=value;}}
    public void Fire()=>handlers?.Invoke(this,null);
    public SamplerEventInfo GetPlaybackInfo()=>new();
    public SamplerEventTimeline GetTimelineProperties()=>new();
}
public sealed class SamplerEventInfo
{
    public string PlaybackStatus=>"Playing";
    public double? PlaybackRate=>1;
}
public sealed class SamplerEventTimeline
{
    public TimeSpan Position=>TimeSpan.FromSeconds(1);
    public TimeSpan EndTime=>TimeSpan.FromSeconds(60);
    public DateTimeOffset LastUpdatedTime=>DateTimeOffset.UtcNow;
}
