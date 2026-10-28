using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed record MediaSample(string Title,double PositionMs,double DurationMs,bool? Playing,double StampMs,bool TimelineFresh);

internal sealed class MediaSessionReader(CancellationToken stop,PlaybackEventSignal changes=null)
{
    private dynamic manager,session;
    private string title="", requestedTitle="";
    private double retryAt, metadataAt;
    public string PreferredTitle=>title;
    private static readonly Assembly sdk=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="Microsoft.Windows.SDK.NET");
    private static readonly MethodInfo asTask=sdk.GetType("System.WindowsRuntimeSystemExtensions").GetMethods().Single(m=>
        m.Name=="AsTask" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length==1 && m.GetParameters().Length==2 &&
        m.GetParameters()[0].ParameterType.Name=="IAsyncOperation`1" && m.GetParameters()[1].ParameterType==typeof(CancellationToken));

    private object Await(Func<object> create,string resultType,int timeoutMs)
    {
        // Microsoft's PS6+ projection approach: bridge WinRT to Task at runtime, without targeting an older CLR.
        var task=ProbeOperation.Complete(()=>(Task)asTask.MakeGenericMethod(sdk.GetType("Windows.Media.Control."+resultType)).Invoke(null,new[] {create(),(object)stop}),timeoutMs,stop);
        return task.GetType().GetProperty("Result").GetValue(task);
    }

    public MediaSample Read(string windowTitle)
    {
        double now=Monotonic.Now;
        if (now<retryAt) return null;
        try
        {
            // Consume the modern CsWinRT projection on the probe's MTA worker, never the UI thread.
            manager??=Await(()=>sdk.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager").GetMethod("RequestAsync").Invoke(null,null),"GlobalSystemMediaTransportControlsSessionManager",2000);
            var candidates=((IEnumerable)manager.GetSessions()).Cast<dynamic>().Where(s=>(string)s.SourceAppUserModelId=="BaoMiHua.exe").Take(2).ToArray();
            if (candidates.Length!=1) { changes?.Attach(null); session=null; return null; }
            if (session!=candidates[0] || requestedTitle!=windowTitle || now-metadataAt>2000)
            {
                session=candidates[0];
                dynamic media=Await(()=>session.TryGetMediaPropertiesAsync(),"GlobalSystemMediaTransportControlsSessionMediaProperties",500);
                title=media.Title; requestedTitle=windowTitle; metadataAt=now;
            }
            changes?.Attach((object)session);
            var info=session.GetPlaybackInfo();
            var timeline=session.GetTimelineProperties();
            bool? playing=((object)info.PlaybackStatus).ToString() switch {
                "Playing"=>true,
                "Paused"=>false,
                _=>null
            };
            double age=(DateTimeOffset.UtcNow-timeline.LastUpdatedTime).TotalMilliseconds;
            double position=timeline.Position.TotalMilliseconds;
            if (playing==true)
            {
                if (!double.IsFinite(age) || age< -500) return null;
                position+=Math.Max(0,age)*((double?)info.PlaybackRate??1);
            }
            // LastUpdatedTime is the last timeline update, not the freshness of this successful status read.
            return new(title,Math.Clamp(position,0,Math.Max(0,timeline.EndTime.TotalMilliseconds)),timeline.EndTime.TotalMilliseconds,playing,Monotonic.Now,playing!=true || age<=1500);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or ProbeRestartException or TimeoutException))
        {
            changes?.Attach(null); session=null; manager=null; retryAt=now+2000;
            return null;
        }
    }
}
