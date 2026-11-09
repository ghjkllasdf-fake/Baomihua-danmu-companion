using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Popcorn;

public static class Monotonic
{
    public static double Now => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}

public sealed class PlaybackClock
{
    private double position, stamp;
    public bool Running { get; private set; }
    public double Rate { get; private set; } = 1;
    public void Set(double positionMs, bool playing, double now, double rate=1)
    {
        if (!double.IsFinite(rate) || rate<=0 || rate>16) throw new ArgumentOutOfRangeException(nameof(rate));
        position = Math.Max(0, positionMs); stamp = now; Running = playing;
        Rate=rate;
    }
    public double At(double now) => Math.Max(0, position + (Running ? Math.Max(0, now - stamp)*Rate : 0));
}

public sealed class SyncClock
{
    private readonly PlaybackClock clock=new();
    private readonly List<(double Stamp,double Position)> rateSamples=new();
    private (double Movement,double Elapsed,double Rate)? rateJump;
    private double lastStamp=-1, manualStamp=-1, lastPosition, lastResolution=1000, lastMotionStamp;
    private string media="";
    public string Mode { get; private set; } = "unavailable";
    public int SeekRevision { get; private set; }
    public bool Running=>clock.Running;
    public double Rate=>clock.Rate;
    public double At(double now)=>clock.At(now);
    public double LastProofMs { get; private set; }
    public void Observe(double? position,bool? playing,double stamp,double now,string mediaKey,double resolutionMs=1000,double? playbackRate=null)
    {
        if (!double.IsFinite(now) || now<0) return;
        if (!double.IsFinite(stamp) || stamp<0 || stamp>now) { Tick(now); return; }
        if (stamp<=lastStamp) { Tick(now); return; }
        bool changed=!string.IsNullOrEmpty(mediaKey) && mediaKey!=media;
        if (Mode=="manual" && stamp<=manualStamp && !changed) { lastStamp=stamp; return; }
        if (now-stamp>1500)
        {
            // A chronologically newer identity invalidates the old clock even when its timing proof aged out.
            if (changed) { lastStamp=stamp; InvalidateMedia(0,now,mediaKey); }
            else Tick(now);
            return;
        }
        double sampleElapsed=LastProofMs>0 ? stamp-LastProofMs : 0;
        lastStamp=stamp;
        double resolution=double.IsFinite(resolutionMs) ? Math.Clamp(resolutionMs,1,1000) : 1000;
        bool tracking=(Mode is "automatic" or "stale") && LastProofMs>0 && !changed;
        double rate=playbackRate.HasValue && double.IsFinite(playbackRate.Value) && playbackRate.Value>0 && playbackRate.Value<=16 ? playbackRate.Value : tracking ? clock.Rate : 1;
        double motionRate=rate;
        bool inferredSeek=false;
        if (!playbackRate.HasValue && position.HasValue && double.IsFinite(position.Value) && playing==true && resolution<=200)
            rate=InferRate(position.Value,stamp,tracking,Math.Max(resolution,lastResolution),rate,out inferredSeek,out motionRate);
        else {rateSamples.Clear();rateJump=null;}
        // A speed change may occur anywhere between two samples; allow either
        // endpoint's rate without mistaking ordinary forward motion for a seek.
        double expectedMotion=sampleElapsed*Math.Max(Math.Max(tracking ? clock.Rate : rate,rate),motionRate);
        double uncertainty=tracking ? Math.Max(resolution,lastResolution) : resolution;
        if (!position.HasValue || !playing.HasValue || !double.IsFinite(position.Value))
        {
            if (changed)
            {
                double nextPosition=position.HasValue && double.IsFinite(position.Value) ? position.Value : 0;
                InvalidateMedia(nextPosition,stamp,mediaKey);
                return;
            }
            Tick(now);
            if(tracking && position.HasValue && double.IsFinite(position.Value) &&
                (position.Value-lastPosition-expectedMotion>Math.Max(100,uncertainty) ||
                 position.Value-lastPosition< -Math.Max(100,uncertainty*.5)))
            {
                // The slider already proves the new target while SMTC is still
                // changing state. Isolate the old scene once, without inventing
                // a running status or resetting it again when that status arrives.
                clock.Set(position.Value,false,stamp,rate);Mode="stale";SeekRevision++;
                lastPosition=position.Value;lastResolution=resolution;lastMotionStamp=stamp;
            }
            if (Mode=="automatic") { clock.Set(clock.At(now),false,now,clock.Rate); Mode="stale"; }
            return;
        }
        double drift=position.Value-clock.At(stamp);
        double movement=position.Value-lastPosition;
        bool stationary=tracking && Math.Abs(movement)<.5;
        if(!stationary)lastMotionStamp=stamp;
        // SMTC can stay Playing while VLC buffers at the exact seek target.
        // Stop extrapolating a repeatedly held position; a clock prediction
        // disagreeing with stationary media is not evidence of another seek.
        bool held=stationary && playing.Value && stamp-lastMotionStamp>=Math.Max(400,uncertainty*2);
        bool running=playing.Value && !held;
        // Only observed motion outside the sampled interval (plus source
        // uncertainty) proves a seek, including across pause/resume and gaps.
        bool seek=!tracking || changed || inferredSeek ||
            movement-expectedMotion>Math.Max(100,uncertainty) || movement< -Math.Max(100,uncertainty*.5) ||
            (!playing.Value && !clock.Running && Math.Abs(position.Value-lastPosition)>Math.Max(20,uncertainty*.5));
        if (seek) {SeekRevision++;rateSamples.Clear();rateJump=null;}
        if (seek || drift>Math.Max(20,resolution*.3) || running!=clock.Running || !playing.Value || rate!=clock.Rate)
        {
            // Only a proven displacement may rewind a playing scene. Buffering
            // and its recovery retain the already displayed comment positions.
            double next=seek || !playing.Value ? position.Value : Math.Max(position.Value,clock.At(stamp));
            clock.Set(next,running,stamp,rate);
        }
        Mode="automatic"; LastProofMs=stamp; lastPosition=position.Value; lastResolution=resolution; media=mediaKey;
        Tick(now);
    }
    private double InferRate(double position,double stamp,bool tracking,double uncertainty,double fallback,out bool seek,out double motionRate)
    {
        seek=false;motionRate=fallback;
        if (!tracking) {rateSamples.Clear();rateJump=null;}
        else if(rateSamples.Count==0 && stamp-LastProofMs<=1500)rateSamples.Add((LastProofMs,lastPosition));
        if (rateSamples.Count>0)
        {
            var previous=rateSamples[^1];
            double elapsed=stamp-previous.Stamp,movement=position-previous.Position;
            // Only continuous precise motion is eligible. Seeks, held sliders and
            // long sampling gaps must not teach the clock a new playback speed.
            if (elapsed<=0 || elapsed>1500 || movement<.5 || movement>elapsed*4+uncertainty)
            {
                rateSamples.Clear();rateJump=null;rateSamples.Add((stamp,position));return fallback;
            }
            double stepRate=movement/elapsed;
            motionRate=Math.Clamp(stepRate,.25,4);
            // A small forward seek can resemble one faster sample. The next
            // sample distinguishes that isolated displacement from sustained speed.
            var jump=rateJump;rateJump=null;
            if(jump.HasValue && jump.Value.Movement>jump.Value.Elapsed*Math.Max(jump.Value.Rate,stepRate)+uncertainty)
            {
                seek=true;rateSamples.Clear();rateSamples.Add((stamp,position));
                return Math.Clamp(stepRate,.25,4);
            }
            if(movement>elapsed*fallback+uncertainty)rateJump=(movement,elapsed,fallback);
        }
        rateSamples.Add((stamp,position));
        while(rateSamples.Count>2 && stamp-rateSamples[1].Stamp>=1000)rateSamples.RemoveAt(0);
        var first=rateSamples[0];double span=stamp-first.Stamp;
        if(span<100)return fallback;
        double observed=(position-first.Position)/span;
        if(observed<.25 || observed>4)return fallback;
        // Use a whole sampling window rather than the noisy latest slider step.
        // Quarter-step rates match the player's menu once enough evidence exists.
        double rounded=Math.Round(observed*4)/4;
        return span>=800 && rounded>=.25 && Math.Abs(rounded-observed)<=.08 ? rounded : observed;
    }
    private void InvalidateMedia(double position,double stamp,string key)
    {
        rateSamples.Clear();
        rateJump=null;
        clock.Set(position,false,stamp); Mode="unavailable"; LastProofMs=0; media=key; lastPosition=position; manualStamp=-1; SeekRevision++;
    }
    public void BeginMediaEpoch(double now)
    {
        rateSamples.Clear();
        rateJump=null;
        // The host's identified media revision is authoritative; timing proofs from distinct media are not ordered together.
        clock.Set(0,false,now); Mode="unavailable"; LastProofMs=0; media=""; lastStamp=-1; manualStamp=-1; lastPosition=0;
    }
    public void Tick(double now)
    {
        if (Mode=="automatic" && now-LastProofMs>1500)
        {
            clock.Set(clock.At(LastProofMs+1500),false,now,clock.Rate); Mode="stale";
        }
    }
    public void Manual(double position,bool playing,double now)
    {
        if (Mode=="automatic") throw new InvalidOperationException("正在自动同步，无需手动校时。");
        clock.Set(position,playing,now); manualStamp=now; Mode="manual"; SeekRevision++;
    }
}

public static class MediaIdentity
{
    public static bool Matches(string windowTitle,string sessionTitle,double? durationMs,double sessionDurationMs) =>
        !string.IsNullOrWhiteSpace(windowTitle) && string.Equals(windowTitle.Trim(),sessionTitle?.Trim(),StringComparison.Ordinal) &&
        durationMs.HasValue && double.IsFinite(durationMs.Value) && durationMs.Value>0 &&
        double.IsFinite(sessionDurationMs) && sessionDurationMs>0 && Math.Abs(durationMs.Value-sessionDurationMs)<2000;
}

public sealed class DanmakuSettings
{
    public double AreaFraction { get; set; } = 0.5;
    public double FontSize { get; set; } = 22;
    public bool ScaleWithWindow { get; set; } = true;
    public double SpeedMultiplier { get; set; } = .6;
    public double Opacity { get; set; } = 1;
    public double OutlineWidth { get; set; } = 0;
    public double ShadowStrength { get; set; } = 1;
    public int Density { get; set; } = 100;
    public bool Rolling { get; set; } = true;
    public bool Top { get; set; } = true;
    public bool Bottom { get; set; } = true;
    public bool Dedupe { get; set; } = true;
    public string Keywords { get; set; } = "";
    public double OffsetMs { get; set; }
    public bool Enabled { get; set; } = true;
    public void Validate()
    {
        if (!new[] { .25, .5, .75, 1.0 }.Contains(AreaFraction) ||
            !InRange(FontSize,16,64) || !InRange(SpeedMultiplier,.5,2) || !InRange(Opacity,.1,1) ||
            !InRange(OutlineWidth,0,3) || !InRange(ShadowStrength,0,1) || !InRange(OffsetMs,-30000,30000) ||
            !new[] { 30,60,100 }.Contains(Density) || Keywords == null || Keywords.Length > 2000)
            throw new ArgumentException("Invalid danmaku settings.");
    }
    private static bool InRange(double x, double min, double max) => double.IsFinite(x) && x >= min && x <= max;
}

public sealed record Comment(double TimeMs, int Mode, int Color, string Text);

public static class DanmakuText
{
    public static string SingleLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var result=new StringBuilder(); int count=0; bool space=false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { space=count>0; continue; }
            if (Rune.IsControl(rune)) continue;
            if (count>=120) break;
            if (space) { result.Append(' '); count++; space=false; if (count==120) break; }
            result.Append(rune.ToString()); count++;
        }
        return result.ToString().TrimEnd();
    }
}

public readonly record struct RasterSize(int Width,int Height,long Bytes);

public static class SpriteLimits
{
    public const int MaxDimension=8192, MaxEntries=512;
    public const long MaxSpriteBytes=16*1024*1024, MaxResidentBytes=64*1024*1024;
    // RenderTargetBitmap takes physical pixels; Pbgra32 owns four bytes per pixel.
    // https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.pixelformats.pbgra32
    public static bool TryMeasure(double widthDip,double heightDip,double pixelsPerDip,out RasterSize size)
    {
        size=default;
        if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) || !double.IsFinite(pixelsPerDip) ||
            widthDip<=0 || heightDip<=0 || pixelsPerDip<=0 || !double.IsFinite(96*pixelsPerDip)) return false;
        double width=Math.Ceiling(widthDip*pixelsPerDip), height=Math.Ceiling(heightDip*pixelsPerDip);
        if (width<1 || height<1 || width>MaxDimension || height>MaxDimension) return false;
        long bytes=checked((long)width*(long)height*4);
        if (bytes>MaxSpriteBytes) return false;
        size=new((int)width,(int)height,bytes); return true;
    }
}

public sealed class Motion(double startMs,double durationMs)
{
    public double StartMs { get; private set; } = startMs;
    public double DurationMs { get; private set; } = durationMs;
    public double Progress(double positionMs)=>(positionMs-StartMs)/DurationMs;
    public void Retime(double positionMs,double newDurationMs)
    {
        double progress=Progress(positionMs);
        DurationMs=newDurationMs; StartMs=positionMs-progress*newDurationMs;
    }
}

public sealed class CommentQueue
{
    private readonly Comment[] comments;
    private int next;
    public CommentQueue(Comment[] source) => comments = source.OrderBy(c => c.TimeMs).ToArray();
    public void Seek(double positionMs, double lookbackMs)
    {
        int low = 0, high = comments.Length;
        while (low < high)
        {
            int mid = low + (high-low)/2;
            if (comments[mid].TimeMs < positionMs-lookbackMs) low=mid+1; else high=mid;
        }
        next=low;
    }
    public bool HasDue(double positionMs)=>next<comments.Length && comments[next].TimeMs<=positionMs;
    public List<Comment> TakeDue(double positionMs, double maxAgeMs)
    {
        var result = new List<Comment>();
        while (next < comments.Length && comments[next].TimeMs <= positionMs)
        {
            Comment c = comments[next++];
            if (positionMs-c.TimeMs <= maxAgeMs) result.Add(c);
        }
        return result;
    }
}

public static class LayoutMath
{
    public static double Scale(double width,double height,bool enabled)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width<=0 || height<=0) throw new ArgumentException("Invalid viewport dimensions.");
        return enabled ? Math.Clamp(Math.Min(width/1280,height/720),.5,3) : 1;
    }
    public static bool CanFollow(double previousX,double previousWidth,double previousSpeed,double newSpeed,double viewportWidth,double gap)
    {
        double clearance=viewportWidth-previousX-previousWidth-gap;
        if (clearance<0) return false;
        return newSpeed<=previousSpeed || clearance/(newSpeed-previousSpeed)>=(previousX+previousWidth)/previousSpeed;
    }
}
