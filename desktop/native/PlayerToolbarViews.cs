using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Popcorn;

internal static class PlayerToolbarUi
{
    internal static readonly Color Ink=Color.FromArgb(245,245,245), Muted=Color.FromArgb(177,181,183), Panel=Color.FromArgb(24,26,27), Line=Color.FromArgb(51,55,57), Aqua=Color.FromArgb(49,203,231);
    internal static Font Font(float size, bool bold=false) { return new Font("Microsoft YaHei UI",size*4f/3f,bold ? FontStyle.Bold : FontStyle.Regular,GraphicsUnit.Pixel); }
    internal static Button Button(string text, int x, int y, int w, int h, Action onClick)
    {
        var b=new Button { Text=text, AccessibleName=text, Location=new Point(x,y), Size=new Size(w,h), FlatStyle=FlatStyle.Flat, BackColor=Panel, ForeColor=Ink, Margin=Padding.Empty, UseVisualStyleBackColor=false };
        b.FlatAppearance.BorderColor=Line; b.FlatAppearance.MouseOverBackColor=Line;
        b.Click+=delegate { onClick(); };
        return b;
    }
    internal static Label Label(string value, int x, int y, int w, int h, float size, Color color, bool bold=false)
    {
        return new Label { Text=value, Location=new Point(x,y), Size=new Size(w,h), Font=Font(size,bold), ForeColor=color, BackColor=Color.Transparent, TextAlign=ContentAlignment.MiddleLeft };
    }
}

internal sealed class PlayerToolbarSlider : Control
{
    private readonly int min,max;
    private int value;
    internal event Action<int> Changed;
    internal event Action<int,bool> Edit;
    internal event Action DragCancelled;
    private bool dragging;
    internal bool Dragging { get { return Capture; } }
    internal PlayerToolbarSlider(string label, int min, int max, int value, int width)
    {
        this.min=min; this.max=max; this.value=value;
        Size=new Size(width,20); AccessibleName=label; AccessibleRole=AccessibleRole.Slider; TabStop=true;
        SetStyle(ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Selectable,true);
    }
    internal void Set(int n) { value=Math.Clamp(n,min,max); Changed?.Invoke(value); Invalidate(); }
    private void At(int x,bool commit) { double inset=Height*.3;Set((int)Math.Round(min+(max-min)*((x-inset)/Math.Max(1,Width-2*inset))));Edit?.Invoke(value,commit); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics; g.Clear(BackColor); g.SmoothingMode=SmoothingMode.AntiAlias;
        float s=Height/20f, inset=6*s, y=Height/2f, x=inset+(Width-2*inset)*(value-min)/(float)(max-min);
        using(var quiet=new Pen(PlayerToolbarUi.Line,4*s)) using(var live=new Pen(PlayerToolbarUi.Aqua,4*s)) using(var thumb=new SolidBrush(PlayerToolbarUi.Ink))
        { g.DrawLine(quiet,inset,y,Width-inset,y);g.DrawLine(live,inset,y,x,y);g.FillEllipse(thumb,x-5*s,y-5*s,10*s,10*s); }
        if(Focused) ControlPaint.DrawFocusRectangle(g,ClientRectangle);
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if(e.Button==MouseButtons.Left){ Focus();Capture=true;dragging=true;At(e.X,false); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e);if(dragging && Capture)At(e.X,false); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if(e.Button==MouseButtons.Left && dragging){dragging=false;At(e.X,true);Capture=false;}
        base.OnMouseUp(e);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if(dragging && !Capture){dragging=false;DragCancelled?.Invoke();}
    }
    protected override bool IsInputKey(Keys key) { return key==Keys.Left || key==Keys.Right || base.IsInputKey(key); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if(e.KeyCode!=Keys.Left && e.KeyCode!=Keys.Right)return;
        Set(value+(e.KeyCode==Keys.Left ? -1 : 1));Edit?.Invoke(value,true);e.Handled=true;
    }
}

internal sealed class PlayerToolbarSheet : Form
{
    private readonly PlayerToolbarSlider opacity,size,speed;
    private readonly CheckBox[] types=new CheckBox[3];
    private readonly Button[] area=new Button[5];
    private readonly int[] choices={10,25,50,75,100};
    private bool syncing;
    private readonly (Control Control, Rectangle Bounds, Font Font)[] layout;
    private readonly System.Collections.Generic.List<Font> scaledFonts = new();
    private Size layoutSize;
    internal bool Dragging { get { return opacity.Dragging || size.Dragging || speed.Dragging; } }
    internal PlayerToolbarSheet(Action<Action<DanmakuSettings>,bool> edit)
    {
        StartPosition=FormStartPosition.Manual; FormBorderStyle=FormBorderStyle.None;
        ShowInTaskbar=false; AutoScaleMode=AutoScaleMode.None; Font=PlayerToolbarUi.Font(9); BackColor=PlayerToolbarUi.Panel; ForeColor=PlayerToolbarUi.Ink;
        ClientSize=new Size(313,264);
        Controls.Add(PlayerToolbarUi.Label("\u5f39\u5e55\u8bbe\u7f6e",14,9,180,28,12,PlayerToolbarUi.Ink,true));
        var filters=new[]{"\u6eda\u52a8","\u9876\u90e8","\u5e95\u90e8"};
        for(int i=0;i<filters.Length;i++)
        {
            int kind=i;
            var check=new CheckBox { Text=filters[i], Checked=true, AutoSize=false, Location=new Point(16+78*i,44), Size=new Size(72,24), FlatStyle=FlatStyle.Flat, ForeColor=PlayerToolbarUi.Ink, BackColor=PlayerToolbarUi.Panel };
            types[i]=check;
            check.CheckedChanged+=delegate { if(!syncing)edit(s=>{if(kind==0)s.Rolling=check.Checked;else if(kind==1)s.Top=check.Checked;else s.Bottom=check.Checked;},true); };
            Controls.Add(check);
        }
        Controls.Add(PlayerToolbarUi.Label("\u663e\u793a\u533a\u57df",14,76,110,24,9,PlayerToolbarUi.Muted));
        var names=new[]{"1/10","1/4","1/2","3/4","\u5168\u5c4f"};
        for(int i=0;i<area.Length;i++)
        {
            int selected=i;
            area[i]=PlayerToolbarUi.Button(names[i],14+i*58,103,54,26,()=>edit(s=>s.AreaFraction=choices[selected]/100.0,true));
            Controls.Add(area[i]);
        }
        opacity=AddRail("\u4e0d\u900f\u660e\u5ea6",149,10,100,100,v=>v+"%",edit,(s,v)=>s.Opacity=v/100.0);
        size=AddRail("\u5b57\u53f7",186,16,64,22,v=>v+" px",edit,(s,v)=>s.FontSize=v);
        speed=AddRail("\u901f\u5ea6",223,5,20,6,v=>(v/10.0).ToString("0.0",CultureInfo.InvariantCulture)+"\u00d7",edit,(s,v)=>s.SpeedMultiplier=v/10.0);
        layout = new (Control, Rectangle, Font)[Controls.Count];
        for (int i = 0; i < Controls.Count; i++) layout[i] = (Controls[i], Controls[i].Bounds, Controls[i].Font);
    }
    internal void Observe(DanmakuSettings settings)
    {
        syncing=true;
        try
        {
            types[0].Checked=settings.Rolling;types[1].Checked=settings.Top;types[2].Checked=settings.Bottom;
            for(int i=0;i<area.Length;i++)area[i].ForeColor=settings.AreaFraction==choices[i]/100.0 ? PlayerToolbarUi.Aqua : PlayerToolbarUi.Ink;
            opacity.Set((int)Math.Round(settings.Opacity*100));size.Set((int)Math.Round(settings.FontSize));speed.Set((int)Math.Round(settings.SpeedMultiplier*10));
        }
        finally{syncing=false;}
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (layout == null || layoutSize == ClientSize || ClientSize.IsEmpty) return;
        layoutSize = ClientSize;
        float s = Math.Min(layoutSize.Width / 313f, layoutSize.Height / 264f);
        int ScalePixel(int n) => (int)Math.Round(n * s);
        var oldFonts = scaledFonts.ToArray();
        scaledFonts.Clear();
        SuspendLayout();
        foreach (var (control, bounds, original) in layout)
        {
            var font = new Font(original.FontFamily, original.Size * s, original.Style, original.Unit);
            scaledFonts.Add(font);
            control.Font = font;
            control.SetBounds(ScalePixel(bounds.X), ScalePixel(bounds.Y), ScalePixel(bounds.Width), ScalePixel(bounds.Height));
        }
        ResumeLayout(false);
        foreach (var font in oldFonts) font.Dispose();
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) foreach (var font in scaledFonts) font.Dispose();
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p=base.CreateParams;p.ExStyle|=0x08000000|0x80;return p; } }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using(var pen=new Pen(PlayerToolbarUi.Line))e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1); }
    private PlayerToolbarSlider AddRail(string name,int y,int min,int max,int value,Func<int,string> format,Action<Action<DanmakuSettings>,bool> edit,Action<DanmakuSettings,int> update)
    {
        Controls.Add(PlayerToolbarUi.Label(name,14,y,70,25,9,PlayerToolbarUi.Ink));
        var readout=PlayerToolbarUi.Label(format(value),249,y,50,25,9,PlayerToolbarUi.Muted); readout.TextAlign=ContentAlignment.MiddleRight;Controls.Add(readout);
        var slider=new PlayerToolbarSlider(name,min,max,value,159){Location=new Point(84,y+2),BackColor=PlayerToolbarUi.Panel}; Controls.Add(slider);
        slider.Changed+=v=>readout.Text=format(v);
        slider.Edit+=(v,complete)=>edit(s=>update(s,v),complete);
        slider.DragCancelled+=()=>edit(null,false);
        return slider;
    }
}

internal sealed class PlayerToolbarDock : Form
{
    internal readonly Button Switch, Settings;
    private readonly Action toggleAction, openAction;
    private bool enabled=true;
    private byte glyphAlpha=255;
    internal double GlyphOpacity => glyphAlpha/255.0;
    private int pressed=-1;
    private int hovered=-1;
    internal PlayerToolbarDock(Action toggle,Action open)
    {
        toggleAction=toggle; openAction=open;
        StartPosition=FormStartPosition.Manual; ShowInTaskbar=false; FormBorderStyle=FormBorderStyle.None; AutoScaleMode=AutoScaleMode.None;
        ClientSize=new Size(88,34); BackColor=PlayerToolbarUi.Panel; Font=PlayerToolbarUi.Font(9);
        Switch=PlayerToolbarUi.Button("",0,0,44,34,toggle); Settings=PlayerToolbarUi.Button("",44,0,44,34,open);
        Settings.AccessibleName="\u5f39\u5e55\u8bbe\u7f6e";
        SetEnabledState(true);
        FitButtons();
    }
    internal void SetEnabledState(bool enabled)
    {
        bool changed=this.enabled!=enabled;
        this.enabled=enabled;
        Switch.AccessibleName=enabled ? "\u5f39\u5e55\u5df2\u5f00\u542f" : "\u5f39\u5e55\u5df2\u5173\u95ed";
        if(changed && Visible) RefreshGlyphs();
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if(Settings!=null)FitButtons(); }
    private void FitButtons()
    {
        int half=ClientSize.Width/2;
        Switch.SetBounds(0,0,half,ClientSize.Height);
        Settings.SetBounds(half,0,ClientSize.Width-half,ClientSize.Height);
    }
    internal void PaintButton(Graphics g,Button button,bool settings,bool enabled)
    {
        float s=Math.Min(button.Width/40f,button.Height/34f)*.65f;
        var state=g.Save();
        // Center the visible outline, not the padded drawing canvas.
        float inkCenterY=settings || enabled ? 16.4f : 17.15f;
        g.TranslateTransform((button.Width-44*s)/2,button.Height/2f-inkCenterY*s);
        g.ScaleTransform(s,s);
        PaintGlyph(g,settings,enabled);
        g.Restore(state);
    }
    internal Rectangle GearBounds => new(Left+Settings.Left,Top+Settings.Top,Settings.Width,Settings.Height);
    internal Rectangle SwitchBounds => new(Left+Switch.Left,Top+Switch.Top,Switch.Width,Switch.Height);
    internal Bitmap RenderInlineGlyphs(int? hoverSlot=null)
    {
        var bitmap=new Bitmap(ClientSize.Width,ClientSize.Height,System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics=Graphics.FromImage(bitmap);
        // A one-alpha input surface keeps the full button area clickable without a visible fill.
        graphics.Clear(Color.FromArgb(1,0,0,0));
        int hover=hoverSlot ?? hovered;
        if(hover is 0 or 1)
        {
            var button=hover==0 ? Switch : Settings;
            float scale=ClientSize.Height/34f;
            var fillBounds=RectangleF.Inflate(button.Bounds,-3*scale,-2*scale);
            using var brush=new SolidBrush(Color.FromArgb(0x5f,0x48,0x48,0x48));
            using var shape=new GraphicsPath();
            shape.AddRoundedRectangle(fillBounds,new SizeF(4*scale,4*scale));
            graphics.SmoothingMode=SmoothingMode.AntiAlias;
            graphics.FillPath(brush,shape);
        }
        PaintButton(graphics,Switch,false,enabled);
        graphics.TranslateTransform(Settings.Left,0);
        PaintButton(graphics,Settings,true,false);
        return bitmap;
    }
    internal bool RefreshGlyphs()
    {
        using var bitmap=RenderInlineGlyphs();
        return LayeredGlyphs.Present(Handle,bitmap,glyphAlpha);
    }
    internal bool SetGlyphOpacity(double opacity)
    {
        byte alpha=(byte)Math.Round(opacity*255);
        if(glyphAlpha==alpha) return true;
        glyphAlpha=alpha;
        return !Visible || RefreshGlyphs();
    }
    internal bool SetHover(Point? screenPoint)
    {
        int slot=screenPoint.HasValue ? Slot(PointToClient(screenPoint.Value)) : -1;
        if(slot==hovered) return true;
        hovered=slot;
        return !Visible || RefreshGlyphs();
    }
    private int Slot(Point point) => !ClientRectangle.Contains(point) ? -1 : point.X<Settings.Left ? 0 : 1;
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if(e.Button==MouseButtons.Left) pressed=Slot(e.Location);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if(e.Button!=MouseButtons.Left) return;
        int slot=pressed;
        pressed=-1;
        if(slot<0 || slot!=Slot(e.Location)) return;
        if(slot==0) toggleAction(); else openAction();
    }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if(!Visible){pressed=-1;hovered=-1;} }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p=base.CreateParams;p.ExStyle|=0x08080080;return p; } }
    protected override void Dispose(bool disposing) { if(disposing){Switch.Dispose();Settings.Dispose();}base.Dispose(disposing); }
    internal static void PaintGlyph(Graphics g,bool settings,bool enabled)
    {
        g.SmoothingMode=SmoothingMode.AntiAlias;
        using(var pen=new Pen(PlayerToolbarUi.Ink,1.55f)) using(var suffix=new Pen(enabled ? PlayerToolbarUi.Aqua : PlayerToolbarUi.Ink,1.8f)) using(var fill=new SolidBrush(PlayerToolbarUi.Ink))
        {
            pen.StartCap=pen.EndCap=suffix.StartCap=suffix.EndCap=LineCap.Round;
            g.DrawLines(pen,new[]{new Point(30,18),new Point(30,9),new Point(11,9),new Point(11,25),new Point(22,25)});
            g.DrawLines(pen,new[]{new Point(17,5),new Point(21,9),new Point(25,5)});
            g.DrawLine(pen,16,15,24,15);g.DrawLine(pen,16,19,21,19);
            if(settings){g.DrawEllipse(pen,24,18,9,9);g.FillEllipse(fill,27,21,3,3);}
            else if(enabled)g.DrawLines(suffix,new[]{new Point(23,23),new Point(27,27),new Point(34,19)});
            else { g.DrawLine(suffix,25,20,33,28);g.DrawLine(suffix,25,28,33,20); }
        }
    }
}
