using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Popcorn;

public sealed record ProbePacket(long Sequence,PlayerSample Sample);

public static class ProbeProtocol
{
    public const int MaxFrameBytes=32768;
    private const double MaxMeasurement=9007199254740991;
    private sealed record WindowFrame(long Handle,string Title,int X,int Y,int Width,int Height,double? DpiScale,bool? WindowVisible,bool? WindowMinimized,bool? IsForeground);
    private sealed record Frame(int Version,long Sequence,WindowFrame Window,string Title,int EpisodeNumber,double? PositionMs,bool? Playing,
        double StampMs,string Detail,string Source,double PositionResolutionMs,double? DurationMs);
    private static readonly JsonSerializerOptions Json=new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties=false,RespectRequiredConstructorParameters=true,MaxDepth=8 };
    private static bool Text(string value,int max)=>value!=null && value.Length<=max;
    private static bool Number(double? value)=>!value.HasValue || double.IsFinite(value.Value) && value.Value>=0 && value.Value<=MaxMeasurement;
    private static void Validate(Frame frame,long previous,double now)
    {
        var w=frame?.Window;
        if (frame==null || frame.Version!=1 || frame.Sequence<=previous || frame.Sequence<1 || !Number(now) ||
            !Number(frame.StampMs) || frame.StampMs>now || !Text(frame.Title,1024) || !Text(frame.Detail,512) ||
            frame.EpisodeNumber<0 || !Number(frame.PositionMs) || !Number(frame.DurationMs) ||
            !double.IsFinite(frame.PositionResolutionMs) || frame.PositionResolutionMs<1 || frame.PositionResolutionMs>1000 ||
            frame.Source is not ("uia-text" or "uia-seekbar-ms" or "uia-seekbar-ms+smtc" or "smtc" or "unavailable") ||
            w!=null && (w.Handle==0 || !Text(w.Title,1024) || w.Width<1 || w.Height<1 || w.Width>65536 || w.Height>65536 ||
                w.DpiScale.HasValue && (!double.IsFinite(w.DpiScale.Value) || w.DpiScale.Value<=0 || w.DpiScale.Value>16)) ||
            w==null && (frame.Title!="" || frame.EpisodeNumber!=0 || frame.PositionMs.HasValue || frame.Playing.HasValue || frame.DurationMs.HasValue))
            throw new InvalidDataException("Invalid probe packet.");
    }
    public static byte[] Encode(PlayerSample sample,long sequence)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var w=sample.Window;
        var window=w==null ? null : new WindowFrame(w.Handle.ToInt64(),w.Title,w.X,w.Y,w.Width,w.Height,w.DpiScale,w.WindowVisible,w.WindowMinimized,w.IsForeground);
        var frame=new Frame(1,sequence,window,sample.Title,sample.EpisodeNumber,sample.PositionMs,sample.Playing,sample.StampMs,sample.Detail,sample.Source,sample.PositionResolutionMs,sample.DurationMs);
        Validate(frame,0,sample.StampMs);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(frame,Json);
        if (bytes.Length>MaxFrameBytes) throw new InvalidDataException("Probe packet exceeds its byte budget.");
        return bytes;
    }
    public static ProbePacket Decode(ReadOnlyMemory<byte> bytes,long previous,double now)
    {
        if (bytes.Length is <1 or >MaxFrameBytes) throw new InvalidDataException("Invalid probe packet size.");
        try
        {
            _=new UTF8Encoding(false,true).GetCharCount(bytes.Span);
            var frame=JsonSerializer.Deserialize<Frame>(bytes.Span,Json);
            Validate(frame,previous,now);
            var w=frame.Window;
            var window=w==null ? null : new VideoWindow(new IntPtr(w.Handle),w.Title,w.X,w.Y,w.Width,w.Height)
                { DpiScale=w.DpiScale,WindowVisible=w.WindowVisible,WindowMinimized=w.WindowMinimized,IsForeground=w.IsForeground };
            bool aged=now-frame.StampMs>1500;
            return new(frame.Sequence,new(window,frame.Title,frame.EpisodeNumber,aged ? null : frame.PositionMs,aged ? null : frame.Playing,frame.StampMs,
                aged ? "播放器采样已过期" : frame.Detail,aged ? "unavailable" : frame.Source,frame.PositionResolutionMs,frame.DurationMs>0 ? frame.DurationMs : null));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Invalid probe packet.");
        }
    }
}
