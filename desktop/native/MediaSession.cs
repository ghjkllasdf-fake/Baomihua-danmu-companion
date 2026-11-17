using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal sealed record MediaSample(string Title,double PositionMs,double DurationMs,bool? Playing,double StampMs,bool TimelineFresh,double? PlaybackRate=null,bool PlaybackBlocked=false);

internal sealed class MediaTimelineProjection
{
    // SMTC publishers normally update timeline anchors about every five seconds.
    // This bounds publisher silence, independently of the 1500ms read heartbeat.
    internal const double MaximumAnchorAgeMs=7500;
    private string media="";
    private uint owner;
    private DateTimeOffset anchor;
    private double anchorPosition,anchorStamp,motionStamp,duration,outputPosition,outputStamp,readStamp,projectionOffset;
    private double? rate,candidateRate;
    private bool anchored,hasOutput,running,continuous;
    internal void Reset(){media="";anchored=hasOutput=running=continuous=false;rate=candidateRate=null;}
    internal MediaSample Read(string title,uint process,double position,double length,bool? playing,DateTimeOffset updated,DateTimeOffset utcNow,double now,double? reportedRate)
    {
        if(media!=title || owner!=process || Math.Abs(duration-length)>2000){Reset();media=title;owner=process;duration=length;}
        double age=(utcNow-updated).TotalMilliseconds;
        bool valid=double.IsFinite(position) && double.IsFinite(length) && length>0 && position>=0 && position<=length &&
            double.IsFinite(now) && double.IsFinite(age) && age>=-500;
        bool blocked=playing!=true;
        if(!valid)return new(title,position,length,playing,now,false,rate,blocked);
        bool changed=!anchored || updated!=anchor || position!=anchorPosition;
        bool supplied=reportedRate.HasValue && double.IsFinite(reportedRate.Value) && reportedRate.Value>0 && reportedRate.Value<=16;
        if(anchored && (updated<anchor || now<readStamp))return new(title,position,length,playing,now,false,rate,blocked);
        double predicted=hasOutput ? outputPosition+(running ? Math.Max(0,now-outputStamp)*(rate??0) : 0) : position;
        double? previousRate=rate;
        if(anchored && now-readStamp>MaximumAnchorAgeMs && !supplied){rate=candidateRate=null;continuous=false;}
        readStamp=now;
        bool backwards=anchored && position<anchorPosition-.5;
        if(changed)
        {
            double nextStamp=now-Math.Max(0,age);
            if(playing==true && (age>MaximumAnchorAgeMs || anchored && nextStamp<anchorStamp-500))
                return new(title,position,length,playing,now,false,rate,true);
            if(!anchored || backwards || playing!=true || !continuous)motionStamp=nextStamp;
            else if(position>anchorPosition+.5)motionStamp=nextStamp;
            if(anchored && !supplied && continuous && playing==true)
            {
                double elapsed=(updated-anchor).TotalMilliseconds,movement=position-anchorPosition;
                double observed=elapsed>=100 && elapsed<=MaximumAnchorAgeMs && movement>0 ? movement/elapsed : 0;
                if(observed>=.25 && observed<=4)
                {
                    // One interval may contain a seek. Only independent,
                    // consistent intervals can establish a nullable rate.
                    if(candidateRate.HasValue && Math.Abs(candidateRate.Value-observed)<=.08)
                    {
                        double mean=(candidateRate.Value+observed)/2,rounded=Math.Round(mean*4)/4;
                        rate=rounded>=.25 && Math.Abs(mean-rounded)<=.08 ? rounded : mean;
                    }
                    candidateRate=observed;
                }
                else candidateRate=null;
            }
            anchor=updated;anchorPosition=position;anchorStamp=nextStamp;projectionOffset=0;anchored=true;
        }
        if(supplied){rate=reportedRate;candidateRate=null;}
        if(playing!=true)candidateRate=null;
        continuous=playing==true;
        bool expired=now-anchorStamp>MaximumAnchorAgeMs || now-motionStamp>MaximumAnchorAgeMs;
        bool fresh=playing!=true || rate.HasValue && !expired;
        if(!fresh){running=false;return new(title,position,length,playing,now,false,rate,blocked||expired);}
        if(!changed && hasOutput && playing==true && (!running || rate!=previousRate))
            projectionOffset=(running ? predicted : outputPosition)-position-Math.Max(0,now-anchorStamp)*rate.Value;
        double projected=playing==true ? position+Math.Max(0,now-anchorStamp)*rate.Value+projectionOffset : changed || !hasOutput ? position : predicted;
        // A new slower anchor can trail the previous prediction without being
        // a backwards seek. Hold until it catches up; never rewind a live scene.
        if(playing==true && hasOutput && !backwards)projected=Math.Max(outputPosition,projected);
        outputPosition=Math.Clamp(projected,0,length);outputStamp=now;hasOutput=true;running=playing==true;
        return new(title,outputPosition,length,playing,now,true,rate,blocked);
    }
}

internal sealed class MediaSessionReader(CancellationToken stop,PlaybackEventSignal changes=null)
{
    private dynamic manager,session;
    private string title="", requestedTitle="";
    private double retryAt, metadataAt;
    private readonly MediaTimelineProjection projection=new();
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

    public MediaSample Read(string windowTitle,uint owner=0)
    {
        double now=Monotonic.Now;
        if (now<retryAt) return null;
        try
        {
            // Consume the modern CsWinRT projection on the probe's MTA worker, never the UI thread.
            manager??=Await(()=>sdk.GetType("Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager").GetMethod("RequestAsync").Invoke(null,null),"GlobalSystemMediaTransportControlsSessionManager",2000);
            var candidates=((IEnumerable)manager.GetSessions()).Cast<dynamic>().Where(s=>(string)s.SourceAppUserModelId=="BaoMiHua.exe").Take(2).ToArray();
            if (candidates.Length!=1) { changes?.Attach(null); return null; }
            if (session!=candidates[0] || requestedTitle!=windowTitle || now-metadataAt>2000)
            {
                if(session!=candidates[0])projection.Reset();
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
            double position=timeline.Position.TotalMilliseconds;
            double? rate=(double?)info.PlaybackRate;
            if(rate.HasValue && (!double.IsFinite(rate.Value) || rate.Value<=0 || rate.Value>16))rate=null;
            return projection.Read(title,owner,position,timeline.EndTime.TotalMilliseconds,playing,timeline.LastUpdatedTime,DateTimeOffset.UtcNow,Monotonic.Now,rate);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or ProbeRestartException or TimeoutException))
        {
            changes?.Attach(null); manager=null; retryAt=now+2000;
            return null;
        }
    }
}
