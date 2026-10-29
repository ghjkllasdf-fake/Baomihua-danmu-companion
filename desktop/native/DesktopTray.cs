using System;
using System.Drawing;
using System.Windows.Forms;

namespace Popcorn;

internal sealed class DesktopTray : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly Icon artwork;
    private bool disposed;
    public DesktopTray(string root,Action open,Action exit)
    {
        menu=new ContextMenuStrip();
        menu.Items.Add("打开工作台",null,(_,_)=>open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出弹幕伴侣",null,(_,_)=>exit());
        artwork=AppIcons.Load(root,true);
        icon=new NotifyIcon {Icon=artwork,Text=Tooltip("",0,"",0),ContextMenuStrip=menu,Visible=true};
        icon.BalloonTipClicked+=(_,_)=>open();
        icon.MouseClick+=(_,e)=>{if(e.Button==MouseButtons.Left)open();};
    }
    private static string Compact(string value,int limit)
    {
        var clean=new System.Text.StringBuilder();
        foreach(char c in value??""){if(!char.IsControl(c))clean.Append(c);}
        string text=clean.ToString().Trim();
        if(text.Length<=limit)return text;
        int end=limit-1;
        if(char.IsHighSurrogate(text[end-1]))end--;
        return text.Substring(0,end)+"…";
    }
    internal static string Tooltip(string title,int episode,string source,int count)
    {
        bool identified=!string.IsNullOrWhiteSpace(title);
        string media=identified ? Compact(title,36)+(episode>0 ? " · 第"+episode+"集" : "") : "等待爆米花播放";
        return "剧集："+media+"\n来源："+(identified && !string.IsNullOrWhiteSpace(source) ? Compact(source,38) : "尚未加载")+
            "\n弹幕："+(identified ? Math.Max(0,count) : 0)+" 条";
    }
    public void Update(string title,int episode,string source,int count)
    {
        if(!disposed){string text=Tooltip(title,episode,source,count);if(icon.Text!=text)icon.Text=text;}
    }
    public void ShowFailure(string message)
    {
        if(disposed)return;
        // Windows delivers the notification according to the user's existing notification policy.
        // https://learn.microsoft.com/dotnet/api/system.windows.forms.notifyicon.showballoontip
        icon.ShowBalloonTip(10000,"弹幕自动匹配需要处理",message,ToolTipIcon.Warning);
    }
    public void Dispose() {if(disposed)return;disposed=true;icon.Visible=false;icon.Dispose();menu.Dispose();artwork.Dispose();}
}
