using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Popcorn;

internal static class NativeSandboxTests
{
    internal static void Run()
    {
        string folder=Path.Combine(Path.GetTempPath(),"DanmuNativeSandbox-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder,"nested"));
        string file=Path.Combine(folder,"nested","controlled.dll");File.WriteAllText(file,"never executed");
        try
        {
            var before=new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).Count;
            NativeRuntime.GrantBrowserReadAccess(folder);
            var rules=new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier));
            DistributionTests.Check(rules.Count>=before,"Sandbox preparation must preserve existing folder permissions.");
            foreach(string sid in new[]{"S-1-15-2-1","S-1-15-2-2"})
            {
                var matching=rules.OfType<FileSystemAccessRule>().Where(rule=>rule.IdentityReference.Value==sid && rule.AccessControlType==AccessControlType.Allow).ToArray();
                DistributionTests.Check(matching.Any(rule=>(rule.FileSystemRights&FileSystemRights.ReadAndExecute)==FileSystemRights.ReadAndExecute),"Both AppContainer groups require private browser read/execute access.");
                DistributionTests.Check(matching.All(rule=>(rule.FileSystemRights&FileSystemRights.Write)==0),"Browser sandbox permission must not grant write access.");
                DistributionTests.Check(new FileInfo(file).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                    .Any(rule=>rule.IdentityReference.Value==sid && rule.IsInherited && (rule.FileSystemRights&FileSystemRights.ReadAndExecute)==FileSystemRights.ReadAndExecute),
                    "Private browser descendants must inherit sandbox read access.");
            }
            NativeRuntime.GrantBrowserReadAccess(folder);
            DistributionTests.Check(new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier)).Count==rules.Count,
                "Repeated sandbox preparation must not add duplicate rules.");
        }
        finally{Directory.Delete(folder,true);}
        Console.WriteLine("PASS: private browser-only sandbox RX permissions, inheritance, no write grant and idempotency.");
    }
}
