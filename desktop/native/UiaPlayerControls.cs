using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Popcorn;

internal static class UiaPlayerControls
{
    private static PlayerControlKind Kind(ControlType type)=>type==ControlType.Text ? PlayerControlKind.Text : type==ControlType.Button ? PlayerControlKind.Button : PlayerControlKind.Other;
    internal static bool Candidate(string id,string name,PlayerControlKind kind)
    {
        if(id is "SeekBarSlider" or "TimeText" or "PlayPauseButton") return true;
        // Empty/loading labels may become state/time evidence on the next poll.
        if(kind is PlayerControlKind.Text or PlayerControlKind.Button) return true;
        name??="";
        if(name.Length==0 || name.Length>1000) return false;
        return Regex.IsMatch(name,@"^(?:\d{1,3}:\d{2}(?::\d{2})?\s*[/／]\s*\d|(?:播放|暂停).+?第\s*\d+\s*集.*?\d{1,3}:\d{2}(?::\d{2})?)");
    }
    private static CacheRequest Properties()
    {
        var request=new CacheRequest {TreeScope=TreeScope.Element,TreeFilter=Condition.TrueCondition,AutomationElementMode=AutomationElementMode.Full};
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        return request;
    }
    public static AutomationElement[] Discover(VideoWindow window,CancellationToken stop)
    {
        var root=AutomationElement.FromHandle(window.Handle);
        var request=Properties();
        AutomationElementCollection found;
        var standard=new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty,"SeekBarSlider"),
            new PropertyCondition(AutomationElement.AutomationIdProperty,"TimeText"),
            new PropertyCondition(AutomationElement.AutomationIdProperty,"PlayPauseButton"),
            new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text),
            new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
        using(request.Activate())
        {
            found=root.FindAll(TreeScope.Descendants,standard);
            bool progress=false;
            for(int i=0;i<Math.Min(found.Count,800);i++)
                if(found[i].Cached.AutomationId is "SeekBarSlider" or "TimeText") {progress=true;break;}
            if(!progress) found=root.FindAll(TreeScope.Descendants,Condition.TrueCondition);
        }
        var selected=new List<AutomationElement>(); double began=Monotonic.Now;
        for(int i=0;i<Math.Min(found.Count,800) && Monotonic.Now-began<=150;i++)
        {
            stop.ThrowIfCancellationRequested();
            // Discovery filtering uses this snapshot. Read() obtains a new
            // metadata snapshot and a live numeric value on every poll.
            var value=found[i].Cached;
            if(Candidate(value.AutomationId,value.Name,Kind(value.ControlType))) selected.Add(found[i]);
        }
        return selected.ToArray();
    }
    public static PlayerControlValue Read(AutomationElement element)
    {
        var current=element.GetUpdatedCache(Properties()).Cached;
        string id=current.AutomationId,name=current.Name??""; var kind=Kind(current.ControlType);
        PlayerControlRange range=null; double stamp=Monotonic.Now;
        if(id=="SeekBarSlider" && element.TryGetCurrentPattern(RangeValuePattern.Pattern,out var pattern))
        {
            var currentRange=((RangeValuePattern)pattern).Current;
            double value=currentRange.Value; stamp=Monotonic.Now;
            range=new(value,currentRange.Minimum,currentRange.Maximum);
        }
        return new(id,name,kind,range,stamp);
    }
}
