using System;
using System.Threading;
using System.Windows;

namespace Popcorn;

internal static class SeekRecoveryTests
{
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static void Run()
    {
        foreach(bool initiallyPlaying in new[]{true,false})
        foreach(double target in new[]{300000d,1500000d})
        {
            double start=target==300000 ? 1500000 : 300000;
            var sync=new SyncClock();
            sync.Observe(start,initiallyPlaying,100,100,"video#21",200);
            int before=sync.SeekRevision;
            sync.Observe(target,true,300,300,"video#21",200);
            Check(sync.SeekRevision==before+1 && sync.At(300)==target,"A large forward/backward seek must reset once immediately.");
            int revision=sync.SeekRevision;
            for(double stamp=500;stamp<=7500;stamp+=200)
            {
                sync.Observe(target,true,stamp,stamp,"video#21",200);
                Check(sync.SeekRevision==revision,"SMTC Playing at a held seek target must not repeatedly clear comments.");
            }
            Check(!sync.Running,"A held seek target must stop advancing the comment clock.");
            double held=sync.At(7500);
            sync.Observe(target+200,true,7700,7700,"video#21",200);
            Check(sync.Running && sync.SeekRevision==revision && sync.At(7700)>=held,"Buffer recovery must resume without rewind or scene reset.");
            sync.Observe(target+400,null,7900,7900,"video#21",200);
            sync.Observe(target+600,true,8100,8100,"video#21",200);
            Check(sync.SeekRevision==revision,"A brief missing media status must not start another scene epoch.");
            sync.Observe(start,null,8300,8300,"video#21",200);
            Check(sync.SeekRevision==revision+1 && sync.At(8300)==start && !sync.Running,
                "A proven new target must clear old comments while SMTC is still unavailable.");
            sync.Observe(start,true,8500,8500,"video#21",200);
            Check(sync.SeekRevision==revision+1,"The recovered playback status must not clear an already admitted seek twice.");
        }
        VerifySurface();
        Console.WriteLine("PASS: large seeks, held precise progress, incomplete status recovery, and uninterrupted right-to-left overlay motion.");
    }
    private static void VerifySurface()
    {
        var sync=new SyncClock();
        sync.Observe(300000,true,100,100,"video#21",200);
        sync.Observe(1500000,true,300,300,"video#21",200);
        var surface=new OverlaySurface();
        try
        {
            surface.Measure(new Size(1280,720));surface.Arrange(new Rect(0,0,1280,720));
            surface.Load(new[]{new Comment(1500000,1,0xffffff,"跳转后连续滚动")});
            void Publish(double stamp)
            {
                surface.UpdateClock(sync.At(stamp),sync.Running,Monotonic.Now,sync.SeekRevision,false,0);
                surface.Frame();
            }
            // Admit an exactly timed comment while paused, then resume at the
            // same seek target. This keeps fixture admission independent of
            // wall-clock time spent initializing WPF/font resources.
            surface.UpdateClock(1500000,false,Monotonic.Now,sync.SeekRevision,false,0);surface.Frame();
            Publish(300);
            var entered=surface.Snapshot(true);
            Check(entered.ActiveCount==1,"A comment must enter from the new seek target.");
            Thread.Sleep(100);
            var moving=surface.Snapshot(true);
            Check(moving.X[0]<entered.X[0],"The comment must move right to left.");
            for(double stamp=500;stamp<=7500;stamp+=200)
            {
                sync.Observe(1500000,true,stamp,stamp,"video#21",200);
                Publish(stamp);
                Check(surface.Snapshot(true).ActiveCount==1,"Held progress must preserve the admitted comment instead of clearing/recreating it.");
            }
            var held=surface.Snapshot(true);
            Thread.Sleep(100);
            Check(Math.Abs(surface.Snapshot(true).X[0]-held.X[0])<.01,"Buffering must hold the existing comment still.");
            sync.Observe(1500200,true,7700,7700,"video#21",200);Publish(7700);
            var resumed=surface.Snapshot(true);
            Thread.Sleep(100);
            var after=surface.Snapshot(true);
            Check(after.ActiveCount==1 && after.X[0]<resumed.X[0] && resumed.X[0]<=held.X[0],
                "The same comment must continue moving left after buffer recovery without jumping back to the right edge.");
            sync.Observe(300000,null,7900,7900,"video#21",200);Publish(7900);
            Check(surface.Snapshot(true).ActiveCount==0,"A real backward seek must still remove the previous target's scene.");
        }
        finally{surface.Detach();}
    }
}
