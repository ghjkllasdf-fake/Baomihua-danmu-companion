using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Popcorn;

internal sealed class PlayerSampler : IDisposable
{
    private readonly PlaybackEventSignal changes=new();
    private readonly MediaSessionReader media;
    private readonly PlayerControlCache<AutomationElement> controls;
    public PlayerSampler(CancellationToken stop,PlayerControlCache<AutomationElement> controlCache=null)
    {
        media=new(stop,changes);
        controls=controlCache??new(window=>UiaPlayerControls.Discover(window,stop),UiaPlayerControls.Read,()=>Monotonic.Now,stop);
    }
    public void WaitForNext(int delay)=>changes.Wait(delay);
    public void Dispose()=>changes.Dispose();
    private PlayerSample latest=new(null,"",0,null,null,0,"未检测到播放器","unavailable");
    public PlayerSample Poll()
    {
        try
        {
            var window=NativeWindows.FindPlayer(media.PreferredTitle);
            if(window==null) { controls.Invalidate(); changes.Attach(null); return latest=new(null,"",0,null,null,Monotonic.Now,"未检测到播放器","unavailable"); }
            return latest=Read(window);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or ProbeRestartException or TimeoutException))
        {
            controls.Invalidate(); changes.Attach(null);
            return latest=new(null,"",0,null,null,Monotonic.Now,"无法读取播放器可访问性信息","unavailable");
        }
    }
    private PlayerSample Read(VideoWindow window)
    {
        string title=window.Title.Trim(); int episode=0; double? position=null, sliderValue=null, sliderMaximum=null, duration=null; bool? playing=null;
        double sampleStamp=Monotonic.Now;
        var titleMatch=Regex.Match(title,@"^(?<title>.+?)\s*第\s*(?<ep>\d+)\s*集");
        if (!titleMatch.Success) titleMatch=Regex.Match(title,@"^(?<title>.+?)\s*[-–—]\s*S\d{1,2}E(?<ep>\d+)\b",RegexOptions.IgnoreCase);
        if (titleMatch.Success) { title=titleMatch.Groups["title"].Value.Trim(); int.TryParse(titleMatch.Groups["ep"].Value,out episode); }
        else title=Regex.Replace(title,@"\.(mkv|mp4|avi|mov|webm|m4v|ts)$","",RegexOptions.IgnoreCase);
        title=Regex.Replace(title,@"\s*[-|]\s*网易爆米花$","");
        if (title is "网易爆米花" or "爆米花") title="";
        foreach(var current in controls.Read(window))
        {
            string name=current.Name;
            if (current.Id=="SeekBarSlider" && current.Range is {} range)
            {
                if (double.IsFinite(range.Value) && double.IsFinite(range.Maximum) && range.Minimum==0 && range.Value>=0 && range.Value<=range.Maximum)
                { sliderValue=range.Value; sliderMaximum=range.Maximum; sampleStamp=current.StampMs; }
            }
            if (name.Length==0 || name.Length>1000) continue;
            if (current.Kind==PlayerControlKind.Text && Regex.IsMatch(name,@"^\d{1,3}:\d{2}(?::\d{2})?$"))
            {
                if (current.Id=="TimeText") position=ParseTime(name);
                else duration=ParseTime(name);
            }
            if (current.Kind==PlayerControlKind.Button)
            {
                if (Regex.IsMatch(name,@"^(暂停|Pause)(?:[\s（(]|$)",RegexOptions.IgnoreCase)) playing=true;
                else if (Regex.IsMatch(name,@"^(播放|继续|Play|Resume)(?:[\s（(]|$)",RegexOptions.IgnoreCase)) playing=false;
            }
            var pair=Regex.Match(name,@"^(?<time>\d{1,3}:\d{2}(?::\d{2})?)\s*[/／]\s*\d");
            if (pair.Success) position=ParseTime(pair.Groups["time"].Value);
            var combined=Regex.Match(name,@"^(?<verb>播放|暂停).+?第\s*(?<ep>\d+)\s*集.*?(?<time>\d{1,3}:\d{2}(?::\d{2})?)");
            if (combined.Success)
            {
                position=ParseTime(combined.Groups["time"].Value); playing=combined.Groups["verb"].Value=="暂停";
                int.TryParse(combined.Groups["ep"].Value,out episode);
            }
        }
        bool precise=sliderValue.HasValue && sliderMaximum.HasValue && duration.HasValue && Math.Abs(sliderMaximum.Value-duration.Value)<2000;
        if (precise) position=sliderValue;
        string source=precise ? "uia-seekbar-ms" : "uia-text";
        var session=media.Read(window.Title);
        if (session!=null && (position.HasValue || session.TimelineFresh) && MediaIdentity.Matches(window.Title,session.Title,duration,session.DurationMs))
        {
            playing=session.Playing;
            if (!position.HasValue) { position=session.PositionMs; sampleStamp=session.StampMs; precise=true; }
            duration=session.DurationMs;
            source=source=="uia-seekbar-ms" ? "uia-seekbar-ms+smtc" : "smtc";
        }
        return new(window,title,episode,position,playing,precise ? sampleStamp : Monotonic.Now,
            position.HasValue && playing.HasValue ? "Windows 媒体会话 + 可访问性 · 窗口客户区" : position.HasValue ? "已读到真实进度，播放/暂停状态尚未确认" : "已识别窗口客户区，但尚未读到完整播放状态/进度",
            source,precise ? 200 : 1000,duration);
    }
    public static double? ParseTime(string text)
    {
        var parts=text.Split(':'); if (parts.Length<2 || parts.Length>3) return null;
        double total=0;
        for (int i=0;i<parts.Length;i++)
        {
            if (!int.TryParse(parts[i],NumberStyles.None,CultureInfo.InvariantCulture,out int value) || value<0 || (i>0 && value>59)) return null;
            total=total*60+value;
        }
        return total*1000;
    }
}
