using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace Popcorn;

public static class PlayerSamplerBoundaryTests
{
    private static Exception failure;
    private static VideoWindow window;
    private static string seasonCache;
    private static int libraryLookups;
    private static bool accessBlocked;
    internal static bool AccessBlocked(VideoWindow _)=>accessBlocked;
    internal static bool IsPlayerProcess(VideoWindow _)=>seasonCache!=null;
    internal static VideoWindow[] FindLibraries(VideoWindow _){libraryLookups++;return Array.Empty<VideoWindow>();}
    internal static int ReadCachedSeason(SeriesIdentity playing,double? duration,out bool conflict)
    {
        conflict=false;
        return seasonCache==null ? 0 : CachedSeasonReader.Read(playing,duration,seasonCache,out conflict);
    }
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
        CheckPrivilegeIntegration();
        CheckNativeIntegration();
        CheckSlowTimelineIntegration();
        CheckSeasonsWithoutAccessibility();
        Console.WriteLine("PASS: restart failures escape actual sampler/SMTC filters; ordinary loss clears stale window and playback proof.");
    }
    private static void CheckPrivilegeIntegration()
    {
        int reads=0;
        using var sampler=new PlayerSampler(CancellationToken.None,readPlayback:video=>{
            if(video!=null)reads++;
            return new(1000,60000,true,1);
        });
        var media=(MediaSessionReader)typeof(PlayerSampler).GetField("media",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(sampler);
        typeof(MediaSessionReader).GetField("manager",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(media,new EmptyMediaManager());
        window=new(new IntPtr(1),"fixture movie",0,0,1280,720);
        try
        {
            accessBlocked=true;
            var sample=sampler.Poll();
            if(sample.Detail!="player-elevated" || sample.Title!="" || sample.Source!="unavailable" || sample.PositionMs!=null || reads!=0)
                throw new Exception("A confirmed elevation mismatch must produce waiting diagnostics before reading the player.");
            var packet=ProbeProtocol.Decode(ProbeProtocol.Encode(sample,1),0,Monotonic.Now);
            var snapshot=PlaybackSnapshot.Capture(packet.Sample,new SyncClock(),Monotonic.Now,1,1);
            if(snapshot.Detail!="player-elevated" || snapshot.Title!="")throw new Exception("The privilege diagnostic was lost between sampler and UI snapshot.");
            accessBlocked=false;sample=sampler.Poll();
            if(sample.Detail=="player-elevated" || sample.PositionMs!=1000 || sample.Title!="fixture movie" || reads!=1)
                throw new Exception("Privilege recovery must clear the note and resume fresh player reads.");
            window=null;sample=sampler.Poll();
            if(sample.Detail=="player-elevated")throw new Exception("A missing/unconfirmed player retained an elevation warning.");
        }
        finally{accessBlocked=false;window=null;}
        Console.WriteLine("PASS: confirmed elevation mismatch waits without player reads, survives wire/snapshot, and clears on recovery or missing detection.");
    }
    private static void CheckNativeIntegration()
    {
        var poison=new PlayerControlCache<AutomationElement>(_=>throw new Exception("The subtitle popup UIA provider was accessed."),_=>throw new Exception("A UIA control was read."),()=>Monotonic.Now);
        PlayerPlaybackReading value=new(1000,60000,true,2);
        using var sampler=new PlayerSampler(CancellationToken.None,poison,readPlayback:_=>value);
        var media=(MediaSessionReader)typeof(PlayerSampler).GetField("media",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(sampler);
        typeof(MediaSessionReader).GetField("manager",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(media,new EmptyMediaManager());
        window=new(new IntPtr(1),"fixture movie",0,0,1280,720);
        try
        {
            var sample=sampler.Poll();
            if(sample.Source!="player-memory" || sample.PositionMs!=1000 || sample.Playing!=true || sample.PlaybackRate!=2 || sample.DurationMs!=60000)
                throw new Exception("The sampler did not use live read-only playback evidence without UIA.");
            _=ProbeProtocol.Decode(ProbeProtocol.Encode(sample,1),0,Monotonic.Now);
            value=value with{PositionMs=5000,Playing=false};
            sample=sampler.Poll();
            if(sample.PositionMs!=5000 || sample.Playing!=false)throw new Exception("The native path cached old progress or ignored pause/seek.");
            value=null;sample=sampler.Poll();
            if(sample.PositionMs!=null || sample.Playing!=null || sample.Source!="unavailable")
                throw new Exception("Unsupported/native-unavailable playback must not fall back to UIA or old native evidence.");
            var session=new SamplerEventSession();
            void SetSession(string title,double age)
            {
                session.Timeline.LastUpdatedTime=DateTimeOffset.UtcNow.AddMilliseconds(-age);
                var fields=new (string,object)[]{("manager",new SamplerEventManager{Sessions=new object[]{session}}),("session",session),
                    ("title",title),("requestedTitle",window.Title),("metadataAt",Monotonic.Now)};
                foreach(var (name,field) in fields)typeof(MediaSessionReader).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(media,field);
            }
            SetSession(window.Title,0);sample=sampler.Poll();
            if(sample.Source!="smtc" || sample.PositionMs is not (>=1000 and <=2500) || sample.Playing!=true || sample.PlaybackRate!=1 || sample.DurationMs!=60000)
                throw new Exception("Fresh matching SMTC must remain a usable no-UIA compatibility path.");
            SetSession(window.Title,10000);sample=sampler.Poll();
            if(sample.Source!="unavailable" || sample.PositionMs!=null || sample.Playing!=null)
                throw new Exception("Stale SMTC cannot establish native-unavailable playback.");
            SetSession("another fixture movie",0);sample=sampler.Poll();
            if(sample.Source!="unavailable" || sample.PositionMs!=null || sample.Playing!=null)
                throw new Exception("Mismatched SMTC media cannot establish playback.");
            value=new(8000,60000,false,1.75);sample=sampler.Poll();
            if(sample.Source!="player-memory" || sample.PositionMs!=8000 || sample.Playing!=false)
                throw new Exception("New native proof must replace rejected fallback evidence.");
            window=window with{Title="next fixture movie"};value=new(1200,90000,true,1);SetSession(window.Title,0);sample=sampler.Poll();
            if(sample.Title!=window.Title || sample.PositionMs!=1200 || sample.DurationMs!=90000 || sample.Playing!=true)
                throw new Exception("A real media/title change must publish its new native position and duration, not the previous movie.");
            value=null;SetSession(window.Title,10000);sample=sampler.Poll();
            if(sample.PositionMs!=null || sample.Playing!=null || sample.PlaybackRate!=null)
                throw new Exception("Native failure cannot reuse the previous successful native proof.");
        }
        finally{window=null;}
        Console.WriteLine("PASS: native progress/state/rate, pause/seek, wire, fresh matching SMTC, stale/mismatched rejection and loss of proof never access a toxic UIA provider.");
    }
    private static void CheckSlowTimelineIntegration()
    {
        foreach(double rate in new[]{1d,1.75,2})
        {
            var poison=new PlayerControlCache<AutomationElement>(_=>throw new Exception("SMTC compatibility must not access UIA."),_=>throw new Exception("Unexpected UIA read."),()=>Monotonic.Now);
            using var sampler=new PlayerSampler(CancellationToken.None,poison,readPlayback:_=>null);
            var reader=(MediaSessionReader)typeof(PlayerSampler).GetField("media",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(sampler);
            var session=new SamplerEventSession();session.Info.PlaybackRate=rate;
            session.Timeline.LastUpdatedTime=DateTimeOffset.UtcNow.AddSeconds(-5);
            window=new(new IntPtr(1),"fixture slow timeline",0,0,1280,720);
            try
            {
                foreach(var (name,value) in new (string,object)[]{("manager",new SamplerEventManager{Sessions=new object[]{session}}),("session",session),("title",window.Title),("requestedTitle",window.Title),("metadataAt",Monotonic.Now)})
                    typeof(MediaSessionReader).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(reader,value);
                var sample=sampler.Poll();
                if(sample.Source!="smtc" || sample.Playing!=true || sample.PlaybackRate!=rate || sample.PositionMs<1000+5000*rate || sample.PositionMs>1000+5500*rate)
                    throw new Exception("A successful matching SMTC read with a normal five-second anchor must remain synchronized at its real rate.");
                var clock=new SyncClock();clock.Observe(sample.PositionMs,sample.Playing,sample.StampMs,Monotonic.Now,"fixture",sample.PositionResolutionMs,sample.PlaybackRate);
                if(clock.Mode!="automatic" || clock.Rate!=rate)throw new Exception("A timeline anchor is not the freshness timestamp of the current status read.");
                session.Info.PlaybackStatus="Paused";sample=sampler.Poll();
                if(sample.Playing!=false || sample.PositionMs<1000+5000*rate)throw new Exception("Pause with an unchanged old anchor must freeze the displayed position, not rewind to that anchor.");
                session.Timeline.Position=TimeSpan.FromSeconds(3);session.Timeline.LastUpdatedTime=DateTimeOffset.UtcNow;sample=sampler.Poll();
                if(sample.Playing!=false || sample.PositionMs!=3000)throw new Exception("A new pause/seek anchor must supply its exact position.");
                session.Info.PlaybackStatus="Changing";session.Timeline.EndTime=TimeSpan.Zero;sample=sampler.Poll();
                sample=ProbeProtocol.Decode(ProbeProtocol.Encode(sample,1),0,Monotonic.Now).Sample;
                if(sample.PositionMs!=null || sample.Playing!=null || !sample.PlaybackBlocked)throw new Exception("Explicit buffering/transition status must survive sampler and wire even without a valid duration.");
                clock.Observe(sample.PositionMs,sample.Playing,sample.StampMs,Monotonic.Now,"fixture",200,sample.PlaybackRate,sample.PlaybackBlocked);
                if(clock.Running || clock.Mode!="stale")throw new Exception("An explicit transition must not use the missing-read continuity lease.");
                session.Info.PlaybackStatus="Playing";session.Timeline.EndTime=TimeSpan.FromSeconds(60);session.Timeline.Position=TimeSpan.FromSeconds(30);session.Timeline.LastUpdatedTime=DateTimeOffset.UtcNow;sample=sampler.Poll();
                clock.Observe(sample.PositionMs,sample.Playing,sample.StampMs,Monotonic.Now,"fixture",200,sample.PlaybackRate,sample.PlaybackBlocked);
                if(!clock.Running)throw new Exception("The pause regression must begin with admitted playing proof.");
                session.Info.PlaybackStatus="Paused";session.Timeline.EndTime=TimeSpan.Zero;sample=sampler.Poll();
                sample=ProbeProtocol.Decode(ProbeProtocol.Encode(sample,1),0,Monotonic.Now).Sample;
                clock.Observe(sample.PositionMs,sample.Playing,sample.StampMs,Monotonic.Now,"fixture",200,sample.PlaybackRate,sample.PlaybackBlocked);
                if(!sample.PlaybackBlocked || clock.Running || clock.Mode!="stale")throw new Exception("An explicit pause with invalid timeline must immediately stop through sampler/wire/Core, without coasting or old-anchor rewind.");
            }
            finally{window=null;}
        }
        Console.WriteLine("PASS: normal five-second SMTC anchors remain usable without a native profile or UIA; exact rates and explicit pause survive projection.");
    }
    private static void CheckSeasonsWithoutAccessibility()
    {
        string parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string directory=Path.GetFullPath(Path.Combine(parent,"danmu-no-uia-season-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);seasonCache=directory;libraryLookups=0;
        try
        {
            var reader=new LibrarySeasonReader(CancellationToken.None);
            var video=new VideoWindow(new IntPtr(1),"Fixture Series S03E01",0,0,1280,720){ProcessId=7};
            if(reader.Read(video,SeriesIdentity.Parse(video.Title),60000,false)!=3)
                throw new Exception("Explicit title seasons must remain available without accessibility.");
            reader=new LibrarySeasonReader(CancellationToken.None);
            video=video with{Title="Fixture Series 第2集 Current episode"};
            var item=new {session_title="Fixture Series",player_title=video.Title,last_played=new {season=2,episode=2},
                files=new[]{new {drive_path_info=new {file_path="Fixture Series - S02E02.mkv"},real_total_duration=60}}};
            string body=JsonSerializer.Serialize(new {code=0,data=new {general_albums=new[]{new {album_type="recent_play",list=new[]{item}}}}});
            string cache=Path.Combine(directory,new string('a',64)+".json");
            File.WriteAllText(cache,JsonSerializer.Serialize(new {url="/v2/homepage",statusCode=200,expiryUtc=DateTimeOffset.UtcNow.AddMinutes(5),data=Convert.ToBase64String(Encoding.UTF8.GetBytes(body))}));
            if(reader.Read(video,SeriesIdentity.Parse(video.Title),60000,false)!=2 || reader.LastReason!="current-media-recent-cache")
                throw new Exception("The production no-UIA path must reach a valid isolated recent-play cache.");
            File.Delete(cache);
            video=video with{Title="Fixture Series 第3集 Next episode"};
            if(reader.Read(video,SeriesIdentity.Parse(video.Title),90000,false)!=2 || reader.LastReason!="same-window-consecutive-episode")
                throw new Exception("Confirmed consecutive episodes must retain season continuity without accessibility.");
            reader=new LibrarySeasonReader(CancellationToken.None);
            if(reader.Read(video,SeriesIdentity.Parse(video.Title),90000,false)!=0)
                throw new Exception("A first episode with no title/cache/continuity evidence must leave season unknown.");
            if(libraryLookups!=0)throw new Exception("No-UIA season resolution enumerated player library windows.");
        }
        finally
        {
            seasonCache=null;
            if(!directory.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(directory).StartsWith("danmu-no-uia-season-",StringComparison.Ordinal))throw new Exception("Unsafe fixture cleanup path.");
            Directory.Delete(directory,true);
        }
        Console.WriteLine("PASS: actual no-UIA season reader uses explicit title, isolated valid cache and confirmed continuity; missing evidence stays unknown without library lookup.");
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
    public readonly SamplerEventInfo Info=new();
    public readonly SamplerEventTimeline Timeline=new();
    public SamplerEventInfo GetPlaybackInfo()=>Info;
    public SamplerEventTimeline GetTimelineProperties()=>Timeline;
}
public sealed class SamplerEventInfo
{
    public string PlaybackStatus {get;set;}="Playing";
    public double? PlaybackRate {get;set;}=1;
}
public sealed class SamplerEventTimeline
{
    public TimeSpan Position {get;set;}=TimeSpan.FromSeconds(1);
    public TimeSpan EndTime {get;set;}=TimeSpan.FromSeconds(60);
    public DateTimeOffset LastUpdatedTime {get;set;}=DateTimeOffset.UtcNow;
}
