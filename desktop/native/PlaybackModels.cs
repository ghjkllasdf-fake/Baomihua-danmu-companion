using System;

namespace Popcorn;

public sealed record VideoWindow(IntPtr Handle,string Title,int X,int Y,int Width,int Height)
{
    public double? DpiScale { get; init; }
    public bool? WindowVisible { get; init; }
    public bool? WindowMinimized { get; init; }
    public bool? IsForeground { get; init; }
}
public sealed record PlayerSample(VideoWindow Window,string Title,int EpisodeNumber,double? PositionMs,bool? Playing,double StampMs,string Detail="",string Source="uia-text",double PositionResolutionMs=1000,double? DurationMs=null);

public sealed record PixelBounds(int X,int Y,int Width,int Height);

public sealed record PlaybackSnapshot(long Sequence,long MediaRevision,int SeekRevision,string SyncMode,string State,string Title,
    double? PositionMs,int? EpisodeNumber,string Detail,string Source,double? PositionResolutionMs,double? DurationMs,double? SampleMonotonicMs,
    PixelBounds VideoBoundsPx,string VideoBoundsSource,double? DpiScale,bool? WindowVisible,bool? WindowMinimized,bool? IsForeground)
{
    public static PlaybackSnapshot Capture(PlayerSample sample,SyncClock clock,double now,long sequence,long mediaRevision)
    {
        var window=sample.Window;
        string mode=clock.Mode;
        string state=mode is "automatic" or "manual" ? clock.Running ? "playing" : "paused" : window==null ? "unavailable" : "unknown";
        double? position=mode=="manual" ? clock.At(now) : window==null ? null : mode is "automatic" or "stale" ? clock.At(now) : sample.PositionMs;
        PixelBounds bounds=window is { Width:>0,Height:>0 } ? new(window.X,window.Y,window.Width,window.Height) : null;
        return new(sequence,mediaRevision,clock.SeekRevision,mode,state,sample.Title,
            Nonnegative(position),sample.EpisodeNumber>0 ? sample.EpisodeNumber : null,sample.Detail,sample.Source,
            Positive(sample.PositionResolutionMs),Positive(sample.DurationMs),Nonnegative(sample.StampMs),
            bounds,bounds==null ? "unavailable" : "window-client",Positive(window?.DpiScale),window?.WindowVisible,window?.WindowMinimized,window?.IsForeground);
    }
    private static double? Nonnegative(double? value)=>value.HasValue && double.IsFinite(value.Value) && value.Value>=0 ? value : null;
    private static double? Positive(double? value)=>value.HasValue && double.IsFinite(value.Value) && value.Value>0 ? value : null;
}
