using System;

namespace Popcorn;

internal static class PlayerPrivilegeTests
{
    internal static void Run()
    {
        int checks=0;
        foreach(bool? player in new bool?[]{null,false,true})
        foreach(bool? companion in new bool?[]{null,false,true})
        {
            bool expected=player==true && companion==false;
            if(PlayerPrivilege.ShouldWarn(player,companion)!=expected)throw new Exception("Only confirmed elevated-player/non-elevated-companion mismatch may warn.");
            checks++;
        }
        if(PlayerPrivilege.QueryElevation(Environment.ProcessId)==null)throw new Exception("The current process token must be queryable read-only.");
        if(PlayerPrivilege.QueryElevation(-1)!=null)throw new Exception("An unreadable process must remain unknown, not elevated.");
        Console.WriteLine($"PASS: {checks+2} player elevation policy, unknown/failure and read-only current-token checks.");
    }
}
