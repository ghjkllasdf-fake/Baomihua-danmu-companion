using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace FakeAutomation
{
    public static class Telemetry {public static double Now,ValueAt;public static int Discoveries,CurrentProperties,CacheActivations,UpdatedCaches;public static readonly List<int> QuerySizes=new();}
    public sealed class ControlType {public static readonly ControlType Text=new(),Button=new(),Other=new();}
    public enum TreeScope {Element,Descendants}
    public enum AutomationElementMode {None,Full}
    public class Condition {public static readonly Condition TrueCondition=new();public virtual bool Matches(AutomationElement element)=>true;}
    public sealed class PropertyCondition(object property,object value):Condition
    {
        public override bool Matches(AutomationElement element)=>property==AutomationElement.AutomationIdProperty ? Equals(element.Id,value) :
            property==AutomationElement.ControlTypeProperty && Equals(element.Type,value);
    }
    public sealed class OrCondition(params Condition[] conditions):Condition
    {public override bool Matches(AutomationElement element)=>conditions.Any(condition=>condition.Matches(element));}
    public sealed class CacheRequest
    {
        public static CacheRequest Active;
        public TreeScope TreeScope;
        public Condition TreeFilter;
        public AutomationElementMode AutomationElementMode;
        private readonly HashSet<object> properties=new();
        public void Add(object property)=>properties.Add(property);
        public IDisposable Activate(){var prior=Active;Active=this;Telemetry.CacheActivations++;return new Scope(prior);}
        public void Validate()
        {
            if(TreeScope!=TreeScope.Element || TreeFilter!=Condition.TrueCondition || AutomationElementMode!=AutomationElementMode.Full ||
                !properties.SetEquals(new[]{AutomationElement.AutomationIdProperty,AutomationElement.NameProperty,AutomationElement.ControlTypeProperty}))
                throw new Exception("Discovery cache must include all three selection properties and retain full live element references.");
        }
        private sealed class Scope(CacheRequest prior):IDisposable {public void Dispose()=>Active=prior;}
    }
    public sealed class AutomationElementCollection(AutomationElement[] values)
    {public int Count=>values.Length;public AutomationElement this[int index]=>values[index];}
    public sealed class AutomationElement
    {
        public static AutomationElement Root;
        public AutomationElement[] Children=Array.Empty<AutomationElement>();
        public string Id="",Name="";
        public ControlType Type=ControlType.Other;
        public bool Seek;
        public Action NameRead,CachedNameRead,MinimumRead,MaximumRead;
        public static readonly object AutomationIdProperty=new(),NameProperty=new(),ControlTypeProperty=new();
        private bool hasCache;
        private string cachedId,cachedName;
        private ControlType cachedType;
        public static AutomationElement FromHandle(IntPtr _)=>Root??throw new Exception("A real window lookup was attempted.");
        public AutomationElementCollection FindAll(TreeScope _,Condition condition)
        {
            Telemetry.Discoveries++;
            var matches=Children.Where(condition.Matches).ToArray();
            Telemetry.QuerySizes.Add(matches.Length);
            if(CacheRequest.Active is {} request)
            {
                request.Validate();
                foreach(var child in matches){child.hasCache=true;child.cachedId=child.Id;child.cachedName=child.Name;child.cachedType=child.Type;}
            }
            return new(matches);
        }
        public AutomationElementInformation Current=>new(this,false);
        public AutomationElementInformation Cached=>hasCache ? new(this,true) : throw new Exception("No selection cache was requested.");
        public AutomationElement GetUpdatedCache(CacheRequest request)
        {
            request.Validate();Telemetry.UpdatedCaches++;NameRead?.Invoke();
            return new AutomationElement {hasCache=true,cachedId=Id,cachedName=Name,cachedType=Type};
        }
        public bool TryGetCurrentPattern(object _,out object pattern){pattern=Seek ? new RangeValuePattern(this) : null;return Seek;}
        public readonly struct AutomationElementInformation(AutomationElement element,bool cached)
        {
            public string AutomationId {get {if(!cached)Telemetry.CurrentProperties++;return cached ? element.cachedId : element.Id;}}
            public string Name {get {if(cached){element.CachedNameRead?.Invoke();return element.cachedName;}Telemetry.CurrentProperties++;element.NameRead?.Invoke();return element.Name;}}
            public ControlType ControlType {get {if(!cached)Telemetry.CurrentProperties++;return cached ? element.cachedType : element.Type;}}
        }
    }
    public sealed class RangeValuePattern(AutomationElement element)
    {
        public static readonly object Pattern=new();
        public RangeValuePatternInformation Current=>new(element);
        public readonly struct RangeValuePatternInformation(AutomationElement element)
        {
            public double Value {get {Telemetry.ValueAt=Telemetry.Now;return 1000;}}
            public double Minimum {get {element.MinimumRead?.Invoke();return 0;}}
            public double Maximum {get {element.MaximumRead?.Invoke();return 60000;}}
        }
    }
}
namespace Popcorn
{
    public static class UiaPlayerControlsTests
    {
        private static void Check(bool value,string message){if(!value)throw new Exception(message);}
        public static void Run()
        {
            var window=new VideoWindow(new IntPtr(1),"fixture",0,0,1280,720);
            var root=new FakeAutomation.AutomationElement();FakeAutomation.AutomationElement.Root=root;
            var text=new FakeAutomation.AutomationElement {Id="TimeText",Name="00:01",Type=FakeAutomation.ControlType.Text};
            var duration=new FakeAutomation.AutomationElement {Name="buffering",Type=FakeAutomation.ControlType.Text};
            var button=new FakeAutomation.AutomationElement {Type=FakeAutomation.ControlType.Button};
            root.Children=new[]{text,duration,button}.Concat(Enumerable.Range(0,64).Select(_=>new FakeAutomation.AutomationElement())).ToArray();FakeAutomation.Telemetry.Now=0;
            FakeAutomation.Telemetry.CurrentProperties=FakeAutomation.Telemetry.CacheActivations=0;
            FakeAutomation.Telemetry.QuerySizes.Clear();
            var selected=UiaPlayerControls.Discover(window,CancellationToken.None);
            Check(FakeAutomation.Telemetry.QuerySizes.Count==1 && FakeAutomation.Telemetry.QuerySizes[0]==3,
                "Standard playback discovery must filter unrelated descendants at the provider.");
            Check(selected.Length==3 && FakeAutomation.Telemetry.CurrentProperties==0 && FakeAutomation.Telemetry.CacheActivations==1 &&
                FakeAutomation.CacheRequest.Active==null,"Discovery must batch selection properties rather than query each current property across processes.");
            text.Name="00:02";
            FakeAutomation.Telemetry.CurrentProperties=FakeAutomation.Telemetry.UpdatedCaches=0;
            Check(UiaPlayerControls.Read(selected[0]).Name=="00:02","Discovery-time cached labels must never become playback evidence.");
            Check(FakeAutomation.Telemetry.CurrentProperties==0 && FakeAutomation.Telemetry.UpdatedCaches==1,
                "Each playback read must obtain one new metadata cache rather than three separate current-property queries.");
            text.Name="00:01";
            var combined=new FakeAutomation.AutomationElement {Name="播放 fixture 第2集 01:23",Type=FakeAutomation.ControlType.Other};
            root.Children=new[]{combined,new FakeAutomation.AutomationElement()};FakeAutomation.Telemetry.QuerySizes.Clear();
            var fallback=UiaPlayerControls.Discover(window,CancellationToken.None);
            Check(fallback.Length==1 && ReferenceEquals(fallback[0],combined) && FakeAutomation.Telemetry.QuerySizes.Count==2,
                "Missing standard progress controls must preserve the complete-tree combined-label fallback.");
            root.Children=new[]{text,duration,button};
            var cache=new PlayerControlCache<FakeAutomation.AutomationElement>(w=>UiaPlayerControls.Discover(w,CancellationToken.None),UiaPlayerControls.Read,()=>FakeAutomation.Telemetry.Now);
            cache.Read(window);duration.Name="00:01:00";button.Name="Pause";FakeAutomation.Telemetry.Now=200;
            var changed=cache.Read(window);
            Check(changed.Any(v=>v.Name=="Pause") && changed.Any(v=>v.Name=="00:01:00"),"A dynamically named state/time control was excluded until periodic discovery.");
            FakeAutomation.Telemetry.Now=0;FakeAutomation.Telemetry.Discoveries=0;
            var old=new FakeAutomation.AutomationElement {Name="Pause",Type=FakeAutomation.ControlType.Button};root.Children=new[]{old};
            cache.Invalidate();cache.Read(window);
            root.Children=new[]{new FakeAutomation.AutomationElement {Name="Play",Type=FakeAutomation.ControlType.Button}};
            FakeAutomation.Telemetry.Now=200;cache.Read(window);FakeAutomation.Telemetry.Now=400;
            var replaced=cache.Read(window);
            Check(replaced.Single().Name=="Play","A readable detached peer refreshed old playing evidence beyond the synchronization response budget.");
            FakeAutomation.Telemetry.Now=0;
            var seek=new FakeAutomation.AutomationElement {Id="SeekBarSlider",Seek=true,NameRead=()=>FakeAutomation.Telemetry.Now+=300,
                MinimumRead=()=>FakeAutomation.Telemetry.Now+=200,MaximumRead=()=>FakeAutomation.Telemetry.Now+=100};
            var measured=UiaPlayerControls.Read(seek);
            Check(measured.Range.Value==1000 && measured.StampMs==FakeAutomation.Telemetry.ValueAt && measured.StampMs<FakeAutomation.Telemetry.Now,
                "A slider measurement was stamped after later slow property reads.");
            FakeAutomation.Telemetry.Now=0;
            root.Children=Enumerable.Range(0,800).Select(_=>new FakeAutomation.AutomationElement {Type=FakeAutomation.ControlType.Text,CachedNameRead=()=>FakeAutomation.Telemetry.Now+=10}).ToArray();
            var bounded=UiaPlayerControls.Discover(window,CancellationToken.None);
            Check(bounded.Length>0 && bounded.Length<=16,"Discovery started additional property reads after the 150ms walk budget.");
            Console.WriteLine("PASS: dynamic candidates, replaced readable peers within 400ms, true slider measurement stamps and bounded discovery.");
        }
    }
}
