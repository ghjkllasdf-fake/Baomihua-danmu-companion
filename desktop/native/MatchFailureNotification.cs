using System;
using System.Drawing;
using System.Windows.Forms;

namespace Popcorn;

internal sealed class MatchFailureNotification : IDisposable
{
    private readonly Form owner;
    private readonly NotifyIcon icon;
    public MatchFailureNotification(Form owner)
    {
        this.owner=owner;
        icon=new NotifyIcon {Icon=SystemIcons.Information,Text="爆米花弹幕伴侣"};
        icon.BalloonTipClicked+=(_,_)=>Open();
        icon.MouseClick+=(_,e)=>{if(e.Button==MouseButtons.Left)Open();};
    }
    private void Open()
    {
        if(owner.IsDisposed)return;
        owner.Show();
        if(owner.WindowState==FormWindowState.Minimized)owner.WindowState=FormWindowState.Normal;
        owner.Activate();
    }
    public void Show(string message)
    {
        icon.Visible=true;
        // Windows delivers the notification according to the user's existing notification policy.
        // https://learn.microsoft.com/dotnet/api/system.windows.forms.notifyicon.showballoontip
        icon.ShowBalloonTip(10000,"弹幕自动匹配需要处理",message,ToolTipIcon.Warning);
    }
    public void Dispose() {icon.Visible=false;icon.Dispose();}
}
