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
    public void Set(double positionMs, bool playing, double now)
    {
        position = Math.Max(0, positionMs); stamp = now; Running = playing;
    }
    public double At(double now) => Math.Max(0, position + (Running ? Math.Max(0, now - stamp) : 0));
}

public sealed class SyncClock
{
    private readonly PlaybackClock clock=new();
    private double lastStamp=-1, manualStamp=-1, lastPosition, lastResolution=1000, lastMotionStamp;
    private string media="";
    public string Mode { get; private set; } = "unavailable";
    public int SeekRevision { get; private set; }
    public bool Running=>clock.Running;
    public double At(double now)=>clock.At(now);
    public double LastProofMs { get; private set; }
    public void Observe(double? position,bool? playing,double stamp,double now,string mediaKey,double resolutionMs=1000)
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
                (position.Value-lastPosition-sampleElapsed>Math.Max(100,uncertainty) ||
                 position.Value-lastPosition< -Math.Max(100,uncertainty*.5)))
            {
                // The slider already proves the new target while SMTC is still
                // changing state. Isolate the old scene once, without inventing
                // a running status or resetting it again when that status arrives.
                clock.Set(position.Value,false,stamp);Mode="stale";SeekRevision++;
                lastPosition=position.Value;lastResolution=resolution;lastMotionStamp=stamp;
            }
            if (Mode=="automatic") { clock.Set(clock.At(now),false,now); Mode="stale"; }
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
        bool seek=!tracking || changed ||
            movement-sampleElapsed>Math.Max(100,uncertainty) || movement< -Math.Max(100,uncertainty*.5) ||
            (!playing.Value && !clock.Running && Math.Abs(position.Value-lastPosition)>Math.Max(20,uncertainty*.5));
        if (seek) SeekRevision++;
        if (seek || drift>Math.Max(20,resolution*.3) || running!=clock.Running || !playing.Value)
        {
            // Only a proven displacement may rewind a playing scene. Buffering
            // and its recovery retain the already displayed comment positions.
            double next=seek || !playing.Value ? position.Value : Math.Max(position.Value,clock.At(stamp));
            clock.Set(next,running,stamp);
        }
        Mode="automatic"; LastProofMs=stamp; lastPosition=position.Value; lastResolution=resolution; media=mediaKey;
        Tick(now);
    }
    private void InvalidateMedia(double position,double stamp,string key)
    {
        clock.Set(position,false,stamp); Mode="unavailable"; LastProofMs=0; media=key; lastPosition=position; manualStamp=-1; SeekRevision++;
    }
    public void BeginMediaEpoch(double now)
    {
        // The host's identified media revision is authoritative; timing proofs from distinct media are not ordered together.
        clock.Set(0,false,now); Mode="unavailable"; LastProofMs=0; media=""; lastStamp=-1; manualStamp=-1; lastPosition=0;
    }
    public void Tick(double now)
    {
        if (Mode=="automatic" && now-LastProofMs>1500)
        {
            clock.Set(clock.At(LastProofMs+1500),false,now); Mode="stale";
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
    public bool ScaleWithWindow { get; set; } = false;
    public double SpeedMultiplier { get; set; } = .8;
    public double Opacity { get; set; } = 0.85;
    public double OutlineWidth { get; set; } = 0;
    public int Density { get; set; } = 60;
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
            !InRange(OutlineWidth,0,3) || !InRange(OffsetMs,-30000,30000) ||
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
