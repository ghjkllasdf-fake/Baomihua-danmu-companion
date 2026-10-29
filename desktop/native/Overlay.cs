using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Popcorn;

public sealed record OverlaySnapshot(bool Visible,double PositionMs,bool Running,int ActiveCount,int CacheEntries,double FontPx,double AreaHeight,double ViewportWidthPx,double ViewportHeightPx,string[] Text,double[] X,double[] Y,double[] Progress,bool ClickThrough=false,bool NoActivate=false,long SpriteBytes=0,long SpritesCreated=0,long RejectedSprites=0,int? OverlayXpx=null,int? OverlayYpx=null,long GeometryBuilds=0,bool LayoutPending=false);

public sealed class OverlayHost : IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready=new();
    private OverlayWindow window;
    private Exception failure;
    public OverlayHost(bool benchmark=false)
    {
        thread=new Thread(()=>{
            try { window=new OverlayWindow(benchmark); }
            catch (Exception ex) { failure=ex; }
            finally { ready.Set(); }
            if (failure==null) Dispatcher.Run();
        }) { IsBackground=true, Name="Popcorn native renderer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!ready.Wait(5000)) throw new InvalidOperationException("原生弹幕窗口启动超时。");
        if (failure!=null) throw new InvalidOperationException("原生弹幕窗口启动失败。",failure);
    }
    private void Post(Action action) { if (!window.Dispatcher.HasShutdownStarted) window.Dispatcher.BeginInvoke(action); }
    public void SetComments(Comment[] comments)=>SetComments(comments,-1);
    public void SetComments(Comment[] comments,long ticket)=>Post(()=>window.Surface.Load(comments,ticket));
    public void SetStatus(long ticket,string state,string message)=>Post(()=>{window.Surface.SetStatus(ticket,state,message);window.Follow();});
    public void SetSettings(DanmakuSettings settings)=>Post(()=>window.Surface.Configure(settings));
    public void Update(VideoWindow video,double position,bool running,int revision,bool automatic,double proofStamp,bool allowComments=true)
    {
        double stamp=Monotonic.Now;
        Post(()=>{ window.Surface.AllowComments=allowComments;window.Surface.UpdateClock(position,running,stamp,revision,automatic,proofStamp); window.Target=video; window.Follow(); });
    }
    public double[] FrameIntervals()=>window.Dispatcher.Invoke(()=>window.Surface.Intervals.ToArray());
    public int MaxActive()=>window.Dispatcher.Invoke(()=>window.Surface.MaxActive);
    public string CompositionMode=>window.Dispatcher.Invoke(()=>window.CompositionMode);
    public OverlaySnapshot Snapshot()=>window.Dispatcher.Invoke(window.Snapshot,DispatcherPriority.Background);
    public void Capture(string path)=>window.Dispatcher.Invoke(()=>{
        var bitmap=new RenderTargetBitmap((int)window.Surface.ActualWidth,(int)window.Surface.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(window.Surface); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path); encoder.Save(stream);
    });
    public void Dispose()
    {
        if (window==null) return;
        Post(()=>{ window.Surface.Detach(); window.Close(); window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); });
        thread.Join(1500); ready.Dispose();
    }
}

internal sealed class OverlayWindow
{
    public readonly OverlaySurface Surface=new();
    public VideoWindow Target;
    private readonly IntPtr handle;
    private readonly OverlayPresentation presentation;
    public Dispatcher Dispatcher=>Surface.Dispatcher;
    public bool IsVisible=>presentation.IsVisible;
    public string CompositionMode=>presentation.Mode;
    private readonly DispatcherTimer framePump;
    private VideoWindow lastBounds;
    private DispatcherOperation pendingWork;
    private readonly Action runFrameWork;
    private bool closed;
    private TimeSpan lastRenderTarget=TimeSpan.MinValue;
    public OverlayWindow(bool benchmark)
    {
        Surface.IsHitTestVisible=false;
        presentation=OverlayPresentation.Create(Surface);handle=presentation.Handle;
        Surface.IsPointExposed=(x,y)=>Target!=null && lastBounds!=null && Surface.ActualWidth>0 && Surface.ActualHeight>0 &&
            NativeWindows.IsVideoPointExposed(handle,Target.Handle,lastBounds.X+(int)(x*lastBounds.Width/Surface.ActualWidth),
                lastBounds.Y+(int)(y*lastBounds.Height/Surface.ActualHeight));
        runFrameWork=RunFrameWork;
        framePump=new DispatcherTimer(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(16) };
        framePump.Tick+=(_,_)=>{ if (IsVisible) Surface.InvalidateVisual(); };
        framePump.Start();
        CompositionTarget.Rendering+=Rendering;
    }
    public void Close() { CancelFrameWork();framePump.Stop();CompositionTarget.Rendering-=Rendering;presentation.Dispose(); }
    private bool Show(VideoWindow video)=>presentation.Show(video);
    private void Hide()=>presentation.Hide();
    private void Rendering(object sender,EventArgs args)
    {
        Follow();if(!IsVisible)return;
        var target=((RenderingEventArgs)args).RenderingTime;
        bool nextTarget=target!=lastRenderTarget;
        if(!Surface.FrameTick(nextTarget))return;
        if(!nextTarget)return;
        lastRenderTarget=target;QueueFrameWork();
    }
    private void QueueFrameWork()
    {
        if(closed || pendingWork!=null || !Surface.NeedsWork)return;
        pendingWork=Dispatcher.BeginInvoke(DispatcherPriority.Background,runFrameWork);
    }
    private void RunFrameWork()
    {
        try {
            if(closed)return;
            if (IsVisible) Surface.StepFrame();
        } finally {pendingWork=null;}
    }
    private void CancelFrameWork()
    {
        closed=true;pendingWork?.Abort();pendingWork=null;
    }
    public OverlaySnapshot Snapshot()
    {
        var bounds=NativeWindows.Read(handle);
        return Surface.Snapshot(IsVisible) with { ClickThrough=NativeWindows.HasExtendedStyles(handle,0x80020),NoActivate=NativeWindows.HasExtendedStyles(handle,0x08000000),OverlayXpx=bounds?.X,OverlayYpx=bounds?.Y };
    }
    public void Follow()
    {
        bool visible=Target!=null && (Surface.HasComments && Surface.AllowComments || Surface.HasNotice) && Surface.Enabled && NativeWindows.IsWindowVisible(Target.Handle) &&
            !NativeWindows.IsIconic(Target.Handle) && Target.ProcessId!=0 && NativeWindows.ProcessId(Target.Handle)==Target.ProcessId;
        Surface.VideoVisible=visible;
        if (!visible) { if (IsVisible) Hide(); return; }
        var bounds=NativeWindows.Read(Target.Handle);
        if (bounds==null || bounds.Title!=Target.Title || bounds.Width<100 || bounds.Height<100) { Surface.VideoVisible=false;Hide(); return; }
        bool moved=lastBounds==null || bounds.X!=lastBounds.X || bounds.Y!=lastBounds.Y || bounds.Width!=lastBounds.Width || bounds.Height!=lastBounds.Height;
        if(moved)
        {
            presentation.ResizeContent(bounds.Width,bounds.Height);lastBounds=bounds;
        }
        if(!IsVisible)
        {Surface.Rebuild(true);if(!Show(bounds)){Surface.VideoVisible=false;Hide();}}
        else if(!NativeWindows.FollowCompanion(handle,bounds,moved)){Surface.VideoVisible=false;Hide();}
    }
}

internal sealed class OverlaySurface : FrameworkElement
{
    private sealed record Bullet(Comment Comment,int Lane,Sprite Sprite,Motion Motion) { public double Width=>Sprite.Width; }
    private DanmakuSettings settings=new();
    private DanmakuSettings layoutSettings=new();
    private readonly PlaybackClock clock=new();
    private CommentQueue queue=new(Array.Empty<Comment>());
    private readonly List<Bullet> active=new();
    private readonly SpriteStore shapes;
    private readonly Dictionary<int,Brush> colors=new();
    private readonly Dictionary<string,double> recent=new(StringComparer.OrdinalIgnoreCase);
    private string[] keywords=Array.Empty<string>();
    private int revision=-1, cursor;
    private bool rebuild=true, preserveMotion, preserveDisplay, automatic;
    private List<Bullet> preparing;
    private double proofStamp,lastFrame,lastWidth,lastHeight;
    private long oversized,geometryBuilds;
    private IEnumerator<int> pendingFrame;
    private int generation;
    private bool detached,stepping;
    private readonly VideoNotice notice=new();
    private long contentTicket=-1;
    private FormattedText noticeText;
    private string noticeLabel="";
    private double noticeWidth,noticeDpi;
    public bool VideoVisible {get;set;}
    public bool AllowComments {get;set;}=true;
    public Func<double,double,bool> IsPointExposed {get;set;}
    public bool HasNotice=>notice.At(Monotonic.Now).Visible;
    public bool HasPendingFrame=>pendingFrame!=null;
    public bool NeedsWork=>!detached && (rebuild || pendingFrame!=null || queue.HasDue(Position));
    public bool HasComments { get; private set; }
    public bool Enabled=>settings.Enabled;
    public int MaxActive { get; private set; }
    public readonly List<double> Intervals=new();
    public OverlaySurface() : this(SpriteLimits.MaxResidentBytes) { }
    public OverlaySurface(long spriteBudget) { shapes=new(spriteBudget); }
    private void ReleaseActive()
    {
        foreach (var bullet in active) shapes.Release(bullet.Sprite);
        active.Clear();
    }
    public void Load(Comment[] comments,long ticket=-1)
    {
        CancelFrame(); detached=false;
        var display=comments.Select(c=>c with { Text=DanmakuText.SingleLine(c.Text) }).Where(c=>c.Text.Length>0).ToArray();
        ReleaseActive();
        if (display.Length==0) shapes.ClearUnused();
        queue=new(display); HasComments=display.Length>0; Rebuild();
        contentTicket=ticket;
        if(HasComments)notice.ContentLoaded(ticket);
    }
    public void SetStatus(long ticket,string state,string message)
    {notice.Set(ticket,state,message,Monotonic.Now);InvalidateVisual();}
    private void ReleasePreparing()
    {
        if(preparing==null) return;
        foreach(var bullet in preparing) shapes.Release(bullet.Sprite);
        preparing.Clear();preparing=null;shapes.ClearUnused();
    }
    private void CancelFrame() { generation++; pendingFrame?.Dispose(); pendingFrame=null;ReleasePreparing(); }
    public void Rebuild(bool preserve=false) { CancelFrame(); preserveMotion=rebuild ? preserveMotion && preserve : preserve;preserveDisplay=preserve; rebuild=true; }
    public void Configure(DanmakuSettings next)
    {
        next.Validate(); double now=Position;
        bool layout=pendingFrame!=null || next.AreaFraction!=settings.AreaFraction || next.FontSize!=settings.FontSize || next.ScaleWithWindow!=settings.ScaleWithWindow ||
            next.Rolling!=settings.Rolling || next.Top!=settings.Top || next.Bottom!=settings.Bottom || next.Keywords!=settings.Keywords ||
            next.OffsetMs!=settings.OffsetMs || next.Dedupe!=settings.Dedupe || next.Density!=settings.Density || next.OutlineWidth!=settings.OutlineWidth;
        if (next.SpeedMultiplier!=settings.SpeedMultiplier)
            foreach (var b in active) b.Motion.Retime(now,8000/next.SpeedMultiplier);
        bool sameOffset=next.OffsetMs==settings.OffsetMs;
        bool sameContent=sameOffset && next.Rolling==settings.Rolling && next.Top==settings.Top && next.Bottom==settings.Bottom &&
            next.Keywords==settings.Keywords && next.Dedupe==settings.Dedupe && next.Density==settings.Density;
        settings=next;
        keywords=(next.Keywords??"").Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if (layout) { shapes.ClearUnused(); Rebuild(sameOffset);preserveDisplay=sameContent; }
        InvalidateVisual();
    }
    private double Position=>clock.At(Monotonic.Now)-settings.OffsetMs;
    public OverlaySnapshot Snapshot(bool visible)
    {
        double width=ActualWidth,height=ActualHeight,position=Position;
        double scale=width>0 && height>0 ? LayoutMath.Scale(width,height,layoutSettings.ScaleWithWindow) : 1;
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double line=layoutSettings.FontSize*scale*1.4+4*scale;
        return new(visible,position,clock.Running,active.Count,shapes.Count,layoutSettings.FontSize*scale*dpi,height*settings.AreaFraction*dpi,width*dpi,height*dpi,
            active.Select(b=>b.Comment.Text).ToArray(),active.Select(b=>width-b.Motion.Progress(position)*(width+b.Width)).ToArray(),
            active.Select(b=>6*scale+b.Lane*line).ToArray(),active.Select(b=>b.Motion.Progress(position)).ToArray(),
            SpriteBytes:shapes.ResidentBytes,SpritesCreated:shapes.Created,RejectedSprites:oversized+shapes.Rejected,GeometryBuilds:geometryBuilds,LayoutPending:!detached&&(rebuild||pendingFrame!=null));
    }
    public void UpdateClock(double position,bool running,double stamp,int nextRevision,bool isAutomatic,double proof)
    {
        if (revision!=nextRevision)
        {
            revision=nextRevision; Rebuild(); ReleaseActive(); InvalidateVisual();
        }
        clock.Set(position,running,stamp); automatic=isAutomatic; proofStamp=proof;
    }
    public void Frame()
    {
        if(stepping) return;
        stepping=true;
        try {
            CancelFrame();
            if(!PrepareFrame()) return;
            do {
                using var work=BuildFrame(generation).GetEnumerator();
                while(work.MoveNext()) { }
            } while(rebuild && !detached);
        } finally {stepping=false;}
    }
    public void StepFrame()
    {
        if(stepping) return;
        stepping=true;
        try {
            if(!PrepareFrame()) return;
            pendingFrame??=BuildFrame(generation).GetEnumerator();
            var work=pendingFrame;
            if(!work.MoveNext() && ReferenceEquals(work,pendingFrame))
            {work.Dispose();pendingFrame=rebuild ? BuildFrame(generation).GetEnumerator() : null;}
            InvalidateVisual();
        } catch {CancelFrame();throw;}
        finally {stepping=false;}
    }
    public bool FrameTick(bool invalidate)
    {
        if(stepping || detached)return false;
        double now=Monotonic.Now;
        if(lastFrame>0 && Intervals.Count<20000) Intervals.Add(now-lastFrame);
        lastFrame=now;
        if(PrepareFrame() && invalidate) InvalidateVisual();
        return true;
    }
    private bool PrepareFrame()
    {
        if(detached) return false;
        double now=Monotonic.Now;
        if (automatic && now-proofStamp>1500 && clock.Running) clock.Set(clock.At(proofStamp+1500),false,now);
        double width=ActualWidth,height=ActualHeight;
        if (width<1 || height<1) {CancelFrame();return false;}
        if (width!=lastWidth || height!=lastHeight) { shapes.ClearUnused(); Rebuild(true); lastWidth=width; lastHeight=height; }
        double position=Position;
        for (int i=active.Count-1;i>=0;i--)
            if (active[i].Motion.Progress(position)<0 || active[i].Motion.Progress(position)>1)
            { shapes.Release(active[i].Sprite); active.RemoveAt(i); }
        return true;
    }
    private IEnumerable<int> BuildFrame(int version)
    {
        double width=ActualWidth,height=ActualHeight,position=Position;
        Dictionary<Comment,Motion> retained=null;
        List<Bullet> target=active;
        bool replacing=rebuild;
        double ageWindow=Math.Max(4500,8000/settings.SpeedMultiplier);
        if (rebuild)
        {
            if (preserveMotion)
            {
                retained=new(); ageWindow=16000;
                foreach (var bullet in active) retained.TryAdd(bullet.Comment,bullet.Motion);
            }
            if(!preserveDisplay) {ReleaseActive();layoutSettings=settings;}
            preparing=new();target=preparing;
            // Only already visible comments are reconstructed. A new source or seek starts at
            // the current position instead of filling the screen with a historical batch.
            recent.Clear(); cursor=0; queue.Seek(position,retained?.Count>0 ? ageWindow : 0); rebuild=false; preserveMotion=false;preserveDisplay=false;
        }
        double scale=LayoutMath.Scale(width,height,settings.ScaleWithWindow), font=settings.FontSize*scale, line=font*1.4+4*scale;
        int lanes=Math.Max(1,(int)Math.Floor((height*settings.AreaFraction-12*scale)/line));
        int scanned=0;
        foreach (var comment in queue.TakeDue(position,ageWindow))
        {
            if(version!=generation || detached) yield break;
            if(++scanned%16==0) {yield return 0;if(version!=generation || detached) yield break;}
            position=Position;
            bool fixedTop=comment.Mode==5, fixedBottom=comment.Mode==4;
            if ((!settings.Top && fixedTop) || (!settings.Bottom && fixedBottom) || (!settings.Rolling && !fixedTop && !fixedBottom) ||
                target.Count>=settings.Density || keywords.Any(k=>comment.Text.Contains(k,StringComparison.OrdinalIgnoreCase))) continue;
            double duration=8000/settings.SpeedMultiplier;
            Motion motion;
            bool existing=retained!=null && retained.TryGetValue(comment,out _);
            if(!existing && comment.TimeMs<position-250)continue;
            if (retained==null || !retained.TryGetValue(comment,out motion)) motion=new Motion(position,duration);
            if (motion.Progress(position)>1 || motion.Progress(position)<0) continue;
            string normalized=comment.Text;
            if (settings.Dedupe && recent.TryGetValue(normalized,out double previous) && comment.TimeMs-previous<10000) continue;
            double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
            string shapeKey=font.ToString("R",CultureInfo.InvariantCulture)+"|"+dpi.ToString("R",CultureInfo.InvariantCulture)+"|"+(settings.OutlineWidth*scale).ToString("R",CultureInfo.InvariantCulture)+"|"+comment.Color+"|"+comment.Text;
            Geometry geometry=null; double spriteWidth=0,spriteHeight=0,pad=0; RasterSize raster=default;
            var shape=shapes.Find(shapeKey);
            if (shape==null)
            {
                var text=new FormattedText(comment.Text,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),font,Brushes.White,dpi);
                geometry=text.BuildGeometry(new Point(0,0));
                geometryBuilds++;
                yield return 0;
                if(version!=generation || detached) yield break;
                position=Position;
                if(motion.Progress(position)>1 || motion.Progress(position)<0) continue;
                if (geometry.Bounds.IsEmpty) continue;
                pad=settings.OutlineWidth*scale+2; spriteWidth=Math.Ceiling(geometry.Bounds.Width+pad*2); spriteHeight=Math.Ceiling(geometry.Bounds.Height+pad*2);
                if (!SpriteLimits.TryMeasure(spriteWidth,spriteHeight,dpi,out raster)) { oversized++; continue; }
            }
            double textWidth=shape?.Width??spriteWidth, speed=(width+textWidth)/(duration/1000);
            double newX=existing ? width-motion.Progress(position)*(width+textWidth) : width;
            int lane=-1;
            for (int j=0;j<lanes;j++)
            {
                int candidate=(cursor+j)%lanes;
                bool safe=true;
                foreach (var old in target.Where(b=>b.Lane==candidate))
                {
                    double oldX=replacing && (retained==null || !retained.ContainsKey(old.Comment)) ? width : width-old.Motion.Progress(position)*(width+old.Width);
                    double oldSpeed=(width+old.Width)/(old.Motion.DurationMs/1000);
                    if (!LayoutMath.CanFollow(oldX,old.Width,oldSpeed,speed,newX,20*scale)) { safe=false; break; }
                }
                if (safe) { lane=candidate; break; }
            }
            if (lane<0) continue;
            if (shape==null)
            {
                shape=shapes.Create(shapeKey,spriteWidth,spriteHeight,raster,()=>
                {
                    if (!colors.TryGetValue(comment.Color,out var brush)) { if (colors.Count>=512) colors.Clear(); brush=new SolidColorBrush(Color.FromRgb((byte)(comment.Color>>16),(byte)(comment.Color>>8),(byte)comment.Color)); brush.Freeze(); colors[comment.Color]=brush; }
                    var visual=new DrawingVisual();
                    using (var dc=visual.RenderOpen())
                    {
                        dc.PushTransform(new TranslateTransform(pad-geometry.Bounds.Left,pad-geometry.Bounds.Top));
                        dc.DrawGeometry(brush,settings.OutlineWidth>0 ? new Pen(Brushes.Black,settings.OutlineWidth*scale) : null,geometry); dc.Pop();
                    }
                    var bitmap=new RenderTargetBitmap(raster.Width,raster.Height,96*dpi,96*dpi,PixelFormats.Pbgra32);
                    bitmap.Render(visual); return bitmap;
                });
                if (shape==null)
                {
                    if(replacing && active.Count>0) {ReleasePreparing();rebuild=true;preserveMotion=true;preserveDisplay=false;yield break;}
                    continue;
                }
                if(version!=generation || detached) {shapes.ClearUnused();yield break;}
            }
            if(!existing)motion=new Motion(Position,duration);
            cursor=(lane+1)%lanes;
            shapes.Pin(shape); target.Add(new(comment,lane,shape,motion)); recent[normalized]=comment.TimeMs;
            yield return 0;
        }
        if(replacing)
        {
            double current=Position;
            for(int i=target.Count-1;i>=0;i--)
                if(retained==null || !retained.ContainsKey(target[i].Comment))
                    target[i]=target[i] with {Motion=new Motion(current,target[i].Motion.DurationMs)};
                else if(target[i].Motion.Progress(current)<0 || target[i].Motion.Progress(current)>1)
                {shapes.Release(target[i].Sprite);target.RemoveAt(i);}
            preparing=null;ReleaseActive();active.AddRange(target);target.Clear();layoutSettings=settings;
        }
        if (recent.Count>1000) foreach (var key in recent.Where(p=>position-p.Value>10000).Select(p=>p.Key).ToArray()) recent.Remove(key);
        MaxActive=Math.Max(MaxActive,active.Count);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); double width=ActualWidth,height=ActualHeight,position=Position;
        if (width<=0 || height<=0) return;
        double scale=LayoutMath.Scale(width,height,layoutSettings.ScaleWithWindow),line=layoutSettings.FontSize*scale*1.4+4*scale;
        dc.PushClip(new RectangleGeometry(new Rect(0,0,width,Math.Max(0,height*settings.AreaFraction))));
        bool rendered=false;
        foreach (var b in AllowComments ? active : Enumerable.Empty<Bullet>())
        {
            double x=width-b.Motion.Progress(position)*(width+b.Width);
            double y=6*scale+b.Lane*line;
            // Text and outline share this bitmap; brush alpha avoids a full-overlay opacity layer.
            dc.DrawRectangle(b.Sprite.AlphaBrush(settings.Opacity),null,new Rect(x,y,b.Sprite.Width,b.Sprite.Height));
            double left=Math.Max(0,x),right=Math.Min(width,x+b.Sprite.Width),bottom=Math.Min(height*settings.AreaFraction,y+b.Sprite.Height);
            if(!rendered && notice.AwaitingDraw && VideoVisible && settings.Opacity>0 && right>left && bottom>y &&
                IsPointExposed?.Invoke((left+right)/2,(y+bottom)/2)==true)rendered=true;
        }
        dc.Pop();
        notice.Rendered(contentTicket,Monotonic.Now,rendered);
        DrawNotice(dc,width);
    }
    private void DrawNotice(DrawingContext dc,double width)
    {
        var view=notice.At(Monotonic.Now);
        if(!view.Visible || width<64)return;
        double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip,maxWidth=Math.Min(320,width-48);
        if(noticeText==null || noticeLabel!=view.Text || noticeWidth!=maxWidth || noticeDpi!=dpi)
        {
            noticeLabel=view.Text;noticeWidth=maxWidth;noticeDpi=dpi;
            noticeText=new FormattedText(view.Text,System.Globalization.CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),13,Brushes.White,dpi) {MaxTextWidth=maxWidth,MaxTextHeight=60,Trimming=TextTrimming.CharacterEllipsis};
        }
        double boxWidth=noticeText.Width+24,boxHeight=noticeText.Height+16,x=width-boxWidth-12;
        dc.PushOpacity(view.Opacity);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220,35,39,34)),null,new Rect(x,12,boxWidth,boxHeight),6,6);
        dc.DrawText(noticeText,new Point(x+12,20));dc.Pop();
    }
    public void Detach() { CancelFrame(); detached=true;ReleaseActive(); shapes.ClearUnused(); colors.Clear(); recent.Clear();HasComments=false; }
}
