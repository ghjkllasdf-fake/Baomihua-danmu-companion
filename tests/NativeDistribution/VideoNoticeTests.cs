using System;

namespace Popcorn;

internal static class VideoNoticeTests
{
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
        Console.WriteLine("PASS: video notice tickets, empty search visibility, first on-video draw, five-second countdown and stale-result fences.");
    }
}
