using System;
using System.IO;
using System.Threading.Tasks;

namespace Popcorn;

internal static class NativeInstanceTests
{
    internal static void Run()
    {
        string data=Path.Combine(Path.GetTempPath(),"DanmuNativeInstance-"+Guid.NewGuid().ToString("N"));
        var firstPaths=new ApplicationPaths(Path.GetTempPath(),"instance-test",data);
        using(var first=new ApplicationInstance(firstPaths))
        {
            DistributionTests.Check(first.Acquired,"First instance must own its profile lock.");
            Task.Run(()=>
            {
                using var second=new ApplicationInstance(new ApplicationPaths(Path.GetPathRoot(data),"instance-test",data));
                DistributionTests.Check(!second.Acquired,"Installer and portable copies must not concurrently write the same user profile.");
                using var isolated=new ApplicationInstance(new ApplicationPaths(Path.GetTempPath(),"another-profile",data));
                DistributionTests.Check(isolated.Acquired,"Isolated test profiles must not block the user's application.");
            }).GetAwaiter().GetResult();
        }
        using var restarted=new ApplicationInstance(firstPaths);
        DistributionTests.Check(restarted.Acquired,"Normal exit must release the profile lock.");
        Console.WriteLine("PASS: cross-copy profile ownership, isolated profiles and restart after release.");
    }
}
