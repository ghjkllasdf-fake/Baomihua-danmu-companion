using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Popcorn;

internal sealed class PlayerToolbar : IDisposable
{
    private const uint NoActivate = 0x0010 | 0x0200;
    private const int ExStyle = -20;
    private const long Transparent = 0x20, Layered = 0x80000;
    internal const int EnterFadeMs=250,ExitFadeMs=500;
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    private readonly PlayerToolbarDock dock;
    private readonly PlayerToolbarSheet sheet;
    private readonly int fadeMs;
    private readonly SwitchHint hint;
    private nint player;
    private uint playerPid;
    private Rectangle panelBounds;
    private long outside = -1;
    private long fadeStarted = -1;
    private long revealStarted = -1;
    private double fadeFrom = 1, revealFrom;
    private long switchHoverSince = -1;

    internal bool ButtonsVisible => dock.Visible;
    internal bool IsFading => fadeStarted >= 0;

    internal PlayerToolbar(Action<Action<DanmakuSettings>,bool> edit,int fadeMs)
    {
        this.fadeMs=fadeMs;
        dock=new PlayerToolbarDock(()=>{if(!IsFading)edit(settings=>settings.Enabled=!settings.Enabled,true);},OpenPanel);
        sheet=new PlayerToolbarSheet(edit){Opacity=.90};
        hint=new SwitchHint();
    }

    internal void Observe(DanmakuSettings settings)
    {
        dock.SetEnabledState(settings.Enabled);
        sheet.Observe(settings);
    }

    internal void Update(VideoWindow video, bool? visible, long now)
    {
        if (!visible.HasValue || !PlayerToolbarPlacement.TryResolve(video,PlayerToolbarNativeGeometry.Read,
            out var client,out var dpi,out var buttons,out var popup)) { Hide(); return; }
        Present(video, buttons, popup, visible.Value, now, client, dpi);
    }

    private bool CanFollow(nint target) => target!=0 && !NativeWindows.IsIconic(target) && NativeWindows.IsWindowVisible(target);

    private void Present(VideoWindow video, Rectangle buttons, Rectangle popup, bool visible, long now, Rectangle client, uint dpi)
    {
        nint target=video.Handle;
        if (!CanFollow(target) || NativeWindows.ProcessId(target)!=video.ProcessId) { Hide(); return; }
        if (player != 0 && (target != player || video.ProcessId != playerPid)) Hide();
        player=target;playerPid=video.ProcessId;
        panelBounds = popup;
        if (visible)
        {
            if (IsFading)
            {
                double from = dock.GlyphOpacity;
                ResetDockAppearance(restoreOpacity: false);
                if (fadeMs > 0) { revealStarted = now; revealFrom = from; }
            }
            if (!dock.Visible)
            {
                dock.Bounds = buttons;
                if (fadeMs > 0)
                {
                    revealStarted = now;
                    revealFrom = 0;
                    dock.SetGlyphOpacity(0);
                }
                if (!dock.RefreshGlyphs()) { Hide(); return; }
                dock.Show();
            }
            else if (dock.Bounds != buttons)
            {
                bool resized = dock.Size != buttons.Size;
                dock.Bounds = buttons;
                if (resized && !dock.RefreshGlyphs()) { Hide(); return; }
            }
            if (revealStarted >= 0)
            {
                double amount = Math.Clamp((now - revealStarted) / (double)EnterFadeMs, 0, 1);
                if (!dock.SetGlyphOpacity(revealFrom + (1 - revealFrom) * (1 - Math.Pow(1 - amount, 1.4)))) { Hide(); return; }
                if (amount >= 1) revealStarted = -1;
            }
        }
        else FadeOrHide(now);
        if (sheet.Visible && panelBounds.IsEmpty) ClosePanel();
        PlaceAbovePlayer();
        if (sheet.Visible || (dock.Visible && !IsFading))
        {
            var p = Cursor.Position;
            Rectangle hit = !dock.Visible || IsFading ? Rectangle.Empty :
                dock.GearBounds;
            bool hitsDock = dock.Visible && !IsFading && dock.Bounds.Contains(p) && NativeWindows.IsVideoPointExposed(dock.Handle,dock.Handle,p.X,p.Y);
            if (dock.Visible && !dock.SetHover(hitsDock ? p : null)) { Hide(); return; }
            UpdateSwitchHint(hitsDock && dock.SwitchBounds.Contains(p) && !sheet.Visible, client, dpi, now);
            if (!sheet.Visible)
            {
                if (PanelHover.ShouldOpen(hit, panelBounds, p, hitsDock))
                    OpenPanel();
                return;
            }
            if (sheet.Bounds != panelBounds) sheet.Bounds = panelBounds;
            if (hit.Contains(p) || panelBounds.Contains(p)) outside = -1;
            else if (outside < 0) outside = now;
            if (outside >= 0 && !sheet.Dragging && now - outside >= 180) ClosePanel();
        }
    }

    private void OpenPanel()
    {
        if (!sheet.Visible && dock.Visible && !IsFading && !panelBounds.IsEmpty && CanFollow(player) && NativeWindows.ProcessId(player)==playerPid)
        {
            HideSwitchHint();
            sheet.Bounds = panelBounds;
            sheet.Show();
            outside = -1;
            PlaceAbovePlayer();
        }
    }

    private void UpdateSwitchHint(bool overSwitch, Rectangle client, uint dpi, long now)
    {
        if (!overSwitch) { HideSwitchHint(); return; }
        if (switchHoverSince < 0) switchHoverSince = now;
        if (now - switchHoverSince < SwitchHint.DelayMs) return;
        if (hint.Present(SwitchHint.Position(dock.SwitchBounds, client, dpi), dpi)) PlaceAbovePlayer();
    }

    private void HideSwitchHint()
    {
        switchHoverSince = -1;
        if (hint.Visible) hint.Hide();
    }

    private void PlaceAbovePlayer()
    {
        if ((!dock.Visible && !sheet.Visible) || !CanFollow(player)) return;
        const uint above = 3;
        nint dockHandle = dock.Visible ? dock.Handle : 0, sheetHandle = sheet.Visible ? sheet.Handle : 0;
        nint hintHandle = hint.Visible ? hint.Handle : 0;
        bool topMost=NativeWindows.HasExtendedStyles(player,8);
        foreach(nint handle in new[]{dockHandle,sheetHandle,hintHandle})
        {
            if(handle!=0 && NativeWindows.HasExtendedStyles(handle,8)!=topMost &&
                !SetWindowPos(handle,new IntPtr(topMost ? -1 : -2),0,0,0,0,NoActivate|0x0001|0x0002)){Hide();return;}
        }
        nint nearest = dockHandle != 0 ? dockHandle : sheetHandle;
        nint prior = GetWindow(player, above);
        nint aboveDock = sheetHandle != 0 ? sheetHandle : hintHandle;
        if (prior == nearest && (aboveDock == 0 || aboveDock == nearest || GetWindow(nearest, above) == aboveDock) &&
            (hintHandle == 0 || sheetHandle == 0 || GetWindow(sheetHandle, above) == hintHandle)) return;
        while (prior != 0 && (prior == dockHandle || prior == sheetHandle || prior == hintHandle)) prior = GetWindow(prior, above);
        const uint flags = NoActivate | 0x0001 | 0x0002;
        if (hintHandle != 0 && !SetWindowPos(hintHandle, prior, 0, 0, 0, 0, flags)) { HideSwitchHint(); hintHandle = 0; }
        if (sheetHandle != 0 && !SetWindowPos(sheetHandle, hintHandle != 0 ? hintHandle : prior, 0, 0, 0, 0, flags)) { Hide(); return; }
        if (dockHandle != 0 && !SetWindowPos(dockHandle, sheetHandle != 0 ? sheetHandle : hintHandle != 0 ? hintHandle : prior, 0, 0, 0, 0, flags)) Hide();
    }

    private void ClosePanel() { sheet.Hide(); outside = -1; }
    private void FadeOrHide(long now)
    {
        HideSwitchHint();
        if (!dock.Visible) return;
        if (fadeStarted < 0)
        {
            if (fadeMs <= 0) { HideDock(); return; }
            fadeFrom = dock.GlyphOpacity;
            revealStarted = -1;
            if (!dock.SetHover(null)) { HideDock(); return; }
            fadeStarted = now;
        }
        double progress = (now - fadeStarted) / (double)fadeMs;
        if (progress >= 1) { HideDock(); return; }
        const long passThrough = Layered | Transparent;
        if (!dock.SetGlyphOpacity(fadeFrom * Math.Pow(1 - progress, 1.4))) { HideDock(); return; }
        long styles = GetWindowLongPtr(dock.Handle, ExStyle).ToInt64();
        if ((styles & passThrough) != passThrough)
        {
            SetWindowLongPtr(dock.Handle, ExStyle, (nint)(styles | Transparent));
            if ((GetWindowLongPtr(dock.Handle, ExStyle).ToInt64() & passThrough) != passThrough) HideDock();
        }
    }

    private void ResetDockAppearance(bool restoreOpacity = true)
    {
        if (!IsFading) return;
        long styles = GetWindowLongPtr(dock.Handle, ExStyle).ToInt64();
        SetWindowLongPtr(dock.Handle, ExStyle, (nint)(styles & ~Transparent));
        if (restoreOpacity && !dock.SetGlyphOpacity(1) && dock.Visible) dock.Hide();
        fadeStarted = -1;
    }

    private void HideDock()
    {
        if (dock.Visible) dock.Hide();
        revealStarted = -1;
        ResetDockAppearance();
    }
    internal void Hide() { HideSwitchHint(); HideDock(); ClosePanel(); player=0;playerPid=0; }
    public void Dispose() { Hide(); hint.Dispose(); dock.Dispose(); sheet.Dispose(); }

}
