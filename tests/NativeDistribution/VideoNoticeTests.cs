using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace Popcorn;

internal static class VideoNoticeTests
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    private sealed class Target : Form {protected override bool ShowWithoutActivation=>true;}
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run()
    {
        var notice=new VideoNotice();
        notice.Set(1,"matching","search",100);
        Check(notice.At(100).Visible,"Search must be visible without any loaded comments.");
        notice.Set(1,"loading","load",200);
        Check(notice.At(99999).Opacity==1,"HTTP completion alone cannot dismiss loading.");
        notice.ContentLoaded(1);
        notice.Rendered(0,100000,true);
        Check(notice.At(100000).Opacity==1,"A stale content ticket cannot dismiss the current operation.");
        notice.Rendered(1,100000,false);
        Check(notice.At(100000).Opacity==1,"Offscreen rendering cannot dismiss loading.");
        notice.Rendered(1,100000,true);
        Check(notice.At(100001).Visible && !notice.At(100500).Visible,"The first on-video draw starts a finite success fade.");
        notice.Set(2,"failed","not found",200000);
        Check(notice.At(200000).Text.Contains("5"),"Final failure must start at five seconds.");
        notice.Set(2,"failed","not found",204000);
        Check(notice.At(204000).Text.Contains("1") && notice.At(204000).Opacity==1,"Repeated status must not renew the failure countdown.");
        Check(notice.At(205150).Opacity is >0 and <1 && !notice.At(205400).Visible,"A failure fades after five seconds, not at each heartbeat.");
        notice.Set(3,"loading","new load",300000);
        notice.Rendered(3,300001,true);
        Check(notice.At(301000).Opacity==1,"Old visible comments cannot complete a new unarmed load.");
        notice.ContentLoaded(3);notice.Set(4,"matching","new video",302000);
        notice.Rendered(3,302001,true);notice.Set(3,"failed","stale",302002);
        Check(notice.At(302010).Text=="new video","Stale load or failure cannot replace a new media notice.");
        notice.Set(5,"manual","",303000);
        Check(!notice.At(303000).Visible,"Cancellation clears the notice without claiming success.");
        var reduced=new VideoNotice(()=>false);
        reduced.Set(1,"failed","not found",0);
        Check(reduced.At(4999).Visible && !reduced.At(5000).Visible,"Reduced motion retains the five-second message but has no fading tail.");
        reduced.Set(2,"loading","load",6000);reduced.ContentLoaded(2);reduced.Rendered(2,6100,true);
        Check(!reduced.At(6100).Visible,"Reduced motion ends success immediately on the first visible frame.");
        Console.WriteLine("PASS: video notice tickets, empty search visibility, first on-video draw, five-second countdown and stale-result fences.");
    }
    private static void Wait(Func<bool> condition,string message,int timeout=5000)
    {
        var clock=Stopwatch.StartNew();
        while(clock.ElapsedMilliseconds<timeout){Application.DoEvents();if(condition())return;Thread.Sleep(16);}
        throw new Exception(message);
    }
    internal static void RunRuntime()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var work=Screen.PrimaryScreen.WorkingArea;
        using var target=new Target {Text="Controlled notice video",ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None,
            StartPosition=FormStartPosition.Manual,Bounds=new Rectangle(work.Left+40,work.Top+40,640,360),BackColor=Color.FromArgb(30,40,50)};
        target.Show();SetWindowPos(target.Handle,IntPtr.Zero,0,0,0,0,0x1|0x2|0x10);
        var video=NativeWindows.Read(target.Handle);
        using var overlay=new OverlayHost();
        overlay.Update(video,0,true,1,false,0,false);
        overlay.SetStatus(10,"matching","搜索弹幕源（1/3）");
        Wait(()=>overlay.Snapshot().Visible && overlay.NoticeSnapshot().Visible,"Search notice requires no downloaded timeline or synchronized clock.");
        target.WindowState=FormWindowState.Minimized;
        Wait(()=>!overlay.Snapshot().Visible,"A minimized video must hide a search-only notice.");
        target.WindowState=FormWindowState.Normal;
        Wait(()=>overlay.Snapshot().Visible,"A restored video must restore an active search notice.");
        overlay.SetStatus(11,"loading","已发现弹幕，进行装载");
        overlay.Update(video,0,true,2,false,0);
        var timeline=Enumerable.Range(0,200).Select(i=>new Comment(i*50,1,0xffffff,"controlled visible danmaku")).ToArray();
        overlay.SetComments(timeline,10);
        Wait(()=>overlay.Snapshot().ActiveCount>0,"Stale-ticket fixture must contain actual active comments.");
        Thread.Sleep(500);
        Check(overlay.NoticeSnapshot().Opacity==1,"Drawing a previous content ticket cannot end current loading.");
        overlay.SetComments(timeline,11);
        Wait(()=>!overlay.NoticeSnapshot().Visible,"A real visible current-ticket draw must finish the loading fade.");

        using var cover=new Target {ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,
            Bounds=target.Bounds,BackColor=Color.FromArgb(120,50,40)};
        cover.Show();SetWindowPos(cover.Handle,IntPtr.Zero,0,0,0,0,0x1|0x2|0x10);
        overlay.SetStatus(12,"loading","已发现弹幕，进行装载");overlay.SetComments(timeline,12);
        Wait(()=>overlay.Snapshot().ActiveCount>0,"Occlusion fixture must contain actual drawable comments.");
        Thread.Sleep(500);
        Check(overlay.NoticeSnapshot().Opacity==1,"Fully occluded video rendering must not pretend the user saw the first comment.");
        cover.Hide();SetWindowPos(target.Handle,IntPtr.Zero,0,0,0,0,0x1|0x2|0x10);
        Wait(()=>!overlay.NoticeSnapshot().Visible,"Uncovering a current visible comment must complete the loading fade.");
        overlay.Update(video,0,true,14,false,0);
        using(var matcher=new AutomaticMatcher((_,action,id,_)=>Task.FromResult(action switch {
            "search"=>(JsonObject)JsonNode.Parse("{\"animes\":[{\"animeId\":\"a\",\"animeTitle\":\"Notice movie\"},{\"animeId\":\"b\",\"animeTitle\":\"Notice movie\"}]}"),
            "episodes"=>(JsonObject)JsonNode.Parse("{\"bangumi\":{\"episodes\":[{\"episodeId\":\"a1\",\"episodeNumber\":"+(id=="a" ? "1" : "2")+"}]}}"),
            _=>new JsonObject { ["comments"]=new JsonArray(Enumerable.Range(0,200).Select(i=>(JsonNode)new JsonObject {["p"]=(i*.05).ToString(System.Globalization.CultureInfo.InvariantCulture)+",1,16777215",["m"]="actual matcher notice"}).ToArray()) }
        }),result=>overlay.SetComments(result.Comments,14),status=>overlay.SetStatus(14,status.State,status.Message),_=>{}))
        {
            matcher.Observe("http://fixture.invalid/token","Notice movie",1,14);
            Wait(()=>matcher.Completion.IsCompleted,"The real matcher fixture did not finish.");matcher.Completion.GetAwaiter().GetResult();
            Check(matcher.Status.State=="loaded","A usable first source must win over the second source's absent episode.");
            Wait(()=>!overlay.NoticeSnapshot().Visible,"A valid best source followed by an absent episode must still arm the real first-render fade.");
        }
        overlay.SetComments(Array.Empty<Comment>(),15);overlay.SetStatus(15,"failed","未找到弹幕，可手动搜索");
        Wait(()=>overlay.Snapshot().Visible,"Final failure must remain visible with an empty timeline.");
        Wait(()=>!overlay.Snapshot().Visible,"Five-second final failure must hide the empty overlay after fading.",6500);
        Console.WriteLine("PASS: actual WPF video notices without comments, minimize/restore, ticketed first render, complete occlusion and final failure timeout.");
    }
}
