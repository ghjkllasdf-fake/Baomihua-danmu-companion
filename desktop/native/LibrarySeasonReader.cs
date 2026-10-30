using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Popcorn;

internal sealed record LibrarySeasonText(string Id,string Text);

internal sealed class SeasonContinuity
{
    private IntPtr handle;
    private uint process;
    private string title="";
    private int episode,season;
    internal int Next(VideoWindow window,SeriesIdentity current)
    {
        string next=SeriesIdentity.TitleKey(current.Title);
        int carry=handle==window.Handle && process==window.ProcessId && title==next && current.Episode==episode+1 ? season : 0;
        handle=window.Handle;process=window.ProcessId;title=next;episode=current.Episode;season=carry;
        return carry;
    }
    internal void Confirm(int value)=>season=value;
}

internal sealed class LibrarySeasonReader(CancellationToken stop)
{
    private string key="";
    private int season;
    private double retryAt;
    private double? previousDuration;
    private readonly SeasonContinuity continuity=new();
    private int sequentialSeason;
    internal string LastReason {get;private set;}="";
    internal int Read(VideoWindow window,SeriesIdentity playing,double? duration)
    {
        string next=window.Handle.ToInt64()+"\n"+window.ProcessId+"\n"+window.Title;
        if(next!=key){sequentialSeason=continuity.Next(window,playing);key=next;season=0;retryAt=0;}
        else if(previousDuration.HasValue && duration.HasValue && Math.Abs(previousDuration.Value-duration.Value)>2000)
        {continuity.Confirm(0);sequentialSeason=0;season=0;retryAt=0;}
        if(duration.HasValue)previousDuration=duration;
        if(playing.Season>0){continuity.Confirm(playing.Season);LastReason="title-season";return playing.Season;}
        if(season>0 || Monotonic.Now<retryAt)return season;
        retryAt=Monotonic.Now+2000;
        if(playing.Episode==0 || SeriesIdentity.EpisodeKey(playing.EpisodeTitle).Length<3 || !duration.HasValue)
        {LastReason="missing-episode-caption-or-duration";return Carry();}
        try
        {
            if(NativeWindows.IsPlayerProcess(window))
            {
                season=CachedSeasonReader.Read(playing,duration,out bool conflict);
                if(conflict){continuity.Confirm(0);sequentialSeason=0;LastReason="conflicting-season-cache";return 0;}
                if(season>0){continuity.Confirm(season);LastReason="current-media-homepage-cache";return season;}
            }
            LastReason="no-library-window";
            foreach(var library in NativeWindows.FindLibraries(window))
            {
                stop.ThrowIfCancellationRequested();
                var root=AutomationElement.FromHandle(library.Handle);
                var request=new CacheRequest{TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.None};
                request.Add(AutomationElement.AutomationIdProperty);request.Add(AutomationElement.NameProperty);
                AutomationElementCollection found;
                using(request.Activate())found=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text));
                var texts=new List<LibrarySeasonText>();
                for(int i=0;i<Math.Min(found.Count,600);i++)
                {
                    stop.ThrowIfCancellationRequested();
                    var value=found[i].Cached;string name=value.Name??"";
                    if(name.Length<=1024)texts.Add(new(value.AutomationId??"",name));
                }
                season=Resolve(playing,duration,texts,out var reason);LastReason=reason;
                if(season>0){continuity.Confirm(season);return season;}
            }
        }
        catch(Exception error) when(error is not (OutOfMemoryException or ProbeRestartException or TimeoutException or OperationCanceledException)){LastReason=error.GetType().Name;}
        return Carry();
    }
    private int Carry()
    {
        if(sequentialSeason>0){season=sequentialSeason;continuity.Confirm(season);LastReason="same-window-consecutive-episode";}
        return season;
    }
    internal static int Resolve(SeriesIdentity playing,double? duration,IEnumerable<LibrarySeasonText> texts)
        =>Resolve(playing,duration,texts,out _);
    private static int Resolve(SeriesIdentity playing,double? duration,IEnumerable<LibrarySeasonText> texts,out string reason)
    {
        reason="missing-current-identity";
        if(playing.Season>0)return playing.Season;
        if(playing.Episode==0 || duration is not >0)return 0;
        string caption=SeriesIdentity.EpisodeKey(playing.EpisodeTitle);
        if(caption.Length<3)return 0;
        var values=texts.ToArray();string titleKey=SeriesIdentity.TitleKey(playing.Title);
        var headers=values.Select(t=>SeriesIdentity.Parse(t.Text))
            .Where(s=>s.Season>0 && s.Episode==0 && SeriesIdentity.TitleKey(s.Title)==titleKey).Select(s=>s.Season).Distinct().ToArray();
        if(headers.Length!=1){reason="season-header-count:"+headers.Length;return 0;}
        var files=values.Where(t=>Regex.IsMatch(t.Text,@"\.(mkv|mp4|avi|mov|webm|m4v|ts)$",RegexOptions.IgnoreCase))
            .Select(t=>SeriesIdentity.Parse(t.Text)).Where(s=>s.Season==headers[0] &&
                SeriesIdentity.TitleKey(s.Title)==titleKey).ToArray();
        if(files.Length==0){reason="no-matching-season-file";return 0;}
        var rows=Enumerable.Range(0,values.Length).Where(i=>Regex.IsMatch(values[i].Text,@"^\s*"+playing.Episode+@"[.、]\s*") &&
            SeriesIdentity.EpisodeKey(values[i].Text)==caption).ToArray();
        bool episode=rows.Length>0;
        bool length=files.Any(f=>f.Episode==playing.Episode) && values.Where(t=>t.Id=="VideoTimeSpanText").Any(t=>SameDuration(t.Text,duration.Value));
        // The details page may still select the previous file while auto-next is
        // playing. In that case the current episode row needs its own duration.
        length|=rows.Any(i=>values.Skip(Math.Max(0,i-2)).Take(Math.Min(i,2)).Any(t=>t.Id=="VideoTimeText" && SameDuration(t.Text,duration.Value)));
        reason="episode-caption:"+episode+";duration:"+length;
        return episode && length ? headers[0] : 0;
    }
    private static bool SameDuration(string text,double expected)
    {
        double ms=0;
        var clock=Regex.Match(text.Split('/','／').Last().Trim(),@"^(?:(?<hours>\d{1,3}):)?(?<minutes>\d{1,3}):(?<seconds>[0-5]\d)$");
        if(clock.Success)
        {
            double.TryParse(clock.Groups["hours"].Value,NumberStyles.None,CultureInfo.InvariantCulture,out double hours);
            ms=(hours*3600+double.Parse(clock.Groups["minutes"].Value,CultureInfo.InvariantCulture)*60+double.Parse(clock.Groups["seconds"].Value,CultureInfo.InvariantCulture))*1000;
        }
        else foreach(Match m in Regex.Matches(text,@"(\d+)\s*(小时|分钟|秒)"))
            ms+=double.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture)*(m.Groups[2].Value=="小时" ? 3600000 : m.Groups[2].Value=="分钟" ? 60000 : 1000);
        return ms>0 && Math.Abs(ms-expected)<=2000;
    }
}
