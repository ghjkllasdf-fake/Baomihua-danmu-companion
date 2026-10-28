using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Popcorn;

internal static class RaceSchedule
{
    public static bool PauseStop,PauseStart;
    public static readonly ManualResetEventSlim StopEntered=new(),AllowStop=new(),StartEntered=new(),AllowStart=new();
    public static readonly ManualResetEventSlim ReapEntered=new(),AllowReap=new();
    public static int PauseReap,TrackExitReads,ExitReads;
    public static void AtStop() { if (PauseStop) { StopEntered.Set(); if (!AllowStop.Wait(3000)) throw new Exception("Stop scheduling barrier timed out."); } }
    public static void AtStart() { if (PauseStart) { StartEntered.Set(); if (!AllowStart.Wait(3000)) throw new Exception("Start scheduling barrier timed out."); } }
    public static void AtReapCheck() { if (Interlocked.Exchange(ref PauseReap,0)!=0) { ReapEntered.Set(); if (!AllowReap.Wait(3000)) throw new Exception("Reap scheduling barrier timed out."); } }
    public static bool HasExited(Process process) { if (Volatile.Read(ref TrackExitReads)!=0) Interlocked.Increment(ref ExitReads); return process.HasExited; }
}
public static class ProbeProcessRaceTests
{
    private static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    public static async Task Run(string runtime,string script)
    {
        var child=new ProbeProcess(runtime,script); RaceSchedule.PauseStop=true;
        var stopping=Task.Run(child.Stop);
        try
        {
            Check(RaceSchedule.StopEntered.Wait(3000),"Stop did not reach its real cleanup boundary.");
            var reaping=child.StopAndReapAsync(); await Task.Delay(150);
            Check(!reaping.IsCompleted,"Reap completed while the original Stop had not closed its owned resources.");
            RaceSchedule.AllowStop.Set(); await stopping;
            Check(await reaping,"Reap did not finish after Stop cleanup completed.");
        }
        finally { RaceSchedule.AllowStop.Set(); try { await stopping; } catch { } RaceSchedule.PauseStop=false; }
        var startingChild=new ProbeProcess(runtime,script); RaceSchedule.PauseStart=true;
        var starting=Task.Run(startingChild.Start);
        try
        {
            Check(RaceSchedule.StartEntered.Wait(3000),"Start did not reach its process creation boundary.");
            startingChild.Stop(); var reaping=startingChild.StopAndReapAsync(); await Task.Delay(150);
            Check(!reaping.IsCompleted,"Reap disposed a process whose Start had not returned.");
            RaceSchedule.AllowStart.Set();
            try { await starting; throw new Exception("A stopped launch was allowed to continue."); } catch (OperationCanceledException) { }
            Check(await reaping,"A stopped in-flight launch was not fully reaped.");
        }
        finally { RaceSchedule.AllowStart.Set(); try { await starting; } catch { } RaceSchedule.PauseStart=false; await startingChild.StopAndReapAsync(); }
        RaceSchedule.StartEntered.Reset(); RaceSchedule.AllowStart.Reset(); RaceSchedule.PauseStart=true; RaceSchedule.PauseReap=1;
        var confirming=new ProbeProcess(runtime,script); var launch=Task.Run(confirming.Start);
        try
        {
            Check(RaceSchedule.StartEntered.Wait(3000),"Start did not reach the exit-confirmation scheduling boundary.");
            confirming.Stop(); var reaping=Task.Run(confirming.StopAndReapAsync);
            Check(RaceSchedule.ReapEntered.Wait(3000),"Reap did not reach its real completion check.");
            RaceSchedule.AllowStart.Set();
            try { await launch; throw new Exception("The stopped launch unexpectedly succeeded."); } catch (OperationCanceledException) { }
            Volatile.Write(ref RaceSchedule.TrackExitReads,1); RaceSchedule.AllowReap.Set();
            Check(await reaping,"Stopped child could not be reaped after Start completed.");
            Check(RaceSchedule.ExitReads>0,"Reap reused a pre-launch exited=true without confirming OS exit after Start completed.");
        }
        finally
        {
            RaceSchedule.AllowStart.Set(); RaceSchedule.AllowReap.Set();
            try { await launch; } catch { }
            await confirming.StopAndReapAsync(); RaceSchedule.PauseStart=false;
        }
        Console.WriteLine("PASS: Reap waits for both in-flight Start and the actual completion of Stop cleanup.");
    }
}
