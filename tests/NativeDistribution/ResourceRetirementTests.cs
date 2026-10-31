using System;
using System.Collections.Generic;
using System.IO;

namespace Popcorn;

internal static class ResourceRetirementTests
{
    internal static void Run()
    {
        string fixture=Path.Combine(Path.GetTempPath(),"DanmuResourceRetirement-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        try
        {
            foreach(string mode in new[]{"success","replacement","locked","scope","foreign","retained"})
            {
                string root=Path.Combine(fixture,mode),snapshot=Path.Combine(fixture,mode+"-snapshot");Directory.CreateDirectory(snapshot);
                Directory.CreateDirectory(Path.Combine(root,"de"));Directory.CreateDirectory(Path.Combine(root,"zh-Hans"));
                var old=new Dictionary<string,string>{{"DanmuCompanion.exe","old"},{"de/System.Windows.Forms.resources.dll","old resource"},{"zh-Hans/System.Windows.Forms.resources.dll","Chinese"}};
                UpdateTransactionTests.Payload(root,"0.1.1-beta.6",old);File.Copy(Path.Combine(root,"manifest.json"),Path.Combine(snapshot,"old-manifest.json"));
                string resource=Path.Combine(root,"de","System.Windows.Forms.resources.dll"),identity=UpdateFileIdentity.Read(resource);
                UpdateTransactionTests.Payload(root,"0.1.1-beta.7",new Dictionary<string,string>{{"DanmuCompanion.exe","new"},{"zh-Hans/System.Windows.Forms.resources.dll","Chinese"}});
                File.WriteAllText(Path.Combine(root,"de","personal.txt"),"foreign sentinel");
                File.WriteAllText(Path.Combine(snapshot,"scope.txt"),"retired-resources-v1|"+root+"\r\nde\\System.Windows.Forms.resources.dll|"+identity+"\r\n");
                if(mode=="replacement")
                {
                    string replacement=resource+".new";File.WriteAllText(replacement,"old resource");File.Move(replacement,resource,true);
                    DistributionTests.Check(ResourceRetirement.Apply(root,snapshot)==2 && File.Exists(resource),"An identical-content foreign replacement must be preserved by disk identity.");
                }
                else if(mode=="locked")
                {
                    using(var file=new FileStream(resource,FileMode.Open,FileAccess.Read,FileShare.None))
                        DistributionTests.Check(ResourceRetirement.Apply(root,snapshot)==2 && File.Exists(resource),"Locked old resources must be preserved without failing the installed program.");
                    DistributionTests.Check(ResourceRetirement.Apply(root,snapshot)==0 && !File.Exists(resource),"Unlocked owned resources must be safely retryable.");
                }
                else if(mode is "scope" or "foreign" or "retained")
                {
                    string relative=mode=="foreign" ? "de\\personal.resources.dll" : mode=="retained" ? "zh-Hans\\System.Windows.Forms.resources.dll" : "..\\personal.resources.dll";
                    File.WriteAllText(Path.Combine(snapshot,"scope.txt"),"retired-resources-v1|"+root+"\r\n"+relative+"|"+identity+"\r\n");
                    UpdatePackageTests.Reject(()=>ResourceRetirement.Apply(root,snapshot),"Malformed, unowned or retained language records must be rejected before deleting files.");
                    DistributionTests.Check(File.Exists(resource),"Rejected resource cleanup must preserve the existing file.");
                }
                else DistributionTests.Check(ResourceRetirement.Apply(root,snapshot)==0 && !File.Exists(resource),"A verified retired old resource must be removed.");
                DistributionTests.Check(File.ReadAllText(Path.Combine(root,"de","personal.txt"))=="foreign sentinel" && File.ReadAllText(Path.Combine(root,"zh-Hans","System.Windows.Forms.resources.dll"))=="Chinese" && UpdatePackage.Read(root).Version=="0.1.1-beta.7","Resource cleanup must preserve foreign files, retained Chinese resources and the new program.");
            }
        }
        finally{UpdatePackage.Check(Path.GetFullPath(fixture).StartsWith(Path.Combine(Path.GetTempPath(),"DanmuResourceRetirement-"),StringComparison.OrdinalIgnoreCase),"Unsafe resource fixture cleanup.");Directory.Delete(fixture,true);}
        Console.WriteLine("PASS: retired resource ownership/hash/identity, identical foreign replacements, locks, safe retry, scope rejection and retained language protection.");
    }
}
