using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Popcorn;

// Interactive regression: both HWNDs are test-owned, and the video is in a separate process.
internal static class VideoCompanionTests
{
    private const uint NoActivate=0x0010, NoSize=0x0001, NoMove=0x0002;
    private const uint NoOwnerZOrder=0x0200, NoSendChanging=0x0400;
    private const uint WmClose=0x0010, NextWindow=2;
    private const int ShowNoActivate=4, MinimizeNoActivate=7;

    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);

    private sealed class TestWindow : Form
    {
        protected override bool ShowWithoutActivation=>true;
        protected override CreateParams CreateParams
        {
            get {var parameters=base.CreateParams;parameters.ExStyle|=0x08000000|0x80;return parameters;}
        }
    }

    // The test executable dispatches this argument before the ordinary suite starts.
    internal static int RunTarget()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Rectangle work=Screen.PrimaryScreen.WorkingArea;
        using var video=new TestWindow {
            Text="Danmu owned regression video",ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None,
            StartPosition=FormStartPosition.Manual,BackColor=Color.FromArgb(32,42,52),
            Bounds=new Rectangle(work.Left+24,work.Top+24,Math.Min(640,work.Width-48),Math.Min(360,work.Height-48))
        };
        video.Shown+=(_,_)=>{Console.WriteLine(video.Handle.ToInt64());Console.Out.Flush();};
        Application.Run(video);
        return 0;
    }

    private static void WaitFor(Func<bool> condition,string message,int timeoutMs=5000)
    {
        var clock=Stopwatch.StartNew();
        while(clock.ElapsedMilliseconds<timeoutMs)
        {
            Application.DoEvents();
            if(condition())return;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException(message);
    }

    private static void Assert(List<string> failures,bool condition,string message)
    {
        if(!condition)failures.Add(message);
    }

    private static bool IsAbove(IntPtr above,IntPtr below)
    {
        IntPtr cursor=above;
        for(int count=0;cursor!=IntPtr.Zero && count<10000;count++)
        {
            if(cursor==below)return true;
            IntPtr next=GetWindow(cursor,NextWindow);
            if(next==cursor)break;
            cursor=next;
        }
        return false;
    }

    private static IntPtr HandleOf(OverlayHost overlay)
    {
        var windowField=typeof(OverlayHost).GetField("window",BindingFlags.Instance|BindingFlags.NonPublic);
        object window=windowField?.GetValue(overlay)??throw new InvalidOperationException("Overlay test could not observe its native window.");
        var handleField=window.GetType().GetField("handle",BindingFlags.Instance|BindingFlags.NonPublic);
        return handleField?.GetValue(window) is IntPtr handle && handle!=IntPtr.Zero ? handle :
            throw new InvalidOperationException("Overlay test could not observe its native HWND.");
    }

    private static void RaiseWithoutActivation(IntPtr handle)
    {
        if(!SetWindowPos(handle,IntPtr.Zero,0,0,0,0,NoActivate|NoSize|NoMove|NoOwnerZOrder|NoSendChanging))
            throw new InvalidOperationException("Could not position a controlled regression window.");
    }

    private static void SetTopmostWithoutActivation(IntPtr handle,bool topmost)
    {
        if(!SetWindowPos(handle,new IntPtr(topmost ? -1 : -2),0,0,0,0,NoActivate|NoSize|NoMove|NoOwnerZOrder|NoSendChanging))
            throw new InvalidOperationException("Could not change the controlled regression window's z-order band.");
    }

    internal static void Run()
    {
        if(!Environment.UserInteractive)throw new InvalidOperationException("Video companion regression requires an interactive desktop; it was not run.");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        string executable=Environment.ProcessPath??throw new InvalidOperationException("Cannot locate the regression test executable.");
        var start=new ProcessStartInfo(executable) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        };
        start.ArgumentList.Add("--video-companion-target");
        using var process=Process.Start(start)??throw new InvalidOperationException("Could not start the controlled video process.");
        IntPtr target=IntPtr.Zero;
        OverlayHost overlay=null;
        TestWindow occluder=null;
        var failures=new List<string>();
        try
        {
            var line=process.StandardOutput.ReadLineAsync();
            if(!line.Wait(TimeSpan.FromSeconds(10)) || !long.TryParse(line.Result,out long raw) || raw==0)
                throw new InvalidOperationException("The controlled video process did not provide its HWND.");
            target=new IntPtr(raw);
            GetWindowThreadProcessId(target,out uint targetPid);
            if(targetPid!=(uint)process.Id || targetPid==(uint)Environment.ProcessId)
                throw new InvalidOperationException("Video HWND is not owned by the controlled separate process.");
            WaitFor(()=>IsWindow(target) && NativeWindows.IsWindowVisible(target),"Controlled video HWND did not become visible.");
            var video=NativeWindows.Read(target)??throw new InvalidOperationException("Could not read the controlled video client bounds.");
            if(video.Width<300 || video.Height<150)throw new InvalidOperationException("Controlled video client bounds are too small for the regression.");
            if(video.ProcessId!=targetPid)throw new InvalidOperationException("Video snapshot must carry the PID sampled from its HWND.");
            if(NativeWindows.HasExtendedStyles(target,0x8))throw new InvalidOperationException("Controlled video must be an ordinary non-topmost window.");
            RaiseWithoutActivation(target);

            overlay=new OverlayHost();
            overlay.SetComments(new[]{new Comment(1000,1,0xffffff,"controlled overlay test")});
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>overlay.Snapshot().Visible,"Overlay never became visible for the controlled target.");
            IntPtr overlayHwnd=HandleOf(overlay);
            var initial=overlay.Snapshot();
            Assert(failures,initial.ClickThrough && initial.NoActivate,"Visible overlay must remain click-through and no-activate.");
            Assert(failures,initial.OverlayXpx==video.X && initial.OverlayYpx==video.Y,"Overlay origin must match the video client origin.");
            Assert(failures,!NativeWindows.HasExtendedStyles(overlayHwnd,0x8),"A normal video must not create a globally topmost overlay.");
            Assert(failures,IsAbove(overlayHwnd,target),"The overlay must be above its own video.");

            uint wrongPid=targetPid==uint.MaxValue ? 1 : targetPid+1;
            overlay.Update(video with { ProcessId=wrongPid },2000,false,1,false,0);
            WaitFor(()=>!overlay.Snapshot().Visible,"A stale HWND snapshot with a different sampled process must hide its overlay.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>overlay.Snapshot().Visible,"The same HWND with a correct sampled process must recover from a stale identity.");
            overlay.Update(video with { ProcessId=0 },2000,false,1,false,0);
            WaitFor(()=>!overlay.Snapshot().Visible,"A snapshot without a sampled process must not attach an overlay.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>overlay.Snapshot().Visible,"The correct sampled identity must recover after an untrusted snapshot.");

            occluder=new TestWindow {
                Text="Danmu owned regression occluder",ShowInTaskbar=false,FormBorderStyle=FormBorderStyle.None,
                StartPosition=FormStartPosition.Manual,BackColor=Color.FromArgb(176,65,58),
                Bounds=new Rectangle(video.X+30,video.Y+30,Math.Min(220,video.Width-60),Math.Min(140,video.Height-60))
            };
            occluder.Show();
            RaiseWithoutActivation(occluder.Handle);
            WaitFor(()=>IsAbove(occluder.Handle,target),"The controlled occluder was not placed above the video.");
            var settle=Stopwatch.StartNew();
            while(settle.ElapsedMilliseconds<800)
            {
                Application.DoEvents();
                overlay.Update(video,2000,false,1,false,0);
                Thread.Sleep(40);
            }
            Assert(failures,overlay.Snapshot().Visible,"The uncovered portion of a background video must retain its overlay.");
            Assert(failures,IsAbove(occluder.Handle,target),"The independent occluder must remain above the target.");
            Assert(failures,IsAbove(occluder.Handle,overlayHwnd),"An ordinary window above the video must also be above its overlay.");
            Assert(failures,IsAbove(overlayHwnd,target),"While occluded, the overlay must remain above its own video.");

            SetTopmostWithoutActivation(target,true);
            WaitFor(()=>NativeWindows.HasExtendedStyles(target,0x8),"The controlled video did not enter the topmost band.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>NativeWindows.HasExtendedStyles(overlayHwnd,0x8) && IsAbove(overlayHwnd,target),
                "A topmost video must retain a topmost companion above it.");
            SetTopmostWithoutActivation(occluder.Handle,true);
            WaitFor(()=>IsAbove(occluder.Handle,target),"The topmost occluder was not placed above the video.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>IsAbove(occluder.Handle,overlayHwnd) && IsAbove(overlayHwnd,target),
                "An independent topmost occluder must cover the topmost companion as well as its video.");
            SetTopmostWithoutActivation(target,false);
            WaitFor(()=>!NativeWindows.HasExtendedStyles(target,0x8),"The controlled video did not leave the topmost band.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>!NativeWindows.HasExtendedStyles(overlayHwnd,0x8) && IsAbove(overlayHwnd,target) && IsAbove(occluder.Handle,overlayHwnd),
                "The companion must leave the topmost band with its video and remain below an independent topmost occluder.");

            ShowWindowAsync(target,MinimizeNoActivate);
            WaitFor(()=>NativeWindows.IsIconic(target),"Controlled video could not be minimized.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>!overlay.Snapshot().Visible,"Minimized video retained a visible overlay.");

            ShowWindowAsync(target,ShowNoActivate);
            WaitFor(()=>!NativeWindows.IsIconic(target) && NativeWindows.IsWindowVisible(target),"Controlled video could not be restored.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>overlay.Snapshot().Visible,"Overlay did not return after the video was restored.");
            Assert(failures,IsAbove(overlayHwnd,target),"The restored overlay must remain above its video.");
            // Windows can legitimately raise the restored target itself above the
            // occluder. Re-establish the controlled overlapping order before
            // asserting that the companion is not separately raised above it.
            RaiseWithoutActivation(occluder.Handle);
            WaitFor(()=>IsAbove(occluder.Handle,target),"Could not re-establish occluder above the restored video.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>IsAbove(occluder.Handle,overlayHwnd) && IsAbove(overlayHwnd,target),
                "Restored overlay did not settle between the video and the independent occluder.");
            Thread.Sleep(200);
            Assert(failures,IsAbove(occluder.Handle,target) && IsAbove(occluder.Handle,overlayHwnd) && IsAbove(overlayHwnd,target),
                "Restored companion must remain above its video and below the occluder.");

            PostMessage(target,WmClose,IntPtr.Zero,IntPtr.Zero);
            WaitFor(()=>!IsWindow(target),"Controlled video did not close.");
            overlay.Update(video,2000,false,1,false,0);
            WaitFor(()=>!overlay.Snapshot().Visible,"Destroyed video left its overlay visible.");
            if(!process.WaitForExit(5000))throw new InvalidOperationException("Controlled video process did not exit after its window closed.");

            if(failures.Count>0)throw new InvalidOperationException("Video companion regression failed:\n- "+string.Join("\n- ",failures));
            Console.WriteLine("PASS: cross-process video/overlay z-order, independent occlusion, minimize, restore and destruction.");
        }
        finally
        {
            if(occluder!=null){occluder.Close();occluder.Dispose();}
            overlay?.Dispose();
            if(target!=IntPtr.Zero && IsWindow(target))PostMessage(target,WmClose,IntPtr.Zero,IntPtr.Zero);
            if(!process.HasExited && !process.WaitForExit(2000))
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
    }
}
