using System;

namespace Popcorn;

internal readonly record struct VideoNoticeView(string Text,double Opacity)
{
    public bool Visible=>Text.Length>0 && Opacity>0;
}

// Monotonic, ticketed state shared by normal and empty-comment video overlays.
internal sealed class VideoNotice
{
    private long ticket=-1,loadedTicket=-1;
    private string state="",text="";
    private double? fadeAt;
    private readonly Func<bool> animations;
    private bool animate=true;
    internal VideoNotice(Func<bool> animations=null){this.animations=animations??(()=>true);}
    internal bool AwaitingDraw=>state=="loading" && loadedTicket==ticket && fadeAt==null;
    internal void Set(long next,string phase,string message,double now)
    {
        if(next<ticket)return;
        string normalized=phase=="loaded" ? "loading" : phase;
        bool same=next==ticket && normalized==state;
        if(!same){loadedTicket=-1;fadeAt=normalized=="failed" ? now+5000 : null;animate=animations();}
        ticket=next;state=normalized;
        text=state is "matching" or "loading" or "failed" ? message : "";
        if(phase=="loaded")text="已发现弹幕，进行装载";
    }
    internal void ContentLoaded(long contentTicket)
    {
        if(contentTicket==ticket && state=="loading")loadedTicket=contentTicket;
    }
    internal void Rendered(long contentTicket,double now,bool onVideo)
    {
        if(onVideo && state=="loading" && contentTicket==ticket && loadedTicket==ticket && fadeAt==null)
            fadeAt=now+(animate ? 120 : 0);
    }
    internal VideoNoticeView At(double now)
    {
        double opacity=1;
        if(fadeAt is {} start)opacity=animate ? Math.Clamp(1-(now-start)/300,0,1) : (now<start ? 1 : 0);
        string message=text;
        if(state=="failed" && fadeAt is {} until && now<until)
            message+=" · "+Math.Clamp((int)Math.Ceiling((until-now)/1000),1,5)+" s";
        return new(message,opacity);
    }
}
